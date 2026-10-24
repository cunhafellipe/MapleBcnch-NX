using MapleBench.Models;
using MapleBench.Services.Nx;
using MapleLib.WzLib;

namespace MapleBench.Services;

/// <summary>
/// Creates a brand-new, EMPTY archive container on disk.
/// </summary>
/// <remarks>
/// <para>
/// Own-design: there is no upstream to port. The two pieces this needs already
/// existed and are used as they are -- MapleLib can build an empty archive in
/// memory (<c>new WzFile(gameVersion, version)</c>, WzFile.cs:132) and write it
/// out (<c>WzFile.SaveToDisk</c>, WzFile.cs:560), and the app can write an NX
/// (<c>NxWriter.Write</c>, NxWriter.cs:335). What did not exist was an ENTRY
/// POINT: every caller of both had an archive already open, so nothing in the
/// product could start from nothing on purpose.
/// </para>
/// <para>
/// The reason that matters is the live data root. An agent that wanted a clean
/// archive to build a fixture in had to carve one out of a real client folder,
/// which means writing into the folder the game is running against. This
/// service gives the agent somewhere else to work, and refuses the folder the
/// open archives live in.
/// </para>
/// </remarks>
public sealed class EmptyArchiveService
{
    private readonly WzSessionService _session;

    public EmptyArchiveService(WzSessionService session)
    {
        _session = session;
    }

