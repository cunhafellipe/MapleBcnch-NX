using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// One source IMG and the directory inside the NX archive it belongs in.
/// </summary>
public sealed class NxMergeEntryArg
{
    [Description("Absolute path to a standalone .img file, e.g. 'E:/SAO Client/content/floor01/generated-field-v001/img/Map/Map9/910870003.img'.")]
    public string Source { get; set; } = "";

    [Description("Directory inside the NX archive, slash-separated, e.g. 'Map/Map9' or 'Obj'. The image keeps its own file name and must already have a home: this grafts images, it does not create directories.")]
    public string Target { get; set; } = "";
}

/// <summary>
/// The NX archive merge surface: put standalone candidate IMGs into the
/// archives the client opens.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="AssetTools"/> because the operation is not a generic
/// node edit. A merge rewrites a whole NX archive, and it is the one thing an
/// authoring pipeline cannot express as a sequence of <c>wz_asset_*</c> calls
/// without opening and saving the archive itself. Here it is one call, previewed
/// first, with the backend's own save transaction underneath.
/// </para>
/// <para>
/// The archive is opened, edited and closed by the backend within the one
/// request, so the session is left as it was found.
/// </para>
/// </remarks>
[McpServerToolType]
public static class NxMergeTools
{
    [McpServerTool(Name = "wz_asset_merge_nx"), Description(
        "Merge standalone .img files into an existing NX archive (e.g. put a generated map at Map/Map9/910870003.img and its object art at Obj/). " +
        "Each entry names a source .img and a target NX directory; the image keeps its own file name and an existing same-named image is replaced. " +
        "The whole archive is rewritten, so it keeps a timestamped backup beside it and verifies the bytes by re-reading them. " +
        "The archive must NOT already be open in this session — close it first. " +
        "commit=false (the default) opens and inspects the archive and reports what would be added or replaced without writing; " +
        "read that, then call again with commit=true. Not a WZ/.wz save and not a publication.")]
    public static async Task<string> MergeNx(
        MapClient client,
        string targetNx,
        NxMergeEntryArg[] entries,
        CancellationToken ct,
        bool backup = true,
        bool commit = false)
    {
        if (entries is null || entries.Length == 0)
        {
            throw new ModelContextProtocol.McpException(
                "Provide at least one entry, each with a source .img path and a target NX directory.");
        }

        foreach (NxMergeEntryArg entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target))
            {
                throw new ModelContextProtocol.McpException(
                    "Every entry needs both a source .img path and a target NX directory.");
            }
        }

        var body = new { targetNx, entries, backup, commit };
        return MapClient.J(await client.PostAsync("api/nx/merge", body, ct));
    }
}
