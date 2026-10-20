using MapleBench.Models;
using MapleBench.Services.Nx;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// Grafts standalone candidate IMGs into an existing NX archive.
/// </summary>
/// <remarks>
/// <para>
/// This is the last step of an authoring pipeline: a map and its object art are
/// built as loose <c>.img</c> files, and the client only sees them once they are
/// inside the archive it opens. The merge is one whole-archive rewrite, so it
/// writes to a scratch file beside the target, re-reads that file with the NX
/// reader, moves the original aside as a timestamped backup and only then swaps
/// the new bytes in. Nothing here ever writes over the archive it is reading.
/// </para>
/// <para>
/// <b>Why this does not go through <see cref="WzSessionService"/>.</b> Opening an
/// NX into the session materialises the whole tree and is guarded by an estimate
/// built for an interactive edit — on the shipped <c>Map.nx</c> (8.4M nodes) it
/// asks for about 13 GB and refuses a machine with 9 GB free, even though a
/// metadata-only conversion of the same archive fits. The merge only needs the
/// node tree as a scaffold to graft onto, never to browse, so it converts in
/// <see cref="NxLoadMode.MetadataOnly"/> and pays for the nodes it touches rather
/// than the worst case for all of them. A target that is <em>already</em> open in
/// the session is still refused: rewriting a file under a live mapping would
/// leave the session reading bytes that no longer exist.
/// </para>
/// </remarks>
public sealed class NxMergeService
{
    private readonly WzSessionService _session;
    private readonly ILogger<NxMergeService> _log;

    public NxMergeService(WzSessionService session, ILogger<NxMergeService> log)
    {
        _session = session;
        _log = log;
    }

