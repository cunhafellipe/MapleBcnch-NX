using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services.Nx;

namespace MapleBench.Services;

public sealed class AnimationFrameDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Origin as stored on the frame.</summary>
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    /// <summary>Where to blit this frame inside the composed canvas.</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    /// <summary>Milliseconds to hold this frame.</summary>
    public int Delay { get; set; }
    /// <summary>
    /// Whether <see cref="Delay"/> came out of the archive or is the reader's fallback.
    /// </summary>
    /// <remarks>
    /// Without this the two are indistinguishable, and an archive that omits `delay`
    /// reads exactly like one that states 100 -- which is what makes a stance play at
    /// the wrong speed with nothing to point at. Measured: <c>f7/0100100.img/stand/0</c>
    /// carries <c>origin</c>/<c>lt</c>/<c>rb</c>/<c>head</c> and no <c>delay</c>, while
    /// <c>f2/00002000.img/stand1/0</c> carries one. Single-frame stances legitimately
    /// omit it, so this is information, not a defect.
    /// </remarks>
    public bool DelayFromArchive { get; set; }
    /// <summary>
    /// The canvases stacked inside this frame, back to front.
    /// </summary>
    /// <remarks>
    /// Empty for a WZ frame, which is a single canvas. NX stores a frame as a container of
    /// canvases — <c>stand1/0</c> holds <c>arm</c> over <c>body</c> — so a caller that draws
    /// NX art has to stack them itself; the <c>Width</c>/<c>Height</c>/<c>OffsetX</c>/
    /// <c>OffsetY</c> on this frame already describe the whole stack.
    /// </remarks>
    public List<AnimationLayerDto> Layers { get; set; } = new();
}

/// <summary>One canvas within a stacked frame.</summary>
public sealed class AnimationLayerDto
{
    public string Name { get; set; } = "";
    /// <summary>Session path of the canvas, so the caller can fetch it from /api/canvas.</summary>
    public string Path { get; set; } = "";
    /// <summary>The canvas's <c>z</c> layer name, when it has one.</summary>
    public string? Layer { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
}

public sealed class AnimationDto
{
    public bool IsAnimation { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Composed canvas size that fits every frame at its anchor.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int TotalMs { get; set; }
    public List<AnimationFrameDto> Frames { get; set; } = new();
}

/// <summary>
/// Recognises the WZ animation convention -- a container whose children are
/// numerically named canvases -- and works out how to play it.
///
/// Frames in a MapleStory animation are cropped differently and are positioned
/// by their 'origin' vector, not by a shared top-left. Blitting them all at
/// (0,0) makes the sprite jitter, which is why every frame carries an explicit
/// offset here rather than leaving the client to guess.
/// </summary>
public sealed class AnimationService
{
    private readonly WzSessionService _session;

    public AnimationService(WzSessionService session)
    {
        _session = session;
    }

