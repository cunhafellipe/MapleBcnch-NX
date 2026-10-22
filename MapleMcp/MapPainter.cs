using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace MapleMcp;

/// <summary>
/// Paints a map out of layer tiles into a bitmap.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a v83 map has no canvas node of its own: the art lives
/// under <c>0/*/*/*</c> as thousands of <c>grassySoil/bsc/2</c>-shaped leaves, and
/// the only thing that assembles them is the browser's <c>mapcanvas.js</c>. An
/// agent that wants a picture has no route to one, which is the whole reason
/// JOURNAL/TODO.md item 10 ("視覺驗收仍然做不了") sat unfixed.
///
/// </para>
/// <para>
/// The layer rectangles come from the backend's own <c>/api/look</c>-style node
/// metadata rather than from a guess about MapleStory's 1990s layer rules: every
/// tile reports its own <c>x</c>, <c>y</c> and <c>zm</c>, so a map that uses an
/// unusual layer count draws the same here as in the browser.
/// </para>
/// <para>
/// <b>Drawing is one HTTP GET per leaf.</b> A 265-tile map is 265 round trips,
/// which measured 0.6–8 ms each, so a full map lands in one to three seconds.
/// That is slower than shipping bytes and it is still the right trade: the
/// backend owns parsing, caching and the NX format, and a second implementation
/// of any of those is a second thing to be wrong.
/// </para>
/// </remarks>
internal static class MapPainter
{
    internal static readonly Color Backdrop = Color.FromArgb(24, 26, 32);
    internal static readonly Color GroundLine = Color.FromArgb(90, 170, 255);
    internal static readonly Color TrackLine = Color.FromArgb(255, 200, 60);
    internal static readonly Color SpawnDot = Color.FromArgb(255, 120, 120);
    internal static readonly Color NpcDot = Color.FromArgb(140, 220, 255);