    /// <summary>
    /// Adds or replaces the images named in <paramref name="request"/> and, when
    /// it is committed, rewrites the archive.
    /// </summary>
    public NxMergeResult Merge(NxMergeRequest request)
    {
        if (request.Entries.Count == 0)
        {
            throw new ArgumentException(
                "At least one entry is required: a source .img and the NX directory to place it in.");
        }

        string target = Path.GetFullPath(request.TargetNx);
        if (!target.EndsWith(".nx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("TargetNx must end in .nx.");
        }
        if (!File.Exists(target))
        {
            throw new FileNotFoundException($"Target NX not found: {target}");
        }

        foreach (NxMergeEntry entry in request.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target))
            {
                throw new ArgumentException("Every entry needs both a source .img path and an NX directory.");
            }
            if (!entry.Source.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Source '{Path.GetFileName(entry.Source)}' must end in .img.");
            }
            string source = Path.GetFullPath(entry.Source);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"Source IMG not found: {source}");
            }
        }

        lock (_session.Gate)
        {
            OpenFile? open = _session.Files.FirstOrDefault(f =>
                f.Kind == "nx" &&
                string.Equals(Path.GetFullPath(f.FilePath), target, StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                throw new InvalidOperationException(
                    $"'{open.Name}' is already open in this session. Close it before merging — a merge " +
                    "rewrites the whole archive, and the open session would keep reading the old mapping.");
            }
        }

        using var nx = NxArchive.Open(target);
        NxConversionResult conversion = NxToWzFile.Convert(nx, NxLoadMode.MetadataOnly);
        WzFile wz = conversion.File;

        List<NxMergeEntryResult> entries = request.Entries.Select(entry => Graft(wz, entry)).ToList();

        if (!request.Commit)
        {
            return new NxMergeResult
            {
                TargetNx = target,
                Committed = false,
                Entries = entries,
                Note = "Preview only: the archive was opened and inspected, but nothing was written.",
            };
        }

        // The bytes must be built while the mapping is still live: an untouched
        // canvas is copied straight out of the mapped file rather than decoded.
        byte[] bytes = NxWriter.Write(wz, out NxWriteReport report);

        string directory = Path.GetDirectoryName(target)!;
        // The same mbsave- prefix the save service uses, so a merge killed
        // mid-write leaves a file the orphan sweep recognises and removes.
        string scratch = Path.Combine(directory, $"mbsave-{Environment.ProcessId}-{Guid.NewGuid():N}.nx");
        string? backup = null;
        try
        {
            File.WriteAllBytes(scratch, bytes);

            // Read the bytes back before trusting them. A node-count mismatch
            // means the writer and reader disagree, which is the failure that
            // would otherwise only show up as a client that will not start.
            using (NxArchive check = NxArchive.Open(scratch))
            {
                if (check.NodeCount != report.Nodes)
                {
                    throw new InvalidOperationException(
                        $"The NX file written for '{Path.GetFileName(target)}' did not read back with the " +
                        $"expected node count ({check.NodeCount} of {report.Nodes}). It was discarded and " +
                        "the destination was not touched.");
                }
                foreach (NxMergeEntryResult entry in entries)
                {
                    if (!Resolves(check.Root, entry.ImagePath))
                    {
                        throw new InvalidOperationException(
                            $"The written NX file does not contain '{entry.ImagePath}'. It was discarded " +
                            "and the destination was not touched.");
                    }
                }
            }

            // The read-back above proves nothing about the device: NxArchive.Open
            // reads the scratch file back through the same page cache
            // WriteAllBytes just filled, so a merge that verified clean had still
            // only reached RAM. Power loss in the seconds after the swap would
            // leave the client folder holding a truncated archive with the
            // original already under its .bak name. FlushToDisk closes that gap,
            // and it sits here — after the read-back has released its mapping and
            // before anything irreversible touches the destination — so a flush
            // that fails costs the scratch file and nothing else.
            WzSaveService.FlushToDisk(scratch);

            // Windows will not rename a file that is memory-mapped, and this
            // archive's mapping is exactly that. It is dropped before the swap;
            // the process is done with the tree, so it is not remapped.
            nx.Release();

            if (request.Backup && File.Exists(target))
            {
                backup = MakeBackupPath(target);
                File.Move(target, backup, overwrite: true);
            }

            File.Move(scratch, target, overwrite: true);
        }
        catch (Exception mergeFailed)
        {
            try { File.Delete(scratch); }
            catch (IOException) { /* the scratch file is not the user's data */ }

            // Put the original back if it was moved aside and the swap did not happen.
            if (backup is not null && !File.Exists(target))
            {
                try { File.Move(backup, target); }
                catch (IOException restoreFailed)
                {
                    // Two failures have to be reported, and they are not the same
                    // one. The bare throw below would carry the merge's own
                    // failure, but its message names the swap and never mentions
                    // that the archive is now sitting under the backup — that is
                    // how a merge would leave the user's whole file somewhere they
                    // were not told to look. So both causes go in the message, each
                    // labelled, and the merge's own stays the InnerException:
                    // IOException cannot carry two, and that one is the reason the
                    // write failed in the first place.
                    throw new IOException(
                        $"{Path.GetFileName(target)} could not be written ({mergeFailed.Message}); " +
                        $"the previous file could not be moved back from {backup} " +
                        $"({restoreFailed.Message}). " +
                        $"Nothing was lost: your archive is at {backup}. Copy it back by hand.",
                        mergeFailed);
                }
            }

            throw;
        }

        _log.LogInformation(
            "Merged {Count} image(s) into {Target}: {Nodes:N0} nodes, {Canvases:N0} canvases, " +
            "{Bytes:N0} bytes, {Widened:N0} values widened, backup {Backup}",
            entries.Count, target, report.Nodes, report.Canvases, bytes.Length, report.TypeWidened,
            backup ?? "(none)");

        List<string> notes = [];
        // Same writer as SaveNx, so the same values get widened here. A merge that
        // widened a node and did not say so leaves the user comparing their file
        // against the original with no explanation for the difference.
        if (report.TypeWidened > 0)
        {
            notes.Add(
                $"NX stores one 64-bit integer and one 64-bit float, so {report.TypeWidened:N0} values " +
                "were widened from a narrower WZ type.");
        }

        if (report.EncodingErrors > 0)
        {
            notes.Add(
                $"{report.EncodingErrors} canvas(s) could not be encoded and were written empty. " +
                "Their pictures are not in the saved file.");
        }
        notes.Add(backup is null
            ? $"Rewrote the archive ({report.Nodes:N0} nodes, {bytes.Length:N0} bytes) with no backup."
            : $"Rewrote the archive ({report.Nodes:N0} nodes, {bytes.Length:N0} bytes); " +
              $"the previous file is at '{Path.GetFileName(backup)}'.");

        return new NxMergeResult
        {
            TargetNx = target,
            Committed = true,
            Entries = entries,
            BackupPath = backup,
            Bytes = bytes.Length,
            Nodes = report.Nodes,
            Images = report.Images,
            Canvases = report.Canvases,
            Verified = true,
            Note = string.Join(" ", notes),
        };
    }

    /// <summary>Places one source IMG in the archive, replacing a same-named image.</summary>
    private static NxMergeEntryResult Graft(WzFile wz, NxMergeEntry entry)
    {
        string source = Path.GetFullPath(entry.Source);
        string imageName = Path.GetFileName(source);
        WzObject container = ResolveContainer(wz, entry.Target);
        WzPropertyCollection properties = ChildrenOf(container)
            ?? throw new InvalidOperationException($"'{entry.Target}' is not a node that can hold images.");

        WzImageProperty? existing = properties.FindByName(imageName);
        string action = existing is null ? "added" : "replaced";
        if (existing is not null)
        {
            properties.Remove(existing);
        }

        using var parsed = new WzImage(imageName, File.OpenRead(source), WzMapleVersion.BMS);
        if (!parsed.ParseImage())
        {
            throw new InvalidDataException($"Failed to parse source IMG: {source}");
        }

        // The archive keeps a map image as a plain child node, not as a WzImage
        // of its own: a root image is an archive entry, and everything under it
        // is a property. So the source image becomes a sub-property carrying its
        // children — exactly the shape NxWriter reads back out as an image node,
        // and the shape the existing 910870002.img already has in this file.
        var grafted = new WzSubProperty(imageName);
        foreach (WzImageProperty property in parsed.WzProperties)
        {
            grafted.WzProperties.Add(property.DeepClone());
        }
        properties.Add(grafted);

        return new NxMergeEntryResult
        {
            Source = source,
            Target = entry.Target,
            ImagePath = entry.Target.TrimEnd('/', '\\') + "/" + imageName,
            Action = action,
        };
    }

    /// <summary>
    /// Walks a slash-separated path inside the archive, refusing with what is
    /// actually there when a segment does not resolve.
    /// </summary>
    /// <remarks>
    /// The first segment names a root image and the rest name properties under
    /// it, because that is the only shape <see cref="NxToWzFile"/> builds: NX
    /// root children become <see cref="WzImage"/>s on the file's directory, and
    /// every directory below them is a <see cref="WzSubProperty"/> inside one.
    /// An NX image's tree is deferred, so the root image is built here before it
    /// is walked rather than read empty.
    /// </remarks>
    private static WzObject ResolveContainer(WzFile wz, string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("The NX directory to place the image in is empty.");
        }

        WzDirectory root = wz.WzDirectory;
        WzImage? image = root.WzImages.FirstOrDefault(i =>
            string.Equals(i.Name, parts[0], StringComparison.OrdinalIgnoreCase));
        if (image is null)
        {
            throw new InvalidOperationException(
                $"'{path}' does not resolve: there is no '{parts[0]}' image in the archive. " +
                $"Existing images: {string.Join(", ", root.WzImages.Select(i => i.Name).Take(20))}");
        }

        // A shell holds no properties until its deferred build runs; the writer
        // runs it for every image, but this walk needs the children first.
        NxImageBuilders.BuildIfPending(image);

        WzObject current = image;
        for (int i = 1; i < parts.Length; i++)
        {
            WzPropertyCollection? children = ChildrenOf(current);
            WzObject? next = children?.FindByName(parts[i]);
            if (next is null)
            {
                string sofar = string.Join("/", parts.Take(i));
                throw new InvalidOperationException(
                    $"'{path}' does not resolve: there is no '{parts[i]}' under '{sofar}'. " +
                    $"Existing: {string.Join(", ", (children?.Select(p => p.Name) ?? Enumerable.Empty<string>()).Take(20))}");
            }
            current = next;
        }

        return current;
    }

    /// <summary>The child collection of a root image or a nested node, or null for a leaf.</summary>
    private static WzPropertyCollection? ChildrenOf(WzObject node) => node switch
    {
        WzImage image => image.WzProperties,
        WzImageProperty property => property.WzProperties,
        _ => null,
    };

    /// <summary>True when a slash-separated image path exists in a freshly opened archive.</summary>
    private static bool Resolves(NxNode root, string imagePath)
    {
        NxNode current = root;
        foreach (string segment in imagePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current[segment];
            if (!current.IsValid)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A timestamped path beside the archive, never overwriting an existing backup.</summary>
    private static string MakeBackupPath(string path)
    {
        string candidate = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        int counter = 2;
        while (File.Exists(candidate))
        {
            candidate = $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}-{counter++}.bak";
        }
        return candidate;
    }
}