    public AnimationDto Describe(string path)
    {
        lock (_session.Gate)
        {
            WzObject node = _session.Resolve(path);
            AnimationDto result = new() { Path = path };

            // Frames are the numerically-named canvas children, in numeric order.
            List<(int Number, WzCanvasProperty Canvas, string Name)> frames = new();

            // NX keeps one more level than WZ does: an action's numbered children are
            // FRAME CONTAINERS, and the canvases are their children. See CollectFrameLayers.
            List<(int Number, FrameLayers Layers, string Name)> nxFrames = new();

            int occurrence = 0;
            Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (WzObject child in _session.EnumerateChildren(node))
            {
                string name = child.Name ?? "";
                seen.TryGetValue(name, out occurrence);
                seen[name] = occurrence + 1;

                if (!int.TryParse(name, out int number))
                    continue;

                // A UOL frame points at a canvas elsewhere; follow it so linked
                // animations still play.
                WzImageProperty? resolved = child as WzImageProperty;
                if (resolved is WzUOLProperty uol)
                    resolved = uol.LinkValue as WzImageProperty;

                string framePath = WzPath.Child(path, name, occurrence);

                if (resolved is WzCanvasProperty canvas)
                    frames.Add((number, canvas, framePath));
                else if (resolved is WzImageProperty container)
                {
                    // The layer paths hang off the FRAME, not off the action. Passing the
                    // action's own path here produced "stand1/body" for every frame, a URL
                    // that resolves to nothing -- /api/canvas answered 404 for all three
                    // frames and the browser drew a body-shaped hole. The frame path is the
                    // one the sibling branch already builds, so both now come from one call.
                    FrameLayers? layers = CollectFrameLayers(container, framePath);
                    if (layers is not null)
                        nxFrames.Add((number, layers, framePath));
                }
            }

            // A numbered child that is a container and a numbered child that is a canvas are
            // never mixed in one archive — WZ frames are canvases, NX frames are containers —
            // so preferring whichever produced frames is not a guess between two readings of
            // the same node.
            if (nxFrames.Count > 0 && frames.Count == 0)
            {
                return DescribeNxFrames(node, path, nxFrames);
            }

// A single canvas is a picture, not an animation -- but the caller
            // still has to draw something, and a response of all zeroes gives it
            // nothing to size a viewport with. Report the one canvas's own
            // dimensions (or the node's, when the node is itself a canvas).
            //
            // It is reported as a ONE-FRAME animation rather than as a bare size,
            // because a mob's `stand` and `hit1` are exactly this shape: one
            // numbered canvas, no siblings (measured on Mob.nx -- 100100's stand
            // has a single frame and its hit1 a single frame). A caller playing a
            // state machine needs the frame's PATH to blit, and a size-only answer
            // leaves it with nothing to draw. `isAnimation` is false in this case
            // so a caller that only wanted a thumbnail is not told to loop.
            if (frames.Count < 2)
            {
                WzCanvasProperty? only = frames.Count == 1
                    ? frames[0].Canvas
                    : node as WzCanvasProperty;

                if (only is not null)
                {
                    System.Drawing.PointF onlyOrigin = SafeOrigin(only);
                    (result.Width, result.Height) = EffectiveSize(only);
                    result.AnchorX = (int)onlyOrigin.X;
                    result.AnchorY = (int)onlyOrigin.Y;
                    result.TotalMs = ReadDelay(only);
                    bool onlyHasDelay = TryReadDelay(only, out _);
                    if (frames.Count == 1)
                    {
                        result.Frames.Add(new AnimationFrameDto
                        {
                            Index = frames[0].Number,
                            Name = frames[0].Number.ToString(),
                            Path = frames[0].Name,
                            Width = result.Width,
                            Height = result.Height,
                            OriginX = result.AnchorX,
                            OriginY = result.AnchorY,
                            OffsetX = 0,
                            OffsetY = 0,
                            Delay = result.TotalMs,
                            DelayFromArchive = onlyHasDelay,
                        });
                    }
                }
                return result;
            }

            frames.Sort((a, b) => a.Number.CompareTo(b.Number));
            result.IsAnimation = true;

            // Pass 1: read each frame's size, origin and delay.
            List<AnimationFrameDto> dtos = new(frames.Count);
            foreach ((int number, WzCanvasProperty canvas, string framePath) in frames)
            {
                System.Drawing.PointF origin = SafeOrigin(canvas);
                (int width, int height) = EffectiveSize(canvas);
                dtos.Add(new AnimationFrameDto
                {
                    Index = number,
                    Name = number.ToString(),
                    Path = framePath,
                    Width = width,
                    Height = height,
                    OriginX = (int)origin.X,
                    OriginY = (int)origin.Y,
                    Delay = ReadDelay(canvas),
                    DelayFromArchive = TryReadDelay(canvas, out _),
                });
            }

            // Pass 2: place every frame so their origins coincide.
            (result.AnchorX, result.AnchorY, result.Width, result.Height, result.TotalMs) =
                PlaceAtSharedOrigin(dtos);
            result.Frames = dtos;
            return result;
        }
    }

    /// <summary>
    /// One NX frame: the canvases it stacks, and how long it holds.
    /// </summary>
    /// <remarks>
    /// An NX frame is a container, not a picture. <c>stand1/0</c> holds <c>arm</c>,
    /// <c>body</c> and a <c>delay</c>; each canvas is a layer of the same instant, stacked
    /// by its own <c>z</c> child and placed by its own <c>origin</c>. Reading a frame as one
    /// picture is what produced a 0x0 answer for every NX action: the container is not a
    /// canvas, so nothing in it was ever counted.
    /// </remarks>
    private sealed class FrameLayers
    {
        public required List<AnimationLayerDto> Layers { get; init; }

        public required int Delay { get; init; }

        /// <summary>Whether <see cref="Delay"/> was stated by the archive.</summary>
        public required bool DelayFromArchive { get; init; }
    }

