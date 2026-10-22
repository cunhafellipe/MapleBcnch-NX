using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The visual half of the server: the routes that return a picture rather than
/// a number, so an agent can LOOK at a node instead of guessing what is in it.
/// </summary>
/// <remarks>
/// <para>
/// Every route here was read out of the endpoint source rather than guessed:
/// <c>MapleBench/Api/Endpoints.cs</c> (MapMedia and the export group) and
/// <c>MapleBench/Api/DumpEndpoints.cs</c>. Where the endpoint accepts an
/// argument this sends it; where it does not, this refuses the argument rather
/// than appending a query key the server ignores — a silently dropped
/// <c>&amp;scale=2</c> is a tool that appears to work.
/// </para>
/// <para>
/// <b>What a rendering proves, exactly.</b> A canvas that comes back as a PNG
/// proves ONE thing: this node decodes as a canvas, here, right now. It does
/// NOT verify the pixels against any reference — nothing in this file compares
/// a rendering to the original art, because the original art is what is being
/// questioned — and it does NOT prove the art is the RIGHT art. A sprite that
/// belongs to a different mob decodes just as cleanly as the right one, and so
/// does a canvas whose origin is wrong, or whose palette was repaired into
/// something plausible. "It rendered" is evidence that the bytes are readable
/// and nothing more. Do not report it as "the art is correct".
/// </para>
/// <para>
/// <b>Inline versus disk.</b> <see cref="View"/> and <see cref="Thumb"/> return
/// base64 so the answer arrives in the same turn it was asked, and cap the
/// payload at 4 MiB — over that the bytes are omitted and the size reported,
/// because a truncated PNG is not a smaller PNG, it is a corrupt one. Every
/// other tool here writes a FILE, requires an explicit <c>outputPath</c>, and
/// refuses to overwrite; the output is an unverified candidate until something
/// outside this server has compared it to a reference.
/// </para>
/// <para>
/// Distinct from <c>RenderTools</c> (wz_map_render, wz_map_render_mob): those
/// draw MAPS out of the map editor's own geometry and character track. These
/// are the archive's own media endpoints.
/// </para>
/// </remarks>
[McpServerToolType]
public static class AssetRenderTools
{
    /// <summary>
    /// What comes back inline stops here. Beyond it the caller is told the size
    /// and given a route to disk, rather than being handed half a PNG.
    /// </summary>
    private const long InlineCap = 4L * 1024 * 1024;

    /// <summary>
    /// For the two routes that answer with image bytes rather than JSON.
    /// </summary>
    /// <remarks>
    /// <see cref="MapClient"/> deliberately exposes no bytes-returning GET — the
    /// file path goes through <c>DownloadAsync</c>, which refuses to overwrite,
    /// streams, and caps at 256 MiB, and that helper is not re-implemented here.
    /// What is missing is the inline case, so this client exists only for the
    /// two routes that must answer in the same turn. Same base URL, same
    /// five-minute timeout as <see cref="MapClient"/>.
    /// </remarks>
    private static readonly HttpClient Media = new() { Timeout = TimeSpan.FromMinutes(5) };

    // ------------------------------------------------------------------
    // 1. One canvas, inline.
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders one canvas to PNG and hands it back as base64 with the size read
    /// out of the PNG header — the endpoint itself returns only bytes, so the
    /// dimensions are parsed, not guessed.
    /// </summary>
    [McpServerTool(Name = "wz_render_view")]
    [Description(
        "Render ONE canvas node to PNG and return the image inline as base64, with the width and height "
        + "read out of the PNG header. A successful rendering proves this node decodes as a canvas and "
        + "nothing more: it does not verify the pixels against a reference and does not prove the art is "
        + "the right art. Inline payloads stop at 4 MiB — a larger canvas comes back with its size and no "
        + "bytes, and wz_render_dump (kind=canvas) writes it to disk instead. Read-only.")]
    public static async Task<string> View(
        MapClient client,
        [Description("Canvas node path, e.g. f1/UIWindow.img/BtUIClose/0. Must be the canvas itself.")] string path,
        [Description("Only 1 is accepted. No backend route scales a canvas render; a canvas draws at its own stored magnification, so any other value is refused rather than quietly ignored.")] double? scale = null,
        [Description("Only \"png\" is accepted inline. format=raw (the decoded PNG zip) is a file download: use wz_render_dump with kind=canvas, format=raw.")] string? format = null,
        [Description("Render through canvas links to the picture they point at. False uses /api/canvas; true uses /api/dump/canvas, which is the route that accepts it.")] bool resolve = false,
        CancellationToken ct = default)
    {
        if (scale is { } s && s != 1.0)
            throw new ModelContextProtocol.McpException(
                "scale is not a parameter of any render route. WzRenderService draws a canvas at the "
                + "magnification stored on the canvas itself; there is no server-side resampling to ask for. "
                + "Resize the file yourself after a disk export if you need a different size.");
        if (format is not null && !format.Equals("png", StringComparison.OrdinalIgnoreCase))
            throw new ModelContextProtocol.McpException(
                "wz_render_view only produces image/png inline. The dump family's raw form (the decoded "
                + "canvas ZIP) is a file: call wz_render_dump with kind=canvas, format=raw and an outputPath.");

        // /api/canvas takes path alone. resolve is /api/dump/canvas's argument,
        // so asking for it moves the request rather than dropping the flag.
        string route = resolve
            ? $"api/dump/canvas?path={MapClient.Q(path)}&resolve=true"
            : $"api/canvas?path={MapClient.Q(path)}";
        return await InlineAsync(client, route, path, ct);
    }