    internal static async Task<int> PaintToFile(
        MapClient client, string path, string mapFileId, JsonNode? cast, string outputPath, CancellationToken ct)
    {
        if (!outputPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            outputPath += ".png";
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        JsonNode? doc = await client.GetAsync($"/api/mapedit/doc?path={MapClient.Q(path)}", ct);
        JsonNode? bounds = doc?["bounds"];
        int left = bounds?["left"]?.GetValue<int>() ?? 0;
        int top = bounds?["top"]?.GetValue<int>() ?? 0;
        int right = bounds?["right"]?.GetValue<int>() ?? 1000;
        int bottom = bounds?["bottom"]?.GetValue<int>() ?? 600;

        const int margin = 24;
        int w = Math.Max(64, right - left + margin * 2);
        int h = Math.Max(64, bottom - top + margin * 2);

        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Backdrop);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // Back (sky, distant treeline, parallax) goes down first: it is the
            // furthest layer, and painting it after the tiles would bury the map.
            int drawn = await DrawBacks(g, client, mapFileId, doc, left, top, w, h, ct);
            drawn += await DrawTiles(g, client, mapFileId, doc, left, top, ct);
            DrawFootholds(g, doc, left, top);
            DrawTrack(g, cast, left, top);
            DrawSpawns(g, cast, left, top);

            DrawFrame(g, w, h, path, drawn);
            bmp.Save(outputPath, ImageFormat.Png);
            return drawn;
        }
    }

    private static async Task<int> DrawTiles(
        Graphics g, MapClient client, string mapFileId, JsonNode? doc, int left, int top, CancellationToken ct)
    {
        if (doc?["layers"] is not JsonArray layers) return 0;

        int drawn = 0;
        int missed = 0;
        // One canvas download per DISTINCT (set, variant, index), not per tile:
        // a 265-tile map names grassySoil/bsc/2 sixty times and fetching it sixty
        // times is sixty round trips for one bitmap.
        var cache = new Dictionary<string, Bitmap?>();

        foreach (JsonNode? layer in layers)
        {
            if (layer?["tiles"] is not JsonArray tiles) continue;
            // Highest z first so the walker stands in front of what it stands on.
            foreach (JsonNode? t in tiles.OrderByDescending(x => x?["zm"]?.GetValue<int>() ?? 0))
            {
                string? art = t?["art"]?.ToString();
                if (string.IsNullOrEmpty(art)) continue;

                if (!cache.TryGetValue(art, out var img))
                {
                    img = await FetchLeaf(client, mapFileId, art, ct);
                    cache[art] = img;
                }
                if (img is null) { missed++; continue; }

                int tx = t?["x"]?.GetValue<int>() ?? 0;
                int ty = t?["y"]?.GetValue<int>() ?? 0;
                g.DrawImage(img, tx - left + 24, ty - top + 24);
                drawn++;
            }
        }
        return drawn;
    }

    /// <summary>
    /// Resolve one <c>art</c> key to its canvas.
    /// </summary>
    /// <remarks>
    /// The key is <c>t|grassySoil|bsc|2</c>: kind, tile-set, variant, index. Two
    /// facts the key does not state and this has to know:
    /// the set is really <c>grassySoil.img</c> (the extension is not in the key),
    /// and the leaf lives at <c>&lt;set&gt;/&lt;variant&gt;/&lt;index&gt;</c> under the
    /// map archive's own Tile or Obj root. Guessing either produced a 404 whose
    /// message ("'grassySoil' not found under 'f7/Tile'") reads like a missing
    /// asset when the asset is right there with an extension.
    /// </remarks>
    private static async Task<Bitmap?> FetchLeaf(
        MapClient client, string mapFileId, string art, CancellationToken ct)
    {
        string[] parts = art.Split('|');
        if (parts.Length < 4) return null;
        string kind = parts[0];                 // "t" = tile, "o" = object
        string set = parts[1];
        string variant = parts[2];
        string index = parts[3];

        string[] roots = kind == "o" ? new[] { "Obj", "Tile" } : new[] { "Tile", "Obj" };
        foreach (string root in roots)
        {
            // The id comes from the map's own address, never a literal. It is not
            // f1: ids are handed out in OPEN ORDER, so f1 is UI.nx on this
            // machine and Map.nx in a session that opened it first.
            // No trailing "/canvas": the leaf node IS the canvas. Measured
            // 200 image/png 4524B at "/bsc/2" and 404 at "/bsc/2/canvas".
            string addr = $"{mapFileId}/{root}/{set}.img/{variant}/{index}";
            var tmp = Path.Combine(Path.GetTempPath(), $"mapleaf-{Guid.NewGuid():N}.png");
            try
            {
                await client.DownloadAsync($"/api/canvas?path={MapClient.Q(addr)}&v=r", tmp, ct);
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                {
                    var b = new Bitmap(tmp);
                    try { File.Delete(tmp); } catch { /* temp is fine */ }
                    return b;
                }
            }
            catch
            {
                // 404 on this root is expected; try the next one.
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Paint the Back layer: sky, distant treeline, parallax scrolls.
    /// </summary>
    /// <remarks>
    /// This is a port of <c>mapedit-view.js:drawBacks</c> (line 976), not a
    /// reconstruction of it. Three things were wrong before the port and each
    /// one is why the sky was missing or clipped:
    ///
    /// <list type="bullet">
    /// <item><b>Frame index.</b> <c>art</c> is <c>b|set|0|frame</c> -- the THIRD
    /// field is constant 0 and the FOURTH selects the picture. Using the third
    /// fetched <c>back/0</c> six times, a 256x256 blank.</item>
    /// <item><b>Anchoring.</b> A back sits at <c>x - ox, y - oy</c> using the
    /// canvas's own origin, not centred and not corner-first. A 2493x340 sky
    /// anchored by its corner hangs hundreds of pixels off the map.</item>
    /// <item><b>Tiling.</b> <c>type</c> 1-7 say whether the image repeats
    /// horizontally, vertically or both -- that is what fills the area past the
    /// image edge. Types 0 and 4 are what this map uses; a type-3 sky tiles both
    /// ways and without it only one tile shows, which is exactly the "only two
    /// blocks" symptom.</item>
    /// </list>
    ///
    /// Parallax (<c>rx</c>/<c>ry</c> as a percentage of camera scroll) is zero in
    /// a full-map render, so it collapses to <c>x - ox</c> here; the term is
    /// kept so the port stays honest about where it went.
    /// </remarks>
    private static async Task<int> DrawBacks(
        Graphics g, MapClient client, string mapFileId, JsonNode? doc,
        int left, int top, int worldW, int worldH, CancellationToken ct)
    {
        if (doc?["backs"] is not JsonArray backs) return 0;
        JsonNode? artTable = doc["art"];

        int drawn = 0;
        // Back entries are stored in front-to-back order in some maps, so paint
        // them in reverse: the first entry is the furthest layer.
        foreach (JsonNode? b in backs.Reverse())
        {
            string? artKey = b?["art"]?.ToString();
            if (string.IsNullOrEmpty(artKey)) continue;

            JsonNode? meta = artTable?[artKey];
            if (meta is null || (meta["missing"]?.GetValue<bool>() ?? false)) continue;

            int ox = meta["ox"]?.GetValue<int>() ?? 0;
            int oy = meta["oy"]?.GetValue<int>() ?? 0;
            int iw = meta["w"]?.GetValue<int>() ?? 0;
            int ih = meta["h"]?.GetValue<int>() ?? 0;
            if (iw <= 0 || ih <= 0) continue;

            int bx = (b?["x"]?.GetValue<int>() ?? 0) - left + 24 - ox;
            int by = (b?["y"]?.GetValue<int>() ?? 0) - top + 24 - oy;

            int type = b?["type"]?.GetValue<int>() ?? 0;
            bool tileX = type is 1 or 3 or 4 or 6 or 7;
            bool tileY = type is 2 or 3 or 5 or 6 or 7;

            int cw = b?["cx"]?.GetValue<int>() is int cx && cx > 0 ? cx : iw;
            int ch = b?["cy"]?.GetValue<int>() is int cy && cy > 0 ? cy : ih;

            // Screen-space extents of the whole map, so a tiling back covers the
            // map and not just the one image.
            int mapL = 24, mapT = 24;
            int mapR = worldW + 24, mapB = worldH + 24;

            int x0 = bx, x1 = bx + 1;
            if (tileX)
            {
                x0 = bx + ((mapL - bx) / cw - 1) * cw;
                x1 = mapR + cw;
            }
            int y0 = by, y1 = by + 1;
            if (tileY)
            {
                y0 = by + ((mapT - by) / ch - 1) * ch;
                y1 = mapB + ch;
            }

            using var img = await FetchBackLeaf(client, mapFileId, artKey, ct);
            if (img is null) continue;

            float alpha = Math.Clamp((b?["a"]?.GetValue<int>() ?? 255) / 255f, 0f, 1f);
            using var layer = new Bitmap(img.Width, img.Height);
            using (var lg = Graphics.FromImage(layer))
            {
                lg.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                lg.DrawImage(img, 0, 0);
            }
            var painted = layer;
            Bitmap? faded = null;
            if (alpha < 1f)
            {
                var cm = new ColorMatrix { Matrix33 = alpha };
                using var fade = new ImageAttributes();
                fade.SetColorMatrix(cm);
                faded = Fade(layer, fade);
                painted = faded;
            }

            bool flip = (b?["f"]?.GetValue<int>() ?? 0) == 1;
            for (int y = y0; y < y1; y += ch)
            {
                for (int x = x0; x < x1; x += cw)
                {
                    if (flip) g.DrawImage(painted, new Rectangle(x + iw, y, iw, ih), 0, 0, iw, ih, GraphicsUnit.Pixel);
                    else g.DrawImage(painted, x, y);
                    if (!tileX && x1 - x0 <= 1) break;
                    drawn++;
                }
                if (!tileY && y1 - y0 <= 1) break;
            }
            faded?.Dispose();
        }
        return drawn;
    }

    private static Bitmap Fade(Bitmap src, ImageAttributes attr)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var dg = Graphics.FromImage(dst);
        dg.Clear(Color.Transparent);
        dg.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                     0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
        return dst;
    }

    /// <summary>
    /// Fetch one Back canvas by its art key.
    /// </summary>
    /// <remarks>
    /// <c>b|grassySoil|0|1</c> -> <c>&lt;archive&gt;/Back/grassySoil.img/back/1</c>.
    /// The third field is a constant 0 in every entry measured; the fourth is the
    /// frame, and it is what picks the picture. The nine frames of this set
    /// differ enormously (906 B blank through 475 KB treeline), so picking the
    /// wrong one is not a cosmetic error.
    /// </remarks>
    private static async Task<Bitmap?> FetchBackLeaf(
        MapClient client, string mapFileId, string art, CancellationToken ct)
    {
        string[] parts = art.Split('|');
        if (parts.Length < 4) return null;
        string set = parts[1], frame = parts[3];

        var tmp = Path.Combine(Path.GetTempPath(), $"mapback-{Guid.NewGuid():N}.png");
        try
        {
            await client.DownloadAsync(
                $"/api/canvas?path={MapClient.Q($"{mapFileId}/Back/{set}.img/back/{frame}")}&v=r",
                tmp, ct);
            if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
            {
                var b = new Bitmap(tmp);
                try { File.Delete(tmp); } catch { }
                return b;
            }
        }
        catch { /* this set has no such frame */ }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        return null;
    }

    private static void DrawFootholds(Graphics g, JsonNode? doc, int left, int top)
    {
        if (doc?["footholds"] is not JsonArray foots) return;
        foreach (JsonNode? f in foots)
        {
            // The fields are x1,y1,x2,y2. Reading x0/y0 — which do not exist —
            // silently yields 0 for every foothold, so all 133 lines drew from a
            // single point at the origin and the map looked like a starburst
            // rather than a floor.
            int x0 = f?["x1"]?.GetValue<int>() ?? 0, y0 = f?["y1"]?.GetValue<int>() ?? 0;
            int x1 = f?["x2"]?.GetValue<int>() ?? 0, y1 = f?["y2"]?.GetValue<int>() ?? 0;
            bool wall = f?["isWall"]?.GetValue<bool>() ?? false;
            using var pen = new Pen(wall ? Color.FromArgb(200, 90, 90) : GroundLine, wall ? 2f : 1f);
            g.DrawLine(pen, x0 - left + 24, y0 - top + 24, x1 - left + 24, y1 - top + 24);
        }
    }

    private static void DrawTrack(Graphics g, JsonNode? cast, int left, int top)
    {
        if (cast?["track"] is not JsonArray track || track.Count < 2) return;
        using var pen = new Pen(TrackLine, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        PointF[] pts = track
            .Select(p => new PointF(
                (p?["x"]?.GetValue<int>() ?? 0) - left + 24,
                (p?["y"]?.GetValue<int>() ?? 0) - top + 24))
            .ToArray();
        if (pts.Length >= 2) g.DrawLines(pen, pts);
    }

    private static void DrawSpawns(Graphics g, JsonNode? cast, int left, int top)
    {
        if (cast?["spawns"] is not JsonArray spawns) return;
        foreach (JsonNode? s in spawns)
        {
            string kind = s?["kind"]?.ToString() ?? "?";
            int x = (s?["x"]?.GetValue<int>() ?? 0) - left + 24;
            int y = (s?["y"]?.GetValue<int>() ?? 0) - top + 24;
            using var brush = new SolidBrush(kind == "npc" ? NpcDot : SpawnDot);
            using var font = new Font("Segoe UI", 9);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center };
            g.FillEllipse(brush, x - 5, y - 5, 10, 10);
            string id = s?["id"]?.ToString() ?? "";
            if (id.Length > 0)
            {
                g.DrawString(id, font, Brushes.White,
                    new RectangleF(x - 40, y + 6, 80, 14), fmt);
            }
        }
    }

    private static void DrawFrame(Graphics g, int w, int h, string path, int drawn)
    {
        using var pen = new Pen(Color.FromArgb(90, 100, 120));
        g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
        using var font = new Font("Consolas", 10);
        g.DrawString($"{path}   tiles drawn: {drawn}", font, Brushes.Gainsboro, 8, 6);
    }
}