    /// <summary>
    /// Reads the canvases stacked inside one NX frame container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns null for a container holding no canvas at all. That is a real answer and it
    /// is not silently turned into an empty frame: a caller would then be told a frame
    /// exists with nothing on it, which draws identically to no frame existing while
    /// claiming otherwise.
    /// </para>
    /// <para>
    /// The container's own <c>delay</c> is the frame duration. It lives on the container and
    /// not on any canvas inside it — verified against the bytes, <c>stand1/0/delay</c> = 500
    /// with no <c>delay</c> under <c>stand1/0/body</c> — and the reference reads it from the
    /// same level (MapleCharacter.cs:395, <c>Iter2.GetInt("delay")</c>). Reading it from a
    /// canvas instead is what produced 100 ms for every NX frame, which is only the
    /// no-delay fallback and not the archive's timing.
    /// </para>
    /// <para>
    /// Layers are ordered back-to-front by <see cref="NxLayerOrder.Compare"/>, which is the
    /// order they have to be <em>painted</em> in. The reference builds a painter's list
    /// rather than a z-sorted one (MapleCharacter.cs:1236), so this list is drawing order
    /// and index 0 is the furthest back.
    /// </para>
    /// <para>
    /// <c>parentPath</c> is the path of THIS FRAME (<c>stand1/0</c>), not of the action
    /// (<c>stand1</c>), because it is the address each layer's canvas is served under. A
    /// frame and its siblings hold same-named canvases -- frame 0's body and frame 1's body
    /// are both "body" -- so the frame is the only thing that tells them apart, and a layer
    /// path built from the action alone is a URL every frame shares and none of them answers.
    /// </para>
    /// </remarks>
    private FrameLayers? CollectFrameLayers(WzImageProperty container, string parentPath)
    {
        List<AnimationLayerDto> layers = new();
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WzObject child in _session.EnumerateChildren(container))
        {
            string childName = child.Name ?? "";
            seen.TryGetValue(childName, out int childOccurrence);
            seen[childName] = childOccurrence + 1;

            WzImageProperty? resolved = child as WzImageProperty;
            if (resolved is WzUOLProperty layerUol)
            {
                try { resolved = layerUol.LinkValue as WzImageProperty; }
                catch { /* a broken link is not a layer this frame can draw */ }
            }

            if (resolved is not WzCanvasProperty canvas)
            {
                continue;
            }

            System.Drawing.PointF origin = SafeOrigin(canvas);
            (int width, int height) = EffectiveSize(canvas);
            layers.Add(new AnimationLayerDto
            {
                Name = childName,
                Path = WzPath.Child(parentPath, childName, childOccurrence),
                Layer = ReadLayerName(canvas),
                Width = width,
                Height = height,
                OriginX = (int)origin.X,
                OriginY = (int)origin.Y,
            });
        }

        if (layers.Count == 0)
        {
            return null;
        }

        layers.Sort((a, b) => NxLayerOrder.Compare(a.Layer, b.Layer));

        return new FrameLayers
        {
            Layers = layers,
            Delay = ReadDelay(container),
            DelayFromArchive = TryReadDelay(container, out _),
        };
    }

    /// <summary>
    /// Builds the animation response for an NX action from its frame containers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frames' sizes and offsets are computed over every layer of every frame, so the
    /// composed box is the union of all of them at their shared anchor — the same
    /// composition rule the WZ path uses, applied to the layers instead of the frames. A
    /// one-canvas action is still an animation: <c>sit</c> and <c>jump</c> each hold a
    /// single frame, and the client treats those as poses rather than looping art.
    /// </para>
    /// <para>
    /// The anchor is the largest origin across every layer of every frame. Layers of one
    /// frame are drawn at <c>anchor - layer.origin</c>, so each canvas lands where its own
    /// origin says it should, and a frame whose body and arm have different origins still
    /// lines up.
    /// </para>
    /// </remarks>
    private AnimationDto DescribeNxFrames(WzObject node, string path, List<(int Number, FrameLayers Layers, string Name)> nxFrames)
    {
        AnimationDto result = new() { Path = path };
        nxFrames.Sort((a, b) => a.Number.CompareTo(b.Number));

        int anchorX = nxFrames
            .SelectMany(f => f.Layers.Layers)
            .Max(l => l.OriginX);
        int anchorY = nxFrames
            .SelectMany(f => f.Layers.Layers)
            .Max(l => l.OriginY);

        List<AnimationFrameDto> dtos = new(nxFrames.Count);
        foreach ((int number, FrameLayers frame, string framePath) in nxFrames)
        {
            // One entry per frame, carrying every layer. Width/height/offset describe the
            // whole stacked frame, so the existing shared-origin rule stays in step
            // unchanged and a caller that composes frames itself needs no NX special case.
            int frameWidth = frame.Layers.Max(l => anchorX - l.OriginX + l.Width);
            int frameHeight = frame.Layers.Max(l => anchorY - l.OriginY + l.Height);

            dtos.Add(new AnimationFrameDto
            {
                Index = number,
                Name = number.ToString(),
                Path = framePath,
                Width = frameWidth,
                Height = frameHeight,
                OffsetX = 0,
                OffsetY = 0,
                Delay = frame.Delay,
                DelayFromArchive = frame.DelayFromArchive,
                Layers = frame.Layers,
            });
        }

        result.IsAnimation = dtos.Count > 0;
        result.AnchorX = anchorX;
        result.AnchorY = anchorY;
        result.Width = dtos.Max(d => d.OffsetX + d.Width);
        result.Height = dtos.Max(d => d.OffsetY + d.Height);
        result.TotalMs = dtos.Sum(d => d.Delay);
        result.Frames = dtos;
        return result;
    }

