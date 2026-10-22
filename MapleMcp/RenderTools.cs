using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// Renders things to PNG on disk, because every other tool in this server
/// answers with JSON and JSON cannot show a sprite.
/// </summary>
/// <remarks>
/// <para>
/// The gap this fills: <c>wz_map_cast</c> returns footholds, a character track,
/// portals and spawns — everything needed to draw a map, as numbers. An agent
/// holding that still cannot answer "does the map look right", and
/// <c>wz_asset_export_canvas</c> only exports one node that already IS a canvas.
/// A v83 map is tiles and objects under numbered layers, so nothing under it is
/// a single canvas node to export.
/// </para>
/// <para>
/// <b>It writes to disk and says where.</b> The bytes are not returned inline:
/// an MCP tool that answered with base64 PNG would blow the context of the very
/// agent it was built for. The caller gets a path it can open, measure, or hand
/// to <c>vision_analyze</c>.
/// </para>
/// <para>
/// Rule 2.1 of 工作室守則.md applies here and is why this is a tool and not a
/// habit: a composed picture proves nothing on its own. What counts is
/// <see cref="RenderMobSprite"/> writing out the mob's own frames — the claim
/// "the walker draws this creature" is checkable against the archive, without
/// believing any canvas.
/// </para>
/// </remarks>
[McpServerToolType]
public static class RenderTools
{
    /// <summary>
    /// Writes one PNG of a map: every tile layer, then the footholds as lines,
    /// then the character's walked track. Read-only against the archive.
    /// </summary>
    [McpServerTool(Name = "wz_map_render")]
    [Description("Render a map to a PNG on disk: tile layers, foothold lines, and the character's track. Use when a map's numbers are right but you cannot tell whether it LOOKS right. Returns the output path and what was drawn. Read-only.")]
    public static async Task<string> RenderMap(
        MapClient client,
        [Description("Map address, e.g. f1/Map/Map1/100000004.img")]
        string path,
        [Description("Absolute output path for the PNG. Must end in .png")]
        string outputPath,
        [Description("Frames to walk for the character track. 0 draws no track.")]
        int steps = 400,
        [Description("Seed for the walk")]
        int seed = 1,
        CancellationToken ct = default)
    {
        // Open it first, through the MCP tool that already does it rather than
        // by hand: /api/mapedit/open takes a BODY (MapOpenRequest), so the
        // query-string form this used to send came back as "No open file with
        // id ''" — an error about archives that has nothing to do with maps.
        await MapTools.Open(client, path, ct);

        JsonNode? cast = await client.GetAsync(
            $"/api/simulate/cast?path={MapClient.Q(path)}&seed={seed}&steps={steps}", ct);

        // One HTTP GET per tile is what the browser does; doing it here means the
        // picture and the numbers come from the same backend, not two code paths
        // that can drift apart.
        // The map's own archive id is the FIRST segment of its address, which is
        // the whole point of the spelling: f7/Map/Map0/000040001.img names the
        // archive, so nothing has to be resolved by name and nothing goes stale
        // when a session opens the archives in a different order.
        string mapFileId = path.Split('/', 2)[0];

        int drawn = await MapPainter.PaintToFile(client, path, mapFileId, cast, outputPath, ct);

        return JsonSerializer.Serialize(new
        {
            path,
            outputPath,
            drawn,
            footholds = cast?["footholds"]?.GetValue<int>() ?? 0,
            trackPoints = cast?["track"] is JsonArray ta ? ta.Count : 0,
            spawns = cast?["spawns"] is JsonArray sa ? sa.Count : 0,
            verdict = cast?["characterVerdict"]?.ToString(),
        });
    }