    // ------------------------------------------------------------------
    // 2. The children of a node, at a glance.
    // ------------------------------------------------------------------

    /// <summary>
    /// Lists every canvas under a node, so a branch can be judged before it is
    /// expanded leaf by leaf. No pixels: this is the shape, not the picture.
    /// </summary>
    [McpServerTool(Name = "wz_render_preview")]
    [Description(
        "List every canvas under a node in one call — the frames it holds, their sizes and their paths — so "
        + "a whole branch can be judged before descending into it. JSON, no images. The backend clamps limit "
        + "to 1–200 and defaults it to 60. Read-only; a listed frame is not a proof that the frame renders, "
        + "use wz_render_view on the individual canvas for that.")]
    public static async Task<string> Preview(
        MapClient client,
        [Description("Node whose canvases should be collected, e.g. f7/0100100.img/stand")] string path,
        [Description("Canvases to return. The backend clamps this to 1–200; default 60.")] int limit = 60,
        CancellationToken ct = default) =>
        MapClient.J(await client.GetAsync($"api/preview?path={MapClient.Q(path)}&limit={limit}", ct));

    // ------------------------------------------------------------------
    // 3. One representative image for any node.
    // ------------------------------------------------------------------

    /// <summary>
    /// The endpoint's own representative image for a node: the node itself if
    /// it is a canvas, otherwise the first canvas found within depth 5.
    /// </summary>
    [McpServerTool(Name = "wz_render_thumb")]
    [Description(
        "Get one small representative PNG for any node — the node's own canvas, or the first canvas found "
        + "under it — for filling a list without opening each entry. Inline base64 with width/height, capped "
        + "at 4 MiB. A node with no art under it returns a 204, reported here as noImage rather than as an "
        + "error. As with every rendering in this file: it proves a canvas decodes, not that the art is right. "
        + "Read-only.")]
    public static async Task<string> Thumb(
        MapClient client,
        [Description("Any node: a canvas, or a branch whose first canvas should stand in for it")] string path,
        CancellationToken ct = default) =>
        await InlineAsync(client, $"api/thumb?path={MapClient.Q(path)}", path, ct);

    // ------------------------------------------------------------------
    // 4. Animation: the description, or the composed file.
    // ------------------------------------------------------------------

    /// <summary>
    /// Without <c>dump</c>: the animation's frames, delays and origins as JSON.
    /// With it: the composed GIF/APNG/sheet as a file on disk.
    /// </summary>
    [McpServerTool(Name = "wz_render_animation")]
    [Description(
        "Inspect an animation node (e.g. f7/0100100.img/stand). With dump=false — the default — returns the "
        + "JSON description: frames, delays, origins, size. With dump=true it writes the composed animation "
        + "to outputPath as a candidate file nobody has verified; GIF and APNG play, sheet and frames are a "
        + "ZIP carrying the timing beside the images. Read-only against the archive; outputPath is required "
        + "for dump=true and is never overwritten.")]
    public static async Task<string> Animation(
        MapClient client,
        [Description("Animation node, e.g. f7/0100100.img/stand — the stance, not the image or the frame")] string path,
        [Description("Composed output format: gif (default), apng, sheet, frames. Only read when dump=true; sheet and frames are ZIPs.")] string? format = null,
        [Description("false returns the JSON description; true writes the composed animation to outputPath.")] bool dump = false,
        [Description("Absolute destination for the composed animation. REQUIRED when dump=true. Refuses to overwrite. The file is a candidate nobody has verified.")] string? outputPath = null,
        CancellationToken ct = default)
    {
        if (!dump)
            return MapClient.J(await client.GetAsync($"api/animation?path={MapClient.Q(path)}", ct));

        // The route always writes a file, so an explicit destination is not
        // optional here — there is no inline form of a composed animation.
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ModelContextProtocol.McpException(
                "dump=true writes a file, so outputPath is required. Use dump=false for the JSON description.");

        string chosen = string.IsNullOrWhiteSpace(format) ? "gif" : format.Trim().ToLowerInvariant();
        if (chosen is not ("gif" or "apng" or "sheet" or "frames"))
            throw new ModelContextProtocol.McpException(
                $"Unknown animation format '{format}'. Valid formats: gif, apng, sheet, frames.");

        string route = $"api/dump/animation?path={MapClient.Q(path)}&format={MapClient.Q(chosen)}";
        return JsonSerializer.Serialize(await client.DownloadAsync(route, outputPath, ct));
    }

