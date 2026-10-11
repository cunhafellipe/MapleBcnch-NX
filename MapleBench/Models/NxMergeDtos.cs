namespace MapleBench.Models;

/// <summary>
/// One standalone <c>.img</c> to graft into an NX archive.
/// </summary>
/// <remarks>
/// The image keeps its own file name inside the archive — the source is
/// <c>.../Map9/910870003.img</c> and the target directory <c>Map/Map9</c> puts it
/// at <c>Map/Map9/910870003.img</c>. Deriving the name rather than taking a
/// second argument means the two cannot disagree about what was written.
/// </remarks>
public sealed class NxMergeEntry
{
    /// <summary>Absolute path to a standalone <c>.img</c> file.</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// Directory inside the NX, slash-separated, e.g. <c>Map/Map9</c> or
    /// <c>Obj</c>. It must already exist: this grafts images, it does not invent
    /// the archive's shape.
    /// </summary>
    public string Target { get; set; } = "";
}

/// <summary>
/// Graft one or more candidate IMGs into an existing NX archive.
/// </summary>
public sealed class NxMergeRequest
{
    /// <summary>Absolute path to the target <c>.nx</c> archive.</summary>
    public string TargetNx { get; set; } = "";

    /// <summary>The images to add or replace, in order.</summary>
    public List<NxMergeEntry> Entries { get; set; } = new();

    /// <summary>
    /// Copy the target aside under a timestamped name before the rewrite.
    /// Default on: the whole archive is rebuilt, so the only copy of what was
    /// there is the backup.
    /// </summary>
    public bool Backup { get; set; } = true;

    /// <summary>
    /// <c>false</c> reports what would be added or replaced and writes nothing.
    /// The archive is still opened and inspected, so the answer is real rather
    /// than a guess.
    /// </summary>
    public bool Commit { get; set; }
}

/// <summary>What one entry did, or would do.</summary>
public sealed class NxMergeEntryResult
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>The archive path the image landed at, e.g. <c>Map/Map9/910870003.img</c>.</summary>
    public string ImagePath { get; set; } = "";
    /// <summary><c>added</c> or <c>replaced</c>.</summary>
    public string Action { get; set; } = "";
}

/// <summary>The outcome of a merge.</summary>
public sealed class NxMergeResult
{
    public string TargetNx { get; set; } = "";
    public bool Committed { get; set; }
    public List<NxMergeEntryResult> Entries { get; set; } = new();
    public string? BackupPath { get; set; }
    public long? Bytes { get; set; }
    public int? Nodes { get; set; }
    public int? Images { get; set; }
    public int? Canvases { get; set; }
    /// <summary>True when the written bytes were re-opened and the entries found in them.</summary>
    public bool Verified { get; set; }
    public string Note { get; set; } = "";
}
