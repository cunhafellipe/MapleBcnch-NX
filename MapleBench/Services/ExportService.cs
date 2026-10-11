using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services.Nx;

namespace MapleBench.Services;

/// <summary>
/// Bulk extraction: a subtree of sprites as a ZIP, or a subtree of values as
/// JSON.
///
/// Both exist so work can leave the editor — sprites into an image editor,
/// values into a diff or a spreadsheet — and, for sprites, come back again.
/// Canvas exports carry a sidecar manifest holding the metadata a PNG cannot
/// (origin, delay, format, links), which is what makes the round trip possible.
/// </summary>
public sealed class ExportService
{
    private const int MaxEntries = 5000;

    /// <summary>
    /// The sweep's own visit budget has to be looser than the entry cap, or it
    /// is the one that fires and the ZIP comes back short of what its manifest
    /// claims.  Eight nodes visited per canvas found leaves room for the
    /// containers in between while still bounding the walk.
    /// </summary>
    private const int MaxVisit = MaxEntries * 8;

    /// <summary>Per-level width cap for the JSON export.</summary>
    private const int MaxChildren = 2000;

    /// <summary>
    /// A hard ceiling on how much of a tree one JSON export may materialise.
    ///
    /// Depth and per-level width are capped already, but 2000^40 is not a bound:
    /// the whole tree is built as a Dictionary under the global session lock
    /// before a single byte is serialised, so one click on an archive root can
    /// exhaust memory and block every other request while it does it.  The
    /// character budget is here because node count alone does not predict size —
    /// a handful of Lua or long string properties can outweigh 100k ints.
    /// </summary>
    private const int MaxNodes = 200_000;
    private const long MaxChars = 32L * 1024 * 1024;

    private readonly WzSessionService _session;
    private readonly WzRenderService _render;
    private readonly AnimationService _animation;
    private readonly ILogger<ExportService> _log;

    public ExportService(
        WzSessionService session, WzRenderService render, AnimationService animation, ILogger<ExportService> log)
    {
        _session = session;
        _render = render;
        _animation = animation;
        _log = log;
    }