    // ------------------------------------------------------------------
    // 5. The dump family, as files.
    // ------------------------------------------------------------------

    /// <summary>
    /// Downloads one dump artefact to an explicit candidate path. The argument
    /// a kind takes is the argument its endpoint takes — csv takes depth, not
    /// limit — so <c>limit</c> is mapped per kind rather than sprayed onto
    /// every route.
    /// </summary>
    [McpServerTool(Name = "wz_render_dump")]
    [Description(
        "Download one dump artefact to a file: kind=canvas (this canvas as PNG, or format=raw for the decoded "
        + "canvas ZIP), sound (the audio bytes), link (the link's resolution as JSON), csv (a node flattened "
        + "to CSV). Writes to outputPath, which is REQUIRED, must be new, and is a candidate nobody has "
        + "verified — nothing here compares it to a reference. Reads the archive only. csv's limit is sent as "
        + "the endpoint's depth parameter (its default is 4).")]
    public static async Task<string> Dump(
        MapClient client,
        [Description("canvas | sound | link | csv. Anything else is refused by name rather than guessed at a route.")] string kind,
        [Description("Node the artefact is taken from")] string path,
        [Description("Absolute destination for the downloaded file. REQUIRED, must not already exist. The result is a candidate nobody has verified.")] string outputPath,
        [Description("canvas only: \"png\" (default) or \"raw\", the decoded canvas ZIP. Ignored by the other kinds.")] string? format = null,
        [Description("csv only: how deep to flatten, sent as the endpoint's depth parameter (default 4). Ignored by the other kinds.")] int? limit = null,
        CancellationToken ct = default)
    {
        string q = "path=" + MapClient.Q(path);
        string route = kind.Trim().ToLowerInvariant() switch
        {
            "canvas" => "api/dump/canvas?" + q + CanvasFormat(format),
            "sound" => "api/dump/sound?" + q,
            "link" => "api/dump/link?" + q,
            "csv" => "api/dump/csv?" + q + (limit is { } d ? "&depth=" + d : string.Empty),
            _ => throw new ModelContextProtocol.McpException(
                $"Unknown dump kind '{kind}'. Valid kinds: canvas, sound, link, csv."),
        };
        return JsonSerializer.Serialize(await client.DownloadAsync(route, outputPath, ct));
    }

