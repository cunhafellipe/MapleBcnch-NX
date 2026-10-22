namespace MapleBench.Services.Look;

/// <summary>
/// One composed look over the wire.
///
/// </summary>
/// <remarks>
/// The shapes are deliberately flat and every number the reference derives is
/// carried, because a caller that has to re-derive the head offset is a caller
/// that can get it wrong a second time. The anchors are sent as
/// <c>{x, y}</c> rather than as a flat number so the browser cannot transpose
/// them without being caught by a shape check.
/// </remarks>
public sealed class LookDto
{
    public string Stance { get; set; } = "";
    public int Frame { get; set; }
    public int Delay { get; set; }

    /// <summary>How many frames this pose carries.</summary>
        public int FrameCount { get; set; } = 1;

        /// <summary>
        /// False when the body sheet carries no frame at the requested index.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this had to be said out loud.</b> A request for a frame past
        /// the end of a pose used to come back HTTP 200 carrying exactly ONE part:
        /// the face marker. That is not a mostly-right answer, it is a face floating
        /// where a body should be, and to a caller that only checks "did I get any
        /// layers" it is indistinguishable from success.</para>
        ///
        /// <para>The reference never asks, because <c>BodyDrawInfo::nextframe</c>
        /// (BodyDrawInfo.cpp:194-200) wraps to frame 0 when the next frame is
        /// absent. This is that same guard expressed as data, because the caller of
        /// an HTTP endpoint is not <c>nextframe</c> and has no way to know the
        /// archive's frame numbering.</para></remarks>
        public bool FrameExists { get; set; } = true;

        /// <summary>
        /// False when the body sheet has no such stance AT ALL, as opposed to a
        /// stance whose requested frame is out of range.
        /// </summary>
        /// <remarks>
        /// These are different mistakes and they were reporting the same thing. The
        /// sweep that produced "58 of 80 poses return nothing" could not tell
        /// <c>sitted</c> (a name the archive has never heard of) from
        /// <c>stand1/7</c> (a real pose asked past its end), because both returned
        /// an empty part list and nothing said which.</remarks>
        public bool StanceExists { get; set; } = true;

    /// <summary>
    /// Every frame of the pose, each with its own anchors and its own paint
    /// order. A pose whose frame 1 has a different navel from its frame 0 is
    /// composed differently, and sending only one frame would freeze the
    /// character in frame 0's stance for the whole animation.
    /// </summary>
    public List<LookFrameDto> Frames { get; set; } = [];

    public PointDto BodyAnchor { get; set; } = new();
    public PointDto HeadAnchor { get; set; } = new();
    public PointDto FaceAnchor { get; set; } = new();
    public PointDto HairAnchor { get; set; } = new();
    public PointDto ArmAnchor { get; set; } = new();
    public PointDto HandAnchor { get; set; } = new();

    public bool ShowsFace { get; set; }

    /// <summary>Back to front; index 0 is furthest back. The paint order.</summary>
    public List<LookPartDto> Parts { get; set; } = [];

    /// <summary>Parts slot arbitration removed, named. Reported, never silent.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>
    /// The slots actually worn, by slot name. Sent so a caller can tell "the cap
    /// I asked for was not resolved" from "the cap resolved and drew nothing".
    /// </summary>
    public List<string> Equipped { get; set; } = [];

    /// <summary>
    /// Every slot that was asked for and could not be worn, one line each, saying
    /// why. A slot map with a typo in it must not quietly compose a bare
    /// character — that is the exact failure this whole task exists to stop.
    /// </summary>
    public List<string> Unresolved { get; set; } = [];

    public BoxDto Bounds { get; set; } = new();

    /// <summary>How many zmap ranks were available. 0 means no Base.nx was open.</summary>
    public int ZmapCount { get; set; }

    /// <summary>
    /// How many layer names carried slot codes. 0 with a non-zero
    /// <see cref="ZmapCount"/> means smap.img was absent, and every part's mask
    /// is a zero for want of a table rather than because nothing conflicts —
    /// a distinction the reader cannot make from the masks alone.
    /// </summary>
    public int SmapCount { get; set; }

    /// <summary>
    /// One line per archive the tables were looked for in, and what each one
    /// yielded. Present so that "no slot masks in the archive" is a statement
    /// with evidence behind it rather than an inference from zeros.
    /// </summary>
    public List<string> TableScan { get; set; } = [];

    /// <summary>The body node this look was composed from.</summary>
    public string Source { get; set; } = "";
}

/// <summary>One frame of a composed pose.</summary>
public sealed class LookFrameDto
{
    public int Frame { get; set; }
    public int Delay { get; set; }
    public PointDto BodyAnchor { get; set; } = new();
    public PointDto HeadAnchor { get; set; } = new();
    public PointDto FaceAnchor { get; set; } = new();
    public PointDto HairAnchor { get; set; } = new();
    public PointDto ArmAnchor { get; set; } = new();
    public PointDto HandAnchor { get; set; } = new();
    public bool ShowsFace { get; set; }
    public List<LookPartDto> Parts { get; set; } = [];
    public BoxDto Bounds { get; set; } = new();
}

public sealed class LookPartDto
{
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public PointDto Shift { get; set; } = new();

    /// <summary>Origin + shift: where the part's own origin lands, relative to the character.</summary>
    public PointDto Screen { get; set; } = new();

    /// <summary>The zmap rank, read from <c>Base/zmap.img</c> by layer name.</summary>
    public int Z { get; set; }

    /// <summary>That layer NAME. NX stores z as a string, never a number.</summary>
    public string ZName { get; set; } = "";

    /// <summary>
    /// The slot codes this part actually holds, as the two-letter codes they are
    /// in the archive. Empty means the mask is a zero — either the layer claimed
    /// nothing, or it claimed only what the owner's vslot does not permit, which
    /// is the intersection AvatarLayers::claimed performs.
    /// </summary>
    public string Slots { get; set; } = "";

    /// <summary>
    /// Those codes as a 64-bit mask, numbered in the first-seen order Tables::mask
    /// assigns. Zero is a real answer when <see cref="Slots"/> is empty.
    /// </summary>
    public ulong SlotBits { get; set; }

    /// <summary>
    /// The smallest zmap rank among the owner's islot codes; lower is stronger
    /// (AvatarLayers::priority_of). This is what decides a contested slot.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>body / head / hair / face — which anchor applied.</summary>
    public string Owner { get; set; } = "";

    public string Part { get; set; } = "";
}

public sealed class PointDto
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class BoxDto
{
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
}