    /// <summary>
    /// A canvas's <c>z</c> child as a layer name, or null when it has none.
    /// </summary>
    /// <remarks>
    /// NX stores it as a string (<c>"body"</c>, <c>"armOverHair"</c>) and map tiles store an
    /// integer instead, so both shapes are accepted and the integer one yields null — a tile
    /// layer has no entry in the z map, and the fallback ordering is the honest answer.
    /// </remarks>
    private static string? ReadLayerName(WzCanvasProperty canvas)
    {
        try
        {
            if (canvas["z"] is WzStringProperty layer)
            {
                return layer.Value;
            }
        }
        catch { /* no usable z property */ }
        return null;
    }

    /// <summary>
    /// Frame duration in ms, read from a frame container rather than a canvas.
    /// </summary>
    /// <param name="container">
    /// An NX frame container, or the canvas standing in for a WZ frame.
    /// </param>
    /// <remarks>
    /// The same rule as <see cref="ReadDelay(WzCanvasProperty)"/>, reached through
    /// <see cref="WzProperties"/> because an NX frame's <c>delay</c> is a sibling of its
    /// canvases, not a child of one.
    /// </remarks>
    private static int ReadDelay(WzImageProperty container)
    {
        TryReadDelay(container, out int delay);
        return delay;
    }

    /// <summary>
    /// The NX container read, also reporting whether the archive stated the delay.
    /// </summary>
    private static bool TryReadDelay(WzImageProperty container, out int delay)
    {
        delay = DefaultDelayMs;
        try
        {
            if (container.WzProperties?.FindByName(WzCanvasProperty.AnimationDelayPropertyName) is { } prop
                && prop.GetInt() is int value
                && value > 0)
            {
                delay = value;
                return true;
            }
        }
        catch { /* no usable delay property */ }
        return false;
    }

    /// <summary>
    /// The composition rule itself, on its own so every caller that plays or
    /// exports an animation places frames the same way — the dumper's GIF/APNG
    /// routes go through <see cref="Describe"/> and the generation chooser's
    /// preview calls this directly on nodes it read outside the session.
    ///
    /// The shared anchor is the largest origin on each axis, so the per-frame
    /// offset (anchor - origin) is always &gt;= 0 and no frame needs negative
    /// coordinates. Mutates each frame's OffsetX/OffsetY in place and returns
    /// the composed canvas: anchor, size that fits every frame at its offset,
    /// and total duration.
    /// </summary>
    public static (int AnchorX, int AnchorY, int Width, int Height, int TotalMs)
        PlaceAtSharedOrigin(IReadOnlyList<AnimationFrameDto> frames)
    {
        if (frames.Count == 0) return (0, 0, 0, 0, 0);

        int anchorX = frames.Max(f => f.OriginX);
        int anchorY = frames.Max(f => f.OriginY);

        foreach (AnimationFrameDto frame in frames)
        {
            frame.OffsetX = anchorX - frame.OriginX;
            frame.OffsetY = anchorY - frame.OriginY;
        }

        return (anchorX, anchorY,
                frames.Max(f => f.OffsetX + f.Width),
                frames.Max(f => f.OffsetY + f.Height),
                frames.Sum(f => f.Delay));
    }

    /// <summary>
    /// Reads a frame's own metadata — size, origin, delay — the way the session
    /// path does, for canvases reached outside the session. The link-following
    /// half of <see cref="EffectiveSize"/> is deliberately NOT here: a chooser
    /// previewing donor art reads nodes whose links resolve against a different
    /// family mount than the session's, and a preview must not silently draw
    /// whatever a link happens to reach.
    /// </summary>
    public static AnimationFrameDto DescribeCanvas(WzCanvasProperty canvas, string name)
    {
        System.Drawing.PointF origin = SafeOrigin(canvas);
        return new AnimationFrameDto
        {
            Name = name,
            Width = canvas.PngProperty?.Width ?? 0,
            Height = canvas.PngProperty?.Height ?? 0,
            OriginX = (int)origin.X,
            OriginY = (int)origin.Y,
            Delay = ReadDelay(canvas),
            DelayFromArchive = TryReadDelay(canvas, out _),
        };
    }

    /// <summary>
    /// Finds canvases anywhere beneath a node so the inspector can show what a
    /// branch contains without the user expanding down to each leaf.
    ///
    /// Breadth-first and capped: the point is a quick contact sheet, not a full
    /// enumeration of an .img holding thousands of sprites.
    /// </summary>
    public List<AnimationFrameDto> CollectCanvases(string path, int limit = 60, int maxVisit = 4000, int maxDepth = 8)
        => CollectCanvases(path, out _, limit, maxVisit, maxDepth);

    /// <summary>
    /// As above, but says whether any of the three caps stopped the sweep early.
    ///
    /// Callers that publish a result -- the ZIP export's manifest -- cannot work
    /// this out from the returned count: the visit and depth caps can both fire
    /// long before the entry cap, which made a partial export look complete.
    ///
    /// It covers all four ways the sweep can come back short -- the visit cap, the
    /// depth cap, the entry cap, and a subtree that threw on the way in -- because
    /// a caller cannot act on a flag that covers only three of them.
    /// </summary>
    public List<AnimationFrameDto> CollectCanvases(
        string path, out bool truncated, int limit = 60, int maxVisit = 4000, int maxDepth = 8)
    {
        lock (_session.Gate)
        {
            WzObject root = _session.Resolve(path);
            List<AnimationFrameDto> found = new();

            Queue<(WzObject Node, string Path, int Depth)> queue = new();
            queue.Enqueue((root, path, 0));
            int visited = 0;
            bool stopped = false;

            while (queue.Count > 0 && found.Count < limit)
            {
                (WzObject node, string nodePath, int depth) = queue.Dequeue();

                // Guard against sweeping an entire archive from a directory node.
                // Breaking on depth is safe rather than lossy: the queue is FIFO,
                // so everything left in it is at least this deep.
                if (++visited > maxVisit || depth > maxDepth)
                {
                    stopped = true;
                    break;
                }

                Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);
                List<WzObject> children;
                try { children = _session.EnumerateChildren(node).ToList(); }
                catch
                {
                    // A branch this sweep could not read is a branch it did not
                    // examine, and the caller has to be able to tell that from a
                    // branch that held nothing. Without this the flag below said
                    // "complete" for a sweep that skipped a whole subtree -- and
                    // that flag is written verbatim into the exported ZIP's
                    // manifest as 'truncated', where it is the only thing telling
                    // a user whether the dump is all of their sprites.
                    stopped = true;
                    continue;
                }

                foreach (WzObject child in children)
                {
                    string name = child.Name ?? "";
                    seen.TryGetValue(name, out int occurrence);
                    seen[name] = occurrence + 1;
                    string childPath = WzPath.Child(nodePath, name, occurrence);

                    // A UOL can be the drawable leaf itself. Item.wz uses this
                    // for ranked/variant items whose info/icon points at the
                    // first item's canvas (for example 02431008 -> 02431007).
                    // Keep the UOL's own session path in the DTO so callers can
                    // render and address the node the user actually selected;
                    // WzRenderService follows the link when it decodes it.
                    WzCanvasProperty? canvas = child as WzCanvasProperty;
                    if (canvas == null && child is WzUOLProperty uol)
                    {
                        try { canvas = uol.LinkValue as WzCanvasProperty; }
                        catch { /* a broken UOL is not drawable */ }
                    }

                    if (canvas != null)
                    {
                        if (found.Count >= limit)
                        {
                            stopped = true;
                            break;
                        }
                        System.Drawing.PointF origin = SafeOrigin(canvas);
                        (int width, int height) = EffectiveSize(canvas);
                        found.Add(new AnimationFrameDto
                        {
                            Name = name,
                            Path = childPath,
                            Width = width,
                            Height = height,
                            OriginX = (int)origin.X,
                            OriginY = (int)origin.Y,
                            Delay = ReadDelay(canvas),
                            DelayFromArchive = TryReadDelay(canvas, out _),
                        });
                        // A canvas can still hold sub-properties, but they are
                        // metadata rather than more sprites.
                        continue;
                    }

                    // Do not descend into unparsed images from a directory sweep;
                    // that would parse the whole archive to draw a thumbnail strip.
                    if (child is WzImage image && node is WzDirectory && !image.Parsed)
                        continue;

                    queue.Enqueue((child, childPath, depth + 1));
                }
            }

            // Anything still queued is something the caps stopped us reaching.
            truncated = stopped || queue.Count > 0;
            return found;
        }
    }