    /// <summary>The dump canvas route's own format switch, validated rather than passed through.</summary>
    private static string CanvasFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return string.Empty;
        string chosen = format.Trim().ToLowerInvariant();
        return chosen switch
        {
            "png" or "raw" => "&format=" + chosen,
            _ => throw new ModelContextProtocol.McpException(
                $"Unknown dump canvas format '{format}'. Valid formats: png, raw."),
        };
    }

    // ------------------------------------------------------------------
    // 6. The export family, as files.
    // ------------------------------------------------------------------

    /// <summary>
    /// Downloads one export artefact. img and node-img are the same bytes for
    /// different node classes — a skill lives at <c>book.img/skill/&lt;id&gt;</c>,
    /// a WzImageProperty, which /export/img rejects — so the two kinds are not
    /// interchangeable.
    /// </summary>
    [McpServerTool(Name = "wz_render_export")]
    [Description(
        "Export a subtree to a file: kind=img (one image as a standalone IMG), node-img (the same bytes for a "
        + "non-image node such as Skill book.img/skill/<id>, which img rejects), json (depth-limited tree), "
        + "xml (classic <imgdir> XML), xml-zip (a tree of XML images), images (every image as PNGs in a ZIP). "
        + "Writes to outputPath, which is REQUIRED, must be new, and is a candidate nobody has verified — "
        + "publishing it to Data/NX or installing it over a client is a separate, deliberate act. Reads the "
        + "archive only.")]
    public static async Task<string> Export(
        MapClient client,
        [Description("img | node-img | json | xml | xml-zip | images. Anything else is refused by name.")] string kind,
        [Description("Node to export")] string path,
        [Description("Absolute destination for the export. REQUIRED, must not already exist. The file is a candidate nobody has verified.")] string outputPath,
        [Description("json only: how deep to emit. The backend clamps this to 1–40, default 12.")] int? depth = null,
        [Description("xml-zip only: how many images to include, under the backend's own ceiling.")] int? limit = null,
        CancellationToken ct = default)
    {
        string q = "path=" + MapClient.Q(path);
        string route = kind.Trim().ToLowerInvariant() switch
        {
            "img" => "api/export/img?" + q,
            "node-img" => "api/export/node-img?" + q,
            "json" => "api/export/json?" + q + (depth is { } j ? "&depth=" + j : string.Empty),
            "xml" => "api/export/xml?" + q,
            "xml-zip" => "api/export/xml-zip?" + q + (limit is { } n ? "&limit=" + n : string.Empty),
            "images" => "api/export/images?" + q,
            _ => throw new ModelContextProtocol.McpException(
                $"Unknown export kind '{kind}'. Valid kinds: img, node-img, json, xml, xml-zip, images."),
        };
        return JsonSerializer.Serialize(await client.DownloadAsync(route, outputPath, ct));
    }

    // ------------------------------------------------------------------
    // Shared inline path.
    // ------------------------------------------------------------------

    /// <summary>
    /// Fetches an image route and answers with base64, the PNG header's own
    /// dimensions, and the server's suggested file name.
    /// </summary>
    /// <remarks>
    /// The backend answers these routes with <c>Results.File(png, "image/png",
    /// name)</c> — raw bytes and nothing else, so every field here is read off
    /// the response rather than assumed: dimensions from the PNG IHDR, the name
    /// from Content-Disposition, emptiness from a 204.
    /// </remarks>
    private static async Task<string> InlineAsync(
        MapClient client, string route, string path, CancellationToken ct)
    {
        using HttpResponseMessage res = await Media.GetAsync(
            new Uri(client.BaseUrl + "/" + route), HttpCompletionOption.ResponseHeadersRead, ct);

        if (!res.IsSuccessStatusCode)
        {
            // The backend's own sentence names the offending argument. It is
            // the most useful thing in the failure, so it is passed through.
            string body = await res.Content.ReadAsStringAsync(ct);
            string detail = MapClient.ExtractMessage(body) ?? body;
            throw new MapClient.BackendError($"{route} -> HTTP {(int)res.StatusCode}: {detail}");
        }

        byte[] data = await res.Content.ReadAsByteArrayAsync(ct);

        // /api/thumb answers 204 when nothing under the node has art. That is
        // an answer, not a failure, and reporting it as a zero-byte image
        // would send the caller hunting for a decode bug that does not exist.
        if (data.Length == 0)
            return JsonSerializer.Serialize(new
            {
                path,
                route,
                noImage = true,
                note = "The backend returned no image: this node holds no art. Nothing was rendered.",
            });

        (int Width, int Height)? size = PngSize(data);
        bool capped = data.Length > InlineCap;
        return JsonSerializer.Serialize(new
        {
            path,
            route,
            format = res.Content.Headers.ContentType?.MediaType ?? "image/png",
            width = size?.Width,
            height = size?.Height,
            bytes = data.Length,
            fileName = SuggestedName(res),
            capped,
            base64Png = capped ? null : Convert.ToBase64String(data),
            note = capped
                ? $"Larger than the {InlineCap / (1024 * 1024)} MiB inline cap, so no bytes are returned: a "
                  + "truncated PNG is a corrupt one, not a smaller one. The size is above; call "
                  + "wz_render_dump with kind=canvas and an outputPath to get the file."
                : "A rendering proves this node decodes as a canvas. It does not verify the pixels against a "
                  + "reference and does not prove the art is the right art.",
        });
    }

    /// <summary>
    /// Width and height out of the PNG IHDR, or null if this is not a PNG.
    /// </summary>
    /// <remarks>
    /// Bytes 16..24 of a PNG are two big-endian uint32s; the signature is
    /// checked first so a caller handed some other content type gets null
    /// rather than two numbers read out of noise.
    /// </remarks>
    private static (int Width, int Height)? PngSize(byte[] data)
    {
        if (data.Length < 24 || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47)
            return null;
        int width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        int height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
        return (width, height);
    }

    /// <summary>The name the backend chose, which is its SafeFileName of the path.</summary>
    private static string? SuggestedName(HttpResponseMessage res) =>
        res.Content.Headers.ContentDisposition?.FileName?.Trim('"');
}