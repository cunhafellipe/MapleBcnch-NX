using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>Source locations come only from operator-controlled configuration.</summary>
[McpServerToolType]
public static class SourceTools
{
    public sealed class Source
    {
        public string Path { get; set; } = "";
        public string? MapleVersion { get; set; }
        public string? Iv { get; set; }
        public short GameVersion { get; set; } = -1;
        public bool ReadOnly { get; set; } = true;
    }

    private static Dictionary<string, Source> Load()
    {
        var path = Environment.GetEnvironmentVariable("MAPLEBENCH_SOURCES");
        if (string.IsNullOrWhiteSpace(path)) throw new McpException("Set MAPLEBENCH_SOURCES to a trusted source JSON configuration.");
        var sources = JsonSerializer.Deserialize<Dictionary<string, Source>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (sources is null) throw new McpException("Source configuration is empty.");
        foreach (var source in sources.Values)
            if (!System.IO.Path.IsPathFullyQualified(source.Path)) throw new McpException("Configured source paths must be absolute.");
        return sources;
    }

    [McpServerTool(Name = "wz_asset_sources"), Description("List trusted source aliases configured by MAPLEBENCH_SOURCES. Existence is not proof of encryption compatibility.")]
    public static string ListSources() => JsonSerializer.Serialize(Load().Select(pair => new
    {
        alias = pair.Key, path = pair.Value.Path, pair.Value.MapleVersion,
        pair.Value.ReadOnly, exists = File.Exists(pair.Value.Path) || Directory.Exists(pair.Value.Path),
        encryptionVerified = false
    }));

    [McpServerTool(Name = "wz_asset_open_source"), Description("Open a trusted source alias, optionally selecting a standalone IMG filename within a configured folder. Rejects traversal and ambiguous filenames; candidateIndex explicitly chooses a listed candidate. Split-WZ image lookup is NOT supported yet. Profiles must be independently verified.")]
    public static async Task<string> OpenSource(MapClient client, string source, CancellationToken ct,
        string? imgName = null, int? candidateIndex = null, bool commit = false)
    {
        var sources = Load();
        if (!sources.TryGetValue(source, out var configured)) throw new McpException("Unknown source alias.");
        string path = configured.Path;
        if (Directory.Exists(path))
        {
            if (string.IsNullOrWhiteSpace(imgName) || imgName.Contains('/') || imgName.Contains('\\') || imgName.Contains(':') || imgName is "." or ".." || !imgName.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                throw new McpException("Folder sources require a plain IMG filename, not a relative path.");
            var candidates = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(p => string.Equals(System.IO.Path.GetFileName(p), imgName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal).Take(257).ToArray();
            if (candidates.Length == 0) throw new McpException("No standalone IMG found. Split-WZ lookup is not implemented.");
            if (candidates.Length > 256) throw new McpException("Too many matches; configure a narrower source folder.");
            if (candidates.Length > 1 && candidateIndex is null)
                throw new McpException("Ambiguous IMG; provide candidateIndex from: " + JsonSerializer.Serialize(candidates.Select((p, i) => new { candidateIndex = i, path = p })));
            int index = candidateIndex ?? 0;
            if (index < 0 || index >= candidates.Length) throw new McpException("candidateIndex out of range.");
            path = candidates[index];
        }
        else
        {
            if (imgName is not null || candidateIndex is not null) throw new McpException("File aliases open their configured file; IMG selection inside WZ is not yet supported.");
            if (!File.Exists(path)) throw new McpException("Configured source does not exist.");
        }
        var request = new { path, configured.MapleVersion, configured.Iv, configured.GameVersion, configured.ReadOnly };
        return commit ? MapClient.J(await client.PostAsync("api/files/open", request, ct))
            : JsonSerializer.Serialize(new { committed = false, request, encryptionVerified = false });
    }
}