    /// <summary>
    /// Writes out every frame of one mob or NPC stance, at the frame's own size,
    /// plus the numbers you need to judge the draw: origin, delay, and the
    /// mirror error a wrong pivot would cause.
    /// </summary>
    [McpServerTool(Name = "wz_map_render_mob")]
    [Description("Export every frame of one mob/NPC stance to PNGs and report each frame's origin, delay and canvas size. Use to check that a mob's art loads and that mirroring pivots on the frame's own origin rather than its corner. Read-only.")]
    public static async Task<string> RenderMobSprite(
        MapClient client,
        [Description("Mob image name, e.g. 2220100.img (not the full node path)")]
        string mobId,
        [Description("Stance: stand, move, hit1, die1, jump. Default stands.")]
        string stance = "stand",
        [Description("Directory to write PNGs into. Created if missing.")]
        string outputDir = "",
        [Description("Archive id holding the art. Default: search the open archives.")]
        string fileId = "",
        CancellationToken ct = default)
    {
        string dir = string.IsNullOrWhiteSpace(outputDir)
            ? Path.Combine(Path.GetTempPath(), "maplebench-mob")
            : outputDir;
        Directory.CreateDirectory(dir);

        string fid = fileId;
        if (string.IsNullOrWhiteSpace(fid))
        {
            JsonNode? files = await client.GetAsync("/api/files", ct);
            JsonNode? mobFile = FindMobArchive(files);
            fid = mobFile?.ToString() ?? "";
            if (string.IsNullOrEmpty(fid))
                return JsonSerializer.Serialize(new { error = "no open archive looks like Mob.nx" });
        }

        JsonNode? node = await client.GetAsync($"/api/children?path={fid}/{mobId}/{stance}", ct);
        if (node is not JsonArray frames || frames.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"no frames under {mobId}.img/{stance}",
                available = await StanceNames(client, fid, mobId, ct),
            });
        }

        var report = new System.Collections.Generic.List<object>();
        foreach (JsonNode? frame in frames)
        {
            string name = frame?["name"]?.ToString() ?? "?";
            string addr = $"{fid}/{mobId}/{stance}/{name}";

            string png = Path.Combine(dir, $"{mobId}-{stance}-{name}.png");
            bool ok;
            try
            {
                await client.DownloadAsync($"/api/canvas?path={MapClient.Q(addr)}&v=r", png, ct);
                ok = File.Exists(png) && new FileInfo(png).Length > 0;
            }
            catch
            {
                ok = false;
            }

            JsonNode? meta = await client.GetAsync($"/api/children?path={MapClient.Q(addr)}", ct);
            int ox = 0, oy = 0, delay = 0;
            if (meta is JsonArray kids)
            {
                foreach (JsonNode? k in kids)
                {
                    switch (k?["name"]?.ToString())
                    {
                        case "origin":
                            ox = k["extra"]?["x"]?.GetValue<int>() ?? 0;
                            oy = k["extra"]?["y"]?.GetValue<int>() ?? 0;
                            break;
                        case "delay":
                            delay = int.TryParse(k["value"]?.ToString(), out int d) ? d : 0;
                            break;
                    }
                }
            }

            // The number that settles a mirroring argument: pivoting on the image
            // corner puts the origin 2*originX away from where the mob stands.
            report.Add(new
            {
                frame = name,
                png = ok ? png : null,
                originX = ox,
                originY = oy,
                delay,
                cornerPivotErrorPx = -2 * ox,
            });
        }

        return JsonSerializer.Serialize(new
        {
            mobId,
            stance,
            outputDir = dir,
            frames = report,
            note = "cornerPivotErrorPx is what mirroring on the image corner would cost; " +
                   "pivoting on the frame's own origin makes it 0.",
        });
    }

    private static JsonNode? FindMobArchive(JsonNode? files)
    {
        if (files is not JsonArray arr) return null;
        foreach (JsonNode? f in arr)
        {
            // A null element has no "name", so it can never be the Mob archive. Saying
            // that outright is what the compiler needed: with `f?["name"]` the flow
            // analysis could not prove `f` was non-null at the `f["id"]` below and
            // reported CS8602, even though `f?["name"]` returning null already implies
            // `f` was null. Written as an explicit null check the fact is visible, so
            // no suppression is needed. Behaviour is unchanged either way: a null
            // element fell through the `n != null` test before.
            if (f is null) continue;

            string? n = f["name"]?.ToString();
            if (n != null && n.StartsWith("Mob", StringComparison.OrdinalIgnoreCase))
                return f["id"];
        }
        return null;
    }

    private static async Task<List<string>> StanceNames(MapClient client, string fid, string mobId, CancellationToken ct)
    {
        JsonNode? node = await client.GetAsync($"/api/children?path={fid}/{mobId}", ct);
        var names = new List<string>();
        if (node is JsonArray arr)
            foreach (JsonNode? n in arr) names.Add(n?["name"]?.ToString() ?? "?");
        return names;
    }
}