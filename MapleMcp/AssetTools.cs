using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>Generic archive operations, independent of the currently selected map.</summary>
[McpServerToolType]
public static class AssetTools
{
    private static string Preview(string route, object body) => JsonSerializer.Serialize(new
    {
        committed = false, route, request = body,
        note = "Preview only: no backend request, no validation and no changes."
    });

    [McpServerTool(Name = "wz_asset_export_img"), Description("Export a single image subtree to a new standalone candidate IMG. Parent directory must exist. Refuses overwrite. Does not synchronize Data or publish NX.")]
    public static async Task<string> ExportImg(MapClient client, string path, string outputPath, CancellationToken ct, bool commit = false)
    {
        if (!outputPath.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) throw new ModelContextProtocol.McpException("IMG output must end in .img.");
        return commit ? JsonSerializer.Serialize(await client.DownloadAsync("api/export/img?path=" + MapClient.Q(path), outputPath, ct)) : Preview("export/img", new { path, outputPath });
    }

    [McpServerTool(Name = "wz_asset_export_canvas"), Description("Decode and export a canvas to a new candidate PNG. Never overwrites existing files. Does not verify pixels against a reference.")]
    public static async Task<string> ExportCanvas(MapClient client, string path, string outputPath, CancellationToken ct, bool commit = false)
    {
        if (!outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ModelContextProtocol.McpException("Canvas output must end in .png.");
        return commit ? JsonSerializer.Serialize(await client.DownloadAsync("api/canvas?path=" + MapClient.Q(path), outputPath, ct)) : Preview("canvas export", new { path, outputPath });
    }

    [McpServerTool(Name = "wz_asset_replace_canvas"), Description("Upload a local image into an existing canvas, in memory only. Backend validates image dimensions and format. Inspect origin/links and save a candidate afterwards; not a publication.")]
    public static async Task<string> ReplaceCanvas(MapClient client, string path, string inputPath, CancellationToken ct, bool commit = false) =>
        commit ? MapClient.J(await client.UploadCanvasAsync(path, inputPath, ct)) : Preview("canvas upload", new { path, inputPath });

    [McpServerTool(Name = "wz_asset_files"), Description("List loaded WZ/IMG/NX archives. Reuse returned file ids; never guess paths.")]
    public static async Task<string> Files(MapClient client, CancellationToken ct) =>
        MapClient.J(await client.GetAsync("api/files", ct));

    [McpServerTool(Name = "wz_asset_open"), Description("Open a WZ, standalone IMG or NX. Reference sources default to read-only. Encryption profiles are MapleBench profiles, NOT Orz key objects. Preview does not verify encryption.")]
    public static async Task<string> Open(MapClient client, string path, CancellationToken ct,
        string? mapleVersion = null, string? iv = null, short gameVersion = -1,
        bool readOnly = true, bool commit = false)
    {
        var body = new { path, mapleVersion, iv, gameVersion, readOnly };
        return commit ? MapClient.J(await client.PostAsync("api/files/open", body, ct)) : Preview("api/files/open", body);
    }

    [McpServerTool(Name = "wz_asset_close"), Description("Unload only the specified archive. Inspect wz_asset_changes first; closing may discard unsaved work. Never unload user-owned archives without approval.")]
    public static async Task<string> Close(MapClient client, string fileId, CancellationToken ct, bool commit = false) =>
        commit ? MapClient.J(await client.DeleteAsync("api/files/" + MapClient.Q(fileId), ct)) : Preview("close", new { fileId });

    [McpServerTool(Name = "wz_asset_node"), Description("Read node metadata at a backend path such as f1/UIWindow.img/BtUIClose. Use returned children to locate scalar leaves.")]
    public static async Task<string> Node(MapClient client, string path, CancellationToken ct) =>
        MapClient.J(await client.GetAsync("api/node?path=" + MapClient.Q(path), ct));

    [McpServerTool(Name = "wz_asset_children"), Description("List children of a loaded archive node.")]
    public static async Task<string> Children(MapClient client, string path, CancellationToken ct) =>
        MapClient.J(await client.GetAsync("api/children?path=" + MapClient.Q(path), ct));

    [McpServerTool(Name = "wz_asset_search"), Description("Bounded search across archive names or values. Search may parse images; scope to the smallest subtree.")]
    public static async Task<string> Search(MapClient client, string query, CancellationToken ct,
        string? path = null, bool matchValues = false, bool regex = false, int limit = 100)
    {
        if (limit < 1 || limit > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        return MapClient.J(await client.PostAsync("api/search", new { path, query, matchNames = true, matchValues, regex, limit }, ct));
    }

    [McpServerTool(Name = "wz_asset_tree"), Description("Export node tree as JSON. Depth is bounded to avoid returning a whole archive.")]
    public static async Task<string> Tree(MapClient client, string path, CancellationToken ct, int depth = 2)
    {
        if (depth < 0 || depth > 8) throw new ArgumentOutOfRangeException(nameof(depth));
        return MapClient.J(await client.GetAsync($"api/export/json?path={MapClient.Q(path)}&depth={depth}", ct));
    }

    [McpServerTool(Name = "wz_asset_set_value"), Description("Change an existing scalar leaf with compatible type. Containers and binary payloads cannot be edited this way. In-memory only until save.")]
    public static async Task<string> SetValue(MapClient client, string path, string value, CancellationToken ct, bool commit = false)
    {
        var body = new { path, value };
        return commit ? MapClient.J(await client.PutAsync("api/node/value", body, ct)) : Preview("api/node/value", body);
    }

    [McpServerTool(Name = "wz_asset_set_values"), Description("Set the SAME scalar value on 1–300 existing leaves using the backend bulk operation. Not a batch of different values. Preview does not validate types.")]
    public static async Task<string> SetValues(MapClient client, string[] paths, string value, CancellationToken ct, bool commit = false)
    {
        if (paths.Length == 0 || paths.Length > 300) throw new ModelContextProtocol.McpException("Provide 1–300 paths.");
        var body = new { paths, value };
        return commit ? MapClient.J(await client.PutAsync("api/node/values", body, ct)) : Preview("api/node/values", body);
    }

    [McpServerTool(Name = "wz_asset_rename"), Description("Rename a node in memory; inspect references before renaming.")]
    public static async Task<string> Rename(MapClient client, string path, string name, CancellationToken ct, bool commit = false)
    {
        var body = new { path, name };
        return commit ? MapClient.J(await client.PutAsync("api/node/name", body, ct)) : Preview("api/node/name", body);
    }

    [McpServerTool(Name = "wz_asset_add"), Description("Create a child. Type is a backend WzPropertyType, Image or Directory; not arbitrary JSON.")]
    public static async Task<string> Add(MapClient client, string path, string type, string name, CancellationToken ct, string? value = null, bool commit = false)
    {
        var body = new { path, type, name, value };
        return commit ? MapClient.J(await client.PostAsync("api/node", body, ct)) : Preview("api/node", body);
    }

    [McpServerTool(Name = "wz_asset_transfer"), Description("Copy or move explicit subtrees across loaded archives. Default copy, no overwrite (backend may auto-rename conflicts). Verify resulting names and non-target content. Not an Orz SKIP merge.")]
    public static async Task<string> Transfer(MapClient client, string[] paths, string targetPath, CancellationToken ct,
        bool move = false, bool overwrite = false, bool commit = false)
    {
        if (paths.Length == 0 || paths.Length > 300) throw new ArgumentException("Provide 1–300 source paths.");
        var body = new { paths, targetPath, move, overwrite };
        return commit ? MapClient.J(await client.PostAsync("api/node/transfer", body, ct)) : Preview("api/node/transfer", body);
    }

    [McpServerTool(Name = "wz_asset_delete"), Description("Delete explicit nodes in memory. Destructive; preview and obtain approval first.")]
    public static async Task<string> Delete(MapClient client, string[] paths, CancellationToken ct, bool commit = false)
    {
        if (paths.Length == 0 || paths.Length > 300) throw new ArgumentException("Provide 1–300 paths.");
        var body = new { paths };
        return commit ? MapClient.J(await client.PostAsync("api/node/delete", body, ct)) : Preview("api/node/delete", body);
    }

    [McpServerTool(Name = "wz_asset_changes"), Description("Inspect unsaved changes before saving or closing archives.")]
    public static async Task<string> Changes(MapClient client, CancellationToken ct) =>
        MapClient.J(await client.GetAsync("api/changes", ct));

    [McpServerTool(Name = "wz_asset_history"), Description("Read backend undo/redo history; history may include another user's edits.")]
    public static async Task<string> History(MapClient client, CancellationToken ct) =>
        MapClient.J(await client.GetAsync("api/history", ct));

    [McpServerTool(Name = "wz_asset_undo"), Description("Undo the most recent session operation, not necessarily this agent's operation. Inspect history and obtain approval first.")]
    public static async Task<string> Undo(MapClient client, CancellationToken ct, bool commit = false) =>
        commit ? MapClient.J(await client.PostAsync("api/undo", new { }, ct)) : Preview("api/undo", new { });

    [McpServerTool(Name = "wz_asset_save_as"), Description("Save an archive to an explicit candidate path with backup enabled. Rewrites the archive. Does NOT synchronize Data/NX or publish patches. Never pass a production path without approval.")]
    public static async Task<string> SaveAs(MapClient client, string fileId, string targetPath, CancellationToken ct, bool commit = false)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("An explicit candidate targetPath is required.");
        var body = new { fileId, targetPath, backup = true };
        return commit ? MapClient.J(await client.PostAsync("api/files/save", body, ct)) : Preview("api/files/save", body);
    }

    [McpServerTool(Name = "wz_asset_create_empty"), Description("Create a brand-new EMPTY .wz or .nx container at an explicit path, so fixtures can be built without touching a live client. The extension decides the format. Never overwrites, and refuses the folder the open archives live in. The container is opened and read back before it is given its name.")]
    public static async Task<string> CreateEmpty(MapClient client, string path, CancellationToken ct,
        string? mapleVersion = null, string? iv = null, short gameVersion = -1, bool commit = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An explicit target path is required.");
        var body = new { path, mapleVersion, iv, gameVersion };
        return commit ? MapClient.J(await client.PostAsync("api/files/create-empty", body, ct)) : Preview("api/files/create-empty", body);
    }
}