    /// <summary>Serialises one WZ image as a standalone .img file.</summary>
    public (byte[] Data, string FileName) ExportImg(string path)
    {
        lock (_session.Gate)
        {
            WzObject node = _session.Resolve(path);
            if (node is not WzImage image)
            {
                throw new InvalidOperationException(
                    $"'{node.Name}' is not a WZ image. Select an .img node, then export it as .img.");
            }

            WzSessionService.EnsureParsed(image);

            // An NX-backed tree needs arranging before it can be written: its canvases have to be
            // given WZ-compressed payloads, and its images have to be marked changed so
            // MapleLib re-serialises them instead of copying bytes out of a reader they do
            // not have. Neither is a no-op for a real .wz, so this stays on the common path.
            byte[] data = NxWzExport.SerializeImageAsWz(image);
            if (data.Length == 0)
                throw new InvalidOperationException("The image serialised to zero bytes, so no download was made.");

            string name = Sanitise(image.Name ?? "export.img");
            if (!name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                name += ".img";
            return (data, name);
        }
    }

    /// <summary>
        /// Serialises any node as a standalone .img file, wrapping a property
        /// subtree in a synthetic image when the node is not an image itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Needed because the per-resource unit is not always a <see cref="WzImage"/>.
        /// Skills are the case that forced this: <c>Skill_00000.ms</c> holds 408
        /// images at the top level, but they are job groups, not skills — one is
        /// 2.7 MB because 75 skills sit inside it. The individual skill lives at
        /// <c>&lt;group&gt;.img/skill/&lt;id&gt;</c>, and that node is a
        /// <c>WzImageProperty</c>, which <see cref="ExportImg"/> refuses outright.
        /// </para>
        /// <para>
        /// The subtree is cloned rather than re-parented: a MapleLib property
        /// carries a parent pointer and a reader, and moving a live node into a new
        /// image would leave the original archive pointing at a property that now
        /// belongs to something else. The clone's own children become the new
        /// image's root children, so the output reads the same as any other .img
        /// — <c>icon</c>, <c>level</c>, <c>info</c> at the top rather than nested
        /// under an extra level.
        /// </para>
        /// </remarks>
        public (byte[] Data, string FileName, string? Note) ExportNodeAsImg(string path, string? asName)
            {
                int strippedLinkedCanvases = 0;
                        int unresolvable = 0;
                        string? strippedNote = null;

                lock (_session.Gate)
                {
                    WzObject node = _session.Resolve(path);
                    WzImage image;

                if (node is WzImage existing)
                {
                    // NOT the session's own object. The recovery path below calls
                    // property.Remove(), which removes through the parent pointer and
                    // mutates the collection the open archive is holding -- so serving
                    // it the live image would quietly delete UOL and _outlink content
                    // from the user's archive and leave WzImage.Changed set, making the
                    // next save persist the mutilation. Nothing here reports that, and
                    // nested WzSubProperty removals set no flag at all, so the deletions
                    // would not even show in the dirty indicator.
                    //
                    // Clone it instead. If the clone cannot be built, say so and refuse
                    // rather than damage the archive to produce a file.
                    image = CloneForExport(existing);
                }
                else if (node is WzImageProperty subtree)
                                                {
                                                    image = new WzImage(Stem(asName) ?? Stem(subtree.Name) ?? "node");

                                                    // The subtree is NOT cloned wholesale, and that is the whole
                                                    // point. DeepClone recurses into every descendant, so a
                                                    // single canvas with no local pixels kills the entire
                                                    // skill -- MapleLib's clone does imageProp.DeepClone()
                                                    // on that null and throws. Cloning one child at a time
                                                    // means the bad canvas costs the canvas and nothing else,
                                                    // and the skill's level/info/common still get written.
                                                    //
                                                    // The clone's own children become the new image's top
                                                    // level, so the output reads like any other .img rather
                                                    // than carrying an extra wrapper level.
                                                    if (subtree.WzProperties is { Count: > 0 } kids)
                                                    {
                                                        foreach (WzImageProperty child in kids)
                                                        {
                                                            try
                                                            {
                                                                image.AddProperty(child.DeepClone());
                                                            }
                                                            catch (Exception ex) when (ex is NullReferenceException
                                                                     or InvalidCastException
                                                                     or ArgumentException
                                                                     or InvalidOperationException
                                                                     or SystemException)
                                                                {
                                                                    unresolvable++;
                                                                }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        image.AddProperty(subtree.DeepClone());
                                                    }
                                                }
                else
                {
                    throw new InvalidOperationException(
                        $"'{node.Name}' is a {node.GetType().Name}, which carries no children to export. "
                        + "Select an .img node, or a property inside one.");
                }

                WzSessionService.EnsureParsed(image);

                            byte[] data;
                            try
                            {
                                data = NxWzExport.SerializeImageAsWz(image);
                            }
                            catch (NullReferenceException)
                            {
                                // A canvas that is only an _outlink into a _Canvas archive
                                // cannot be materialised when that archive is not loaded, and
                                // MapleLib dereferences the missing reader instead of saying so.
                                // 113 skills in Skill_00007 are in this state: their icon is a
                                // 1x1 link into Skill/_Canvas, and the rest of the skill --
                                // level, info, common -- is perfectly readable. Dropping the
                                // unresolvable canvas and writing the skill without it is far
                                // better than losing the skill, but it has to be reported
                                // rather than passed off as a complete export.
                                int dropped = DropLinkedCanvases(image);
                                if (dropped == 0)
                                    throw;

                                strippedLinkedCanvases = dropped;
                                data = NxWzExport.SerializeImageAsWz(image);
                            }

                            if (data.Length == 0)
                                throw new InvalidOperationException("The node serialised to zero bytes, so no download was made.");

                            string name = Sanitise(asName ?? image.Name ?? "node");
                            if (!name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                                name += ".img";
                            if (strippedLinkedCanvases > 0 || unresolvable > 0)
                                                    {
                                                        List<string> parts = [];
                                                        if (unresolvable > 0)
                                                            parts.Add($"{unresolvable} canvas(s) whose pixels live in a _Canvas archive that is not loaded");
                                                        if (strippedLinkedCanvases > 0)
                                                            parts.Add($"{strippedLinkedCanvases} unresolved _Canvas link(s)");
                                                        strippedNote = string.Join("; ", parts);
                                                    }
                            return (data, name, strippedNote);
                        }
                    }

                    /// <summary>
                    /// Removes canvas properties whose pixels live in another archive, so the
                    /// rest of the node can still be written. Returns how many were dropped.
                    /// </summary>
                    /// <summary>A detached copy of a whole image, built one child at a time.</summary>
                    /// <remarks>
                    /// DeepClone on the image itself recurses into every descendant and throws
                    /// on the first canvas whose reader is missing, losing the whole export.
                    /// Cloning per child means a bad canvas costs that canvas only. The copy is
                    /// never attached to the session, so the recovery pass that removes
                    /// unresolvable nodes can only damage this copy.
                    /// </remarks>
                    private static WzImage CloneForExport(WzImage source)
                    {
                        WzImage copy = new(source.Name);
                        if (source.WzProperties is not { Count: > 0 } kids)
                            return copy;

                        int copied = 0;
                        foreach (WzImageProperty child in kids)
                        {
                            WzImageProperty? clone = TryClone(child);
                            if (clone is null)
                                continue;

                            // AddProperty refuses a name it already holds, and same-named
                            // siblings are legal here: WzPropertyCollection counts them and
                            // WzPath.Child disambiguates them with "#n". Letting that refusal
                            // escape would be the very 500 this recovery exists to prevent, so
                            // the copy is allowed to differ in NAME and never in content.
                            if (copy.WzProperties.Any(p => p.Name == clone.Name))
                                clone.Name += "#export";

                            copy.AddProperty(clone);
                            copied++;
                        }

                        if (copied == 0)
                            throw new InvalidOperationException(
                                $"'{source.Name}' has no child that could be copied, so there is nothing to export.");

                        return copy;
                    }

                    /// <summary>A detached copy of one child, or null if it cannot be cloned.</summary>
                    private static WzImageProperty? TryClone(WzImageProperty child)
                    {
                        try
                        {
                            return child.DeepClone();
                        }
                        // Deliberately broad: the failure being recovered from is an NRE from
                        // MapleLib dereferencing a missing canvas reader, and a different
                        // unrecoverable shape should cost this child rather than the export.
                        catch (Exception)
                        {
                            return null;
                        }
                    }

                    private static int DropLinkedCanvases(WzImage image)
                    {
                        int dropped = 0;
                        foreach (WzImageProperty prop in image.WzProperties.OfType<WzImageProperty>().ToList())
                        {
                            dropped += DropLinkedCanvases(prop);
                        }
                        return dropped;
                    }

                    private static int DropLinkedCanvases(WzImageProperty property)
                        {
                            int dropped = 0;

                            // UOL is MapleStory's pointer type. Touching .WzProperties on one forces
                            // LinkValue, which is null when the target archive is not loaded, and
                            // that is an NRE rather than a "not available". These skills are written
                            // against _Canvas, which is deliberately not opened, so every UOL here
                            // is unresolvable and gets dropped. Reading it unguarded turned a
                            // recoverable export failure into a 500 -- the second NRE was mine.
                            if (property is WzUOLProperty)
                                return DropUnresolvable(property);

                            if (property is WzCanvasProperty canvas)
                            {
                                // _outlink is stored as a short string property next to the canvas
                                // data. Its presence means the real pixels are elsewhere.
                                try
                                {
                                    if (canvas.WzProperties?["_outlink"] is not null)
                                    {
                                        property.Remove();
                                        return 1;
                                    }
                                }
                                catch (Exception ex) when (ex is NullReferenceException)
                                {
                                    return DropUnresolvable(property);
                                }
                                return 0;
                            }

                            try
                            {
                                if (property.WzProperties is { } kids)
                                {
                                    foreach (WzImageProperty child in kids.OfType<WzImageProperty>().ToList())
                                    {
                                        dropped += DropLinkedCanvases(child);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is NullReferenceException)
                            {
                                return DropUnresolvable(property);
                            }
                            return dropped;
                        }

                        private static int DropUnresolvable(WzImageProperty property)
                        {
                            property.Remove();
                            return 1;
                        }

        /// <summary>The name without a directory or an .img suffix.</summary>
        private static string? Stem(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string stem = Path.GetFileNameWithoutExtension(value);
            return string.IsNullOrWhiteSpace(stem) ? null : stem;
        }

        /// <summary>
        /// Every canvas under a node as PNGs in a ZIP, mirroring the WZ hierarchy,
        /// plus a manifest.json describing each one.
        /// </summary>
    public byte[] ExportImages(string path)
    {
        // The sweep reports which of its own caps stopped it; anything else here
        // would be guesswork, and guessing from canvases.Count alone misses the
        // visit and depth limits entirely.
        List<AnimationFrameDto> canvases = _animation.CollectCanvases(
            path, out bool sweepTruncated, MaxEntries, MaxVisit);
        if (canvases.Count == 0)
            throw new InvalidOperationException("There are no images under this node.");

        MemoryStream buffer = new();
        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            List<object> manifest = new();
            HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);

            // Every canvas the sweep found and this loop could not write.
            //
            // These used to be a LogDebug and a 'continue'. The manifest then
            // reported 'count' -- how many landed -- and 'truncated', which covers
            // only the sweep's own caps, so a ZIP holding 100 of 600 sprites was
            // published as a complete export of 100. A dump is the one artefact
            // that leaves this tool and gets used somewhere else; it has to say
            // what is not in it. Named individually rather than counted, because
            // "17 frames could not be decoded" is not something anyone can act on
            // and a list of paths is.
            List<object> missing = new();
            void Dropped(string path, string why) => missing.Add(new
            {
                wzPath = Uri.UnescapeDataString(path),
                reason = why,
            });

            foreach (AnimationFrameDto canvas in canvases)
            {
                byte[]? png;
                try
                {
                    png = _render.RenderCanvasPng(canvas.Path);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Skipping {Path} during export", canvas.Path);
                    Dropped(canvas.Path, ex.Message);
                    continue;
                }
                if (png == null)
                {
                    Dropped(canvas.Path, "This canvas holds no pixels of its own.");
                    continue;
                }

                string entryName = UniqueEntryName(RelativeName(path, canvas.Path), used);

                ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                using (Stream stream = entry.Open())
                    stream.Write(png, 0, png.Length);

                manifest.Add(new
                {
                    file = entryName,
                    wzPath = Uri.UnescapeDataString(canvas.Path),
                    width = canvas.Width,
                    height = canvas.Height,
                    originX = canvas.OriginX,
                    originY = canvas.OriginY,
                    delay = canvas.Delay,
                });
            }

            if (manifest.Count == 0)
                throw new InvalidOperationException("None of the images under this node could be decoded.");

            ZipArchiveEntry manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (Stream stream = manifestEntry.Open())
            {
                JsonSerializer.Serialize(stream, new
                {
                    source = Uri.UnescapeDataString(path),
                    exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    count = manifest.Count,

                    // What the sweep found, so 'count' can be read against
                    // something. count == found and missing empty is the only
                    // shape that means "this is all of it".
                    found = canvases.Count,

                    // The sweep stopped early: there are canvases under this node
                    // it never reached, and they are not in 'missing' either
                    // because nothing knows their names.
                    truncated = sweepTruncated,

                    // Found, and not in this ZIP. An empty list is a claim, so it
                    // is always present rather than omitted when empty.
                    missing,
                    images = manifest,
                }, new JsonSerializerOptions { WriteIndented = true });
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// A subtree as JSON. Containers become objects, scalars become their
    /// values, and canvases/sounds become a short descriptor rather than data.
    /// </summary>
    public byte[] ExportJson(string path, int maxDepth)
    {
        JsonWalk walk = new() { MaxDepth = maxDepth };
        object? tree;
        string? rootName;

        lock (_session.Gate)
        {
            WzObject root = _session.Resolve(path);
            rootName = root.Name;
            tree = Describe(root, walk, 0);
        }

        // Serialising touches only the plain values just copied out of the tree,
        // so it happens outside the gate: that lock is global and every other
        // request queues behind it.
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            source = Uri.UnescapeDataString(path),
            exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            root = rootName,
            nodes = walk.Nodes,
            truncated = walk.Truncated,
            note = walk.Reason,
            data = tree,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private object? Describe(WzObject node, JsonWalk walk, int depth)
    {
        switch (node)
        {
            case WzIntProperty p: return p.Value;
            case WzShortProperty p: return p.Value;
            case WzLongProperty p: return p.Value;
            case WzFloatProperty p: return p.Value;
            case WzDoubleProperty p: return p.Value;
            case WzStringProperty p:
                // A Lua script or a long quest string is worth thousands of ints,
                // so its length is charged rather than counted as one node.
                walk.Charge(p.Value?.Length ?? 0);
                return p.Value;
            case WzUOLProperty p: return new { uol = p.Value };
            case WzVectorProperty p: return new { x = p.X?.Value ?? 0, y = p.Y?.Value ?? 0 };
            case WzNullProperty: return null;
            case WzCanvasProperty p:
                return new
                {
                    canvas = true,
                    width = p.PngProperty?.Width ?? 0,
                    height = p.PngProperty?.Height ?? 0,
                    format = p.PngProperty?.Format.ToString(),
                    children = DescribeChildren(node, walk, depth),
                };
            case WzBinaryProperty p:
                return new { sound = true, lengthMs = p.Length, type = p.SoundType.ToString() };
        }

        if (depth >= walk.MaxDepth)
            return new { truncated = true, note = $"depth limit {walk.MaxDepth} reached" };

        return DescribeChildren(node, walk, depth);
    }

    private Dictionary<string, object?>? DescribeChildren(WzObject node, JsonWalk walk, int depth)
    {
        if (depth >= walk.MaxDepth)
            return null;

        Dictionary<string, object?> result = new();
        int index = 0;
        foreach (WzObject child in _session.EnumerateChildren(node))
        {
            if (++index > MaxChildren)
            {
                result["__truncated"] = $"more than {MaxChildren} children";
                break;
            }
            string key = child.Name ?? "";
            // Duplicate names are legal in WZ but not in JSON.
            if (result.ContainsKey(key))
                key = $"{key}#{index}";

            // The per-level caps bound one level, not the walk. Once the whole
            // export's budget is spent it stays spent, so every open level closes
            // here rather than each of them starting a fresh 2000 children.
            if (!walk.Spend(key.Length))
            {
                result["__truncated"] = walk.Reason;
                break;
            }

            result[key] = Describe(child, walk, depth + 1);
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Budget for one JSON export: how much of the tree has been materialised so
    /// far, and why it stopped if it did.  Per-call rather than per-service so
    /// two concurrent exports cannot spend each other's allowance.
    /// </summary>
    private sealed class JsonWalk
    {
        public required int MaxDepth { get; init; }

        public int Nodes { get; private set; }
        public long Chars { get; private set; }

        /// <summary>Non-null once a budget ran out; also the message the payload carries.</summary>
        public string? Reason { get; private set; }

        public bool Truncated => Reason != null;

        /// <summary>Charges one node against the budget. False once it is spent.</summary>
        public bool Spend(int chars)
        {
            if (Reason != null)
                return false;

            Nodes++;
            Chars += chars;

            if (Nodes > MaxNodes)
                Reason = $"node limit {MaxNodes} reached";
            else if (Chars > MaxChars)
                Reason = $"size limit {MaxChars / (1024 * 1024)} MB reached";

            return Reason == null;
        }

        /// <summary>Charges bytes without counting a node, for oversized values.</summary>
        public void Charge(int chars) => Chars += chars;
    }

    /// <summary>Path below the export root, as a safe relative ZIP entry name.</summary>
    private static string RelativeName(string rootPath, string canvasPath)
    {
        string relative = canvasPath.StartsWith(rootPath + "/", StringComparison.Ordinal)
            ? canvasPath[(rootPath.Length + 1)..]
            : canvasPath;

        StringBuilder builder = new();
        foreach (string segment in relative.Split('/'))
        {
            if (builder.Length > 0)
                builder.Append('/');
            builder.Append(Sanitise(Uri.UnescapeDataString(segment)));
        }
        builder.Append(".png");
        return builder.ToString();
    }

    /// <summary>
    /// Windows device names, which stay reserved whatever extension follows —
    /// <c>NUL.png</c> is as unopenable as <c>NUL</c>.
    /// </summary>
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

    /// <summary>
    /// One WZ node name as one path segment that is safe to extract.
    ///
    /// Node names come out of a downloaded WZ file, so they are attacker-
    /// influenced even though nobody types them: <c>Path.GetInvalidFileNameChars</c>
    /// does not include '.', so a node called ".." survives intact and the entry
    /// "../foo.png" walks out of whatever directory the user unzipped into.
    /// Rewrites are per-character where possible so that two names that differ
    /// stay different — ".." and "." must not both become "_" and land on the
    /// same file. (<see cref="UniqueEntryName"/> is the backstop for the cases
    /// where they still collide.)
    /// </summary>
    internal static string Sanitise(string segment)
    {
        StringBuilder builder = new(segment.Length);
        foreach (char c in segment)
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        string cleaned = builder.ToString().Trim();

        // Trailing dots are both halves of the problem: they make "." and ".."
        // traversal segments, and Windows silently drops them when creating a
        // file, so "a." and "a" would otherwise be the same path on disk.
        int end = cleaned.Length;
        while (end > 0 && cleaned[end - 1] == '.')
            end--;
        if (end < cleaned.Length)
            cleaned = cleaned[..end] + new string('_', cleaned.Length - end);

        if (cleaned.Length == 0)
            return "_";

        // A reserved name cannot be created at all, so one bad node would fail
        // the extraction of the whole archive rather than just its own entry.
        int dot = cleaned.IndexOf('.');
        string stem = dot < 0 ? cleaned : cleaned[..dot];
        return ReservedNames.Contains(stem) ? "_" + cleaned : cleaned;
    }

    private static string UniqueEntryName(string name, HashSet<string> used)
    {
        if (used.Add(name))
            return name;

        string stem = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        int counter = 2;
        string candidate;
        do { candidate = $"{stem}_{counter++}.png"; } while (!used.Add(candidate));
        return candidate;
    }
}