    /// <summary>
    /// Creates an empty container and returns what was written.
    /// </summary>
    /// <exception cref="ArgumentException">The request does not describe a container.</exception>
    /// <exception cref="InvalidOperationException">The request was refused, or the container could not be written and read back.</exception>
    public CreateEmptyArchiveResult Create(CreateEmptyArchiveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("A target path is required: there is nowhere to create the container.");

        string target;
        try
        {
            target = Path.GetFullPath(request.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{request.Path}' is not a usable path: {ex.Message}");
        }

        // The extension decides the format, because the format is decided by the
        // magic and the magic has to be written by the matching writer. A file
        // called x.nx that holds a classic WZ is a file the app refuses to open
        // (WzSessionService.Open -> NxArchive.IsNxFile), so a mismatch is
        // refused here rather than created and discovered later.
        string extension = Path.GetExtension(target).ToLowerInvariant();
        string format = extension switch
        {
            ".wz" => "wz",
            ".nx" => "nx",
            _ => throw new ArgumentException(
                $"'{Path.GetFileName(target)}' does not end in .wz or .nx, so there is no format to write."),
        };
        if (!string.IsNullOrWhiteSpace(request.Format)
            && !request.Format.Trim().Equals(format, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The format '{request.Format}' does not match the file name: '{Path.GetFileName(target)}' " +
                $"is a {format.ToUpperInvariant()} container.");
        }

        // Never overwrite. An existing file is the user's -- it may be a real
        // archive, or a container this agent made an hour ago and has since
        // built fixtures into. Either way, replacing it silently is the one
        // outcome nobody asked for.
        if (File.Exists(target))
        {
            throw new InvalidOperationException(
                $"'{target}' already exists. This creates a NEW container and never overwrites one; " +
                "delete it yourself if you meant to start again.");
        }

        string? liveFolder = LiveArchiveFolderHolding(target);
        if (liveFolder != null)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(target)}' would be created inside '{liveFolder}', which is where an open " +
                "archive lives. That folder is the client the app is working against, and a scratch container " +
                "written there is a file in the live data root. Create the container somewhere else.");
        }

        WzMapleVersion version = WzSessionService.ParseVersion(request.MapleVersion) ?? WzMapleVersion.BMS;
        byte[]? customIv = WzSessionService.ParseIv(request.Iv);
        if (customIv != null)
        {
            version = WzMapleVersion.CUSTOM;
        }

        string directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException($"'{target}' has no folder to be created in.");
        Directory.CreateDirectory(directory);

        // A scratch name, because MapleLib's SaveToDisk derives its own scratch
        // file from the file name alone and drops it in the working directory:
        // two containers with the same name created at the same time would
        // truncate each other's scratch. The GUID stem makes that impossible.
        string scratch = Path.Combine(directory, $"empty-{Guid.NewGuid():N}.{format}");

        int nodes = 0;
        int images = 0;
        try
        {
            if (format == "wz")
            {
                WriteClassic(scratch, version, customIv, request.GameVersion);
            }
            else
            {
                WriteNx(scratch, version, request.GameVersion);
            }

            // A file that was written is not a file that can be read back. The
            // container is opened with the app's own readers before it is given
            // its name, and the scratch file is deleted if it does not come
            // back: a container nobody can open is worse than no container,
            // because the caller is told it has one.
            (nodes, images) = VerifyCreated(scratch, format, version, customIv, request.GameVersion);

            File.Move(scratch, target);
        }
        catch
        {
            TryDelete(scratch);
            throw;
        }

        return new CreateEmptyArchiveResult
        {
            Path = target,
            Format = format,
            Bytes = new FileInfo(target).Length,
            Nodes = nodes,
            Images = images,
            MapleVersion = version.ToString(),
            GameVersion = request.GameVersion,
        };
    }

    /// <summary>
    /// The folder of an open archive that the target would land in, or null.
    /// </summary>
    /// <remarks>
    /// The open archives are the live client: they are what the app is working
    /// against, and they are the folder a stray file must not appear in. The
    /// check is on the FOLDER rather than on a configured path because the app
    /// has no configured root -- it opens whatever the user points it at, and
    /// that is the only honest definition of "the client's folder".
    /// </remarks>
    private string? LiveArchiveFolderHolding(string target)
    {
        foreach (OpenFile file in _session.Files)
        {
            string? folder = Path.GetDirectoryName(file.FilePath);
            if (string.IsNullOrEmpty(folder))
                continue;
            if (IsUnder(target, folder))
                return folder;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="target"/> is inside <paramref name="folder"/>.
    /// </summary>
    /// <remarks>
    /// The separator matters: "C:\client" and "C:\clients" are different
    /// folders, and a prefix test without the trailing separator refuses the
    /// second one because of the first.
    /// </remarks>
    private static bool IsUnder(string target, string folder)
    {
        string prefix = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes an empty classic WZ.
    /// </summary>
    /// <remarks>
    /// <c>SaveToDisk</c> writes a scratch file named after the OUTPUT file into
    /// the WORKING directory (WzFile.cs:591), and it deletes it afterwards. The
    /// working directory of this process is %TEMP%\MapleBench\&lt;pid&gt;
    /// (Program.cs), so that scratch never lands in a client's folder.
    /// </remarks>
    private static void WriteClassic(string path, WzMapleVersion version, byte[]? customIv, short gameVersion)
    {
        using WzFile wz = new(gameVersion, version);
        // The IV is passed explicitly whenever the caller pinned one:
        // SaveToDisk otherwise derives the key from the version name, and its
        // CUSTOM branch reads a stored app setting rather than the caller's key
        // (WzFile.cs:563-568).
        wz.SaveToDisk(path, false, version, customIv);
    }

    /// <summary>
    /// Writes an empty NX.
    /// </summary>
    /// <remarks>
    /// The same <see cref="WzFile"/> as the classic path, through the app's own
    /// writer. The writer always emits a root node, so an empty NX holds one
    /// node and no images (NxWriter.Plan, NxWriter.cs:519).
    /// </remarks>
    private static void WriteNx(string path, WzMapleVersion version, short gameVersion)
    {
        using WzFile wz = new(gameVersion, version);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        NxWriter.Write(wz, stream, out _);
    }

    /// <summary>
    /// Opens the container the way the app opens one, and reports what it holds.
    /// </summary>
    /// <returns>The node count and the image count.</returns>
    private static (int Nodes, int Images) VerifyCreated(
        string path, string format, WzMapleVersion version, byte[]? customIv, short gameVersion)
    {
        if (format == "wz")
        {
            using WzFile file = customIv != null
                ? new WzFile(path, customIv)
                : new WzFile(path, gameVersion, version);
            WzFileParseStatus status = file.ParseWzFile();
            if (status != WzFileParseStatus.Success)
            {
                throw new ServerFailureException(
                    $"The container written to '{Path.GetFileName(path)}' did not read back " +
                    $"({status.GetErrorDescription()}), so it was discarded and nothing was created.");
            }
            int images = file.WzDirectory.WzImages.Count;
            int folders = file.WzDirectory.WzDirectories.Count;
            if (images != 0 || folders != 0)
            {
                throw new ServerFailureException(
                    $"The container written to '{Path.GetFileName(path)}' came back with {images} images " +
                    $"and {folders} folders, so it was discarded. An empty container has none of either.");
            }
            return (0, 0);
        }

        using NxArchive nx = NxArchive.Open(path);
        if (nx.NodeCount != 1)
        {
            throw new ServerFailureException(
                $"The NX container written to '{Path.GetFileName(path)}' came back with {nx.NodeCount} nodes " +
                "instead of the one root node, so it was discarded.");
        }
        return (nx.NodeCount, 0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // The scratch file is left behind only if the file system refuses
            // the delete. The caller is told the create failed either way, and
            // a stray "empty-<guid>.wz" is harmless next to a refused write.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
