namespace MapleBench.Models;

/// <summary>
/// A request to create a brand-new, EMPTY archive container on disk.
/// </summary>
/// <remarks>
/// Own-design. Nothing in the product had this shape: every archive the app
/// touches already existed on disk, and the one constructor that could build
/// an archive from nothing (<c>new WzFile(gameVersion, version)</c>) was only
/// ever called on a tree that already had images in it.
/// </remarks>
public sealed class CreateEmptyArchiveRequest
{
    /// <summary>
    /// Absolute path of the container to create. The extension decides the
    /// format: <c>.wz</c> or <c>.nx</c>.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Optional. "wz" or "nx". When given it must agree with the extension,
    /// because the format of a container is decided by its magic and the magic
    /// is decided by the format -- a file called <c>x.nx</c> that holds a
    /// classic WZ is a file the app will not open.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// GMS / EMS / BMS / CLASSIC / GENERATE / CUSTOM. Null means BMS, the same
    /// default the loose-image reader uses.
    /// </summary>
    public string? MapleVersion { get; set; }

    /// <summary>
    /// Explicit 4-byte IV as hex ("4D23C72B"). Only meaningful for a classic
    /// <c>.wz</c>; an NX container is not encrypted.
    /// </summary>
    public string? Iv { get; set; }

    /// <summary>-1 = let MapleLib work the version hash out.</summary>
    public short GameVersion { get; set; } = -1;
}

/// <summary>What a create produced.</summary>
public sealed class CreateEmptyArchiveResult
{
    /// <summary>Absolute path of the file that now exists.</summary>
    public string Path { get; set; } = "";

    /// <summary>"wz" or "nx".</summary>
    public string Format { get; set; } = "";

    /// <summary>Size of the file on disk.</summary>
    public long Bytes { get; set; }

    /// <summary>
    /// Node records in the container. An NX always has at least one (the root);
    /// a classic WZ has none, because its nodes are images and an empty archive
    /// has no images.
    /// </summary>
    public int Nodes { get; set; }

    /// <summary>Images inside the container. Always zero: this is an empty one.</summary>
    public int Images { get; set; }

    /// <summary>The encryption the container was written with, for the record.</summary>
    public string MapleVersion { get; set; } = "";

    /// <summary>
    /// The game version the container was written with. A classic WZ stores a
    /// version hash derived from it, so opening the container back with a
    /// DIFFERENT game version writes it under a different hash the next time it
    /// is saved. This is the number to open it with.
    /// </summary>
    public short GameVersion { get; set; } = -1;
}