    /// <summary>
    /// The drawn size of a canvas, following '_inlink'/'_outlink'.
    ///
    /// Linked frames store a 1x1 (or empty) placeholder PNG and keep the real
    /// pixels elsewhere — most of Mob.wz is built this way. Reading the
    /// placeholder's dimensions would size the composed animation canvas to a
    /// single pixel and scatter every frame.
    ///
    /// What makes a canvas a placeholder is the '_inlink'/'_outlink' child, not
    /// its size: a 1x1 canvas with no link is a genuine one-pixel sprite (and is
    /// exactly what WzNodeFactory seeds a new canvas with), and it has no other
    /// size to report anyway. Conversely a linked canvas is drawn from its link
    /// whatever its own dimensions claim — <see cref="WzRenderService"/> always
    /// follows the link — so the size has to be read from the same place.
    /// </summary>
    private static (int Width, int Height) EffectiveSize(WzCanvasProperty canvas)
    {
        int width = canvas.PngProperty?.Width ?? 0;
        int height = canvas.PngProperty?.Height ?? 0;

        try
        {
            if (!canvas.ContainsInlinkProperty() && !canvas.ContainsOutlinkProperty())
                return (width, height);

            if (canvas.GetLinkedWzImageProperty() is WzCanvasProperty linked
                && !ReferenceEquals(linked, canvas)
                && linked.PngProperty is { Width: > 0, Height: > 0 })
            {
                return (linked.PngProperty.Width, linked.PngProperty.Height);
            }
        }
        catch { /* broken link: fall back to what the placeholder claims */ }

        return (width, height);
    }

    /// <summary>
    /// MapleLib's origin helper throws when the property is missing, so read it
    /// defensively — plenty of frames have no origin at all.
    /// </summary>
    private static System.Drawing.PointF SafeOrigin(WzCanvasProperty canvas)
    {
        try
        {
            if (canvas[WzCanvasProperty.OriginPropertyName] is WzVectorProperty vector)
                return new System.Drawing.PointF(vector.X?.Value ?? 0, vector.Y?.Value ?? 0);
        }
        catch { /* fall through to the origin-less default */ }
        return System.Drawing.PointF.Empty;
    }

    /// <summary>
    /// Frame duration in ms.  Clients treat a missing or zero delay as 100 ms,
    /// so matching that keeps playback looking like the game.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="ReadDelay(WzImageProperty)"/>, which reads the same
    /// property from an NX frame container. One constant, because the 100 ms is the
    /// client's own fallback and two different numbers here would make a frame with no
    /// delay play at a different rate depending on which shape it was read from.
    /// </remarks>
    private const int DefaultDelayMs = 100;

    private static int ReadDelay(WzCanvasProperty canvas)
    {
        TryReadDelay(canvas, out int delay);
        return delay;
    }

    /// <summary>
    /// The same read as <see cref="ReadDelay(WzCanvasProperty)"/>, also reporting
    /// whether the archive actually stated the delay.
    /// </summary>
    private static bool TryReadDelay(WzCanvasProperty canvas, out int delay)
    {
        delay = DefaultDelayMs;
        try
        {
            if (canvas[WzCanvasProperty.AnimationDelayPropertyName] is { } prop)
            {
                int value = prop.GetInt();
                if (value > 0)
                {
                    delay = value;
                    return true;
                }
            }
        }
        catch { /* no usable delay property */ }
        return false;
    }
}
