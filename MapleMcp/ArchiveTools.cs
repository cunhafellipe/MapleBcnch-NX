using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The archive-facing half of the server: open a split client's archives,
/// walk them, and serialise each image node to its own <c>.img</c> file.
/// </summary>
/// <remarks>
/// <para>
/// This is the step that had no MCP surface anywhere on this machine.
/// MapleMcp could only edit maps; WzImg-MCP's 74 tools all begin by assuming
/// the .img tree already exists and says so in its own README — "First export
/// the .wz files into .img with HaCreator". So the pipeline had a gap exactly
/// where a split client's data begins.
/// </para>
/// <para>
/// The backend owns serialisation. Every .img written here comes from
/// <c>/api/export/img</c>, which is MapleLib's own writer, so the bytes match
/// what the editor would save. Nothing re-implements the format.
/// </para>
/// </remarks>
[McpServerToolType]
public static class ArchiveTools
{
    // ---------------------------------------------------------------- status

    [McpServerTool(Name = "wz_archive_capabilities"),
     Description("Is the MapleBench backend reachable, and what is already open. Start here: every other tool needs it running, and a dead backend otherwise looks like a broken tool.")]
    public static async Task<string> Capabilities(ArchiveClient client, CancellationToken ct)
    {
        if (!await client.IsUpAsync(ct))
        {
            return $"""
                MapleBench is NOT answering at {client.BaseUrl}.
                Every wz_archive_* tool will fail until it is running
                (MapleBenchX.exe --browser). Nothing has been changed.
                """;
        }

        JsonNode? files = await client.GetJsonAsync("api/files", ct);
        return $"""
            MapleBench is up at {client.BaseUrl}.

            Open archives:
            {ArchiveClient.J(files)}
            """;
    }

    [McpServerTool(Name = "wz_archive_scan"),
     Description("List the .wz/.ms archives under a folder, grouped, marking which are PKG1 pointer files rather than data. Use this to find what to open — a split client's Data folder holds hundreds of archives and the ones worth splitting are not the ones the folder names suggest.")]
    public static async Task<string> Scan(
        ArchiveClient client,
        [Description("Folder to scan, e.g. C:\\MUWORK\\GAME\\gamania Games\\MapleStory\\Data")] string path,
        CancellationToken ct)
    {
        JsonNode? result = await client.GetJsonAsync(
            $"api/scan?path={ArchiveClient.Q(path)}", ct);
        return ArchiveClient.J(result);
    }

    // ------------------------------------------------------------- lifecycle

    [McpServerTool(Name = "wz_archive_open"),
     Description("Open one .wz or .ms archive and return its session id, detected format and version. The id (e.g. f1) is what the children/node/export tools take. A PKG1 pointer file such as Mob.wz opens but has no images — the real data is in the split files it names.")]
    public static async Task<string> Open(
        ArchiveClient client,
        [Description("Absolute path to a .wz or .ms file")] string path,
        CancellationToken ct)
    {
        JsonNode? result = await client.PostJsonAsync(
            "api/files/open", new { path }, ct);
        return ArchiveClient.J(result);
    }

    [McpServerTool(Name = "wz_archive_close"),
     Description("Close an open archive by session id, freeing its memory. ALWAYS close what you opened; a 679 MB archive stays resident until it is closed. WARNING: closing DISCARDS unsaved edits in that archive — save first, and never close an archive the user opened.")]
    public static async Task<string> Close(
        ArchiveClient client,
        [Description("Session id, e.g. f1")] string fileId,
        CancellationToken ct)
    {
        bool ok = await client.CloseAsync(fileId, ct);
        return ok
            ? $"Closed {fileId}. Any unsaved edits in it were discarded — closing does not write to disk."
            : $"Could not close {fileId} — the backend may already be gone. "
              + "Its memory is released when the backend exits either way.";
    }

    // ------------------------------------------------------------- traversal

    [McpServerTool(Name = "wz_archive_children"),
     Description("List the children of a node: each name, kind and node path. Start from the archive id for its top level, then walk down. Filter with kind='Image' to get the nodes worth exporting.")]
    public static async Task<string> Children(
        ArchiveClient client,
        [Description("Node path: an archive id like f1, or a path into it like f1/Npc")] string path,
        [Description("Return only children of this kind, e.g. Image or Folder")] string? kind = null,
        CancellationToken ct = default)
    {
        string route = $"api/children?path={ArchiveClient.Q(path)}";
        JsonNode? result = await client.GetJsonAsync(route, ct);

        if (kind is null || result is not JsonArray all)
            return ArchiveClient.J(result);

        JsonArray kept = new();
        foreach (JsonNode? child in all)
        {
            if (child is JsonObject obj &&
                string.Equals(obj["kind"]?.GetValue<string>(), kind,
                              StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(child.DeepClone());
            }
        }
        return ArchiveClient.J(kept);
    }

    [McpServerTool(Name = "wz_archive_node"),
     Description("Read one node: its type and, for an image, its property names. Use this to check what an .img actually holds before exporting it.")]
    public static async Task<string> Node(
        ArchiveClient client,
        [Description("Node path, e.g. f1/0002000.img")] string path,
        CancellationToken ct)
    {
        JsonNode? result = await client.GetJsonAsync(
            $"api/node?path={ArchiveClient.Q(path)}", ct);
        return ArchiveClient.J(result);
    }

    // ---------------------------------------------------------------- export

    [McpServerTool(Name = "wz_archive_export_img"),
     Description("Serialise ONE image node to a standalone .img file on disk, using MapleLib's writer, and report the bytes written plus the first bytes so the result can be sanity-checked. For a whole archive use wz_archive_split instead — this is the single-node version.")]
    public static async Task<string> ExportImg(
        ArchiveClient client,
        [Description("Node path of the image, e.g. f1/0002000.img")] string path,
        [Description("Destination .img path. Written to this exact path, overwriting without asking.")] string outPath,
        CancellationToken ct)
    {
        byte[] data = await client.GetBytesAsync(
            $"api/export/img?path={ArchiveClient.Q(path)}", ct);

        if (data.Length == 0)
            return $"The backend returned 0 bytes for {path}. The node exists but holds nothing.";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllBytesAsync(outPath, data, ct);

        return $"""
            Wrote {data.Length:N0} bytes to {outPath}
              first bytes: {Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)))}
            """;
    }

    [McpServerTool(Name = "wz_archive_split"),
     Description(
         "Split archives into one .img file per image node, on disk. This is the " +
         "bulk form of wz_archive_export_img: it opens each archive, walks it, and " +
         "writes every image node as a separate .img under outDir, keeping each " +
         "archive's folder structure. It is the missing step between a split " +
         "client's .wz archives and a tree of .img files.")]
    public static async Task<string> Split(
        ArchiveClient client,
        [Description("Absolute paths of the .wz/.ms files to split")] string[] archives,
        [Description("Folder to write the .img tree into")] string outDir,
        [Description("Also split _Canvas sprite archives. Leave this false: a _Canvas archive holds sprites indexed by asset key, so each of its nodes serialises to one .img that is most or all of the archive, producing hundreds of MB per file and a result that is archive contents rather than resources.")] bool includeCanvas = false,
        [Description("Stop with an estimate instead of writing anything")] bool dryRun = false,
        [Description("Hard cap on files written, for a trial run")] int limitFiles = 0,
        CancellationToken ct = default)
    {
        if (!await client.IsUpAsync(ct))
            throw new ArchiveClient.BackendError(
                $"MapleBench is not answering at {client.BaseUrl} — nothing was written.");

        var report = new SplitReport();
        foreach (string archive in archives)
        {
            ct.ThrowIfCancellationRequested();
            if (!report.Alive(client, ct)) break;

            string name = Path.GetFileName(archive);
            if (!includeCanvas && IsCanvasPath(archive))
            {
                report.Skipped.Add($"{name}: _Canvas sprite archive (pass includeCanvas=true to override)");
                continue;
            }

            // A category root file is a PKG1 directory, not data: 216 bytes of
            // XOR-encoded paths to the real split files. Opening it yields
            // nothing, so it is reported rather than silently counted as empty.
            // The existence check comes first: a mistyped path throws from
            // FileInfo.Length, which would abandon every archive still queued
            // behind it and lose the files already written.
            if (!File.Exists(archive))
            {
                report.Failed.Add($"{archive}: no such file");
                continue;
            }
            FileInfo info = new(archive);
            if (info.Length < 1024)
            {
                report.Skipped.Add($"{name}: {info.Length} bytes, a PKG1 pointer rather than an archive");
                continue;
            }

            string? fileId = null;
            bool openedHere = false;
            try
            {
                // The backend REUSES a session for a path it already holds and returns the
                // SAME id, so "the id I got back" does not mean "I opened this". Closing
                // it would dispose an archive the user had open with unsaved edits -- and
                // the finally below runs on the dry-run path too. Ask the backend what was
                // already open BEFORE the call, and close only what this run added.
                JsonNode? before = await client.GetJsonAsync("api/files", ct);
                var alreadyOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (before is JsonArray openList)
                    foreach (JsonNode? f in openList)
                        if (f?["filePath"]?.GetValue<string>() is { Length: > 0 } p)
                            alreadyOpen.Add(p);

                JsonNode? opened = await client.PostJsonAsync(
                    "api/files/open", new { path = archive }, ct);
                fileId = opened?["id"]?.GetValue<string>() ?? opened?["opened"]?[0]?["id"]?.GetValue<string>();
                if (fileId is null)
                {
                    report.Failed.Add($"{name}: open returned no session id");
                    continue;
                }

                string full = Path.GetFullPath(archive);
                openedHere = !alreadyOpen.Contains(full);
                if (!openedHere)
                {
                    report.Skipped.Add(
                        $"{name}: already open in the backend, so it is left exactly as it is "
                        + "(including unsaved edits). Close it yourself if that is not wanted.");
                }
                if (!report.NoteOpenSize(info, outDir))
                    continue;

                JsonNode? kids = await client.GetJsonAsync(
                    $"api/children?path={ArchiveClient.Q(fileId)}", ct);
                if (kids is not JsonArray children)
                {
                    report.Skipped.Add($"{name}: root has no children");
                    continue;
                }

                // Keep the archive's own subfolder, so Mob_00000.ms lands under
                // Packs/ rather than colliding with another pack's 1000.img.
                string folder = Path.GetFileNameWithoutExtension(archive);
                string dest = Path.Combine(outDir, folder);

                if (dryRun)
                {
                    long imgs = children.Count(c =>
                        c?["kind"]?.GetValue<string>() == "Image");
                    report.WouldWrite += imgs;
                    report.DryRunDetail.Add(
                        $"{name}: {children.Count:N0} children, {imgs:N0} images -> {dest}");
                    continue;
                }

                foreach (JsonNode? child in children)
                {
                    ct.ThrowIfCancellationRequested();
                    if (child is not JsonObject obj) continue;
                    if (obj["kind"]?.GetValue<string>() != "Image") continue;
                    if (limitFiles > 0 && report.Written >= limitFiles)
                    {
                        // Say so, or "Wrote N" reads as a finished run.
                        report.HitLimit = true;
                        report.Skipped.Add(
                            $"stopped at limitFiles={limitFiles:N0} — the remaining nodes in "
                            + $"{name} were NOT split. Raise the limit to continue.");
                        break;
                    }

                    string childName = obj["name"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("child without a name");
                    string nodePath = obj["path"]?.GetValue<string>()
                        ?? $"{fileId}/{childName}";

                    try
                    {
                        byte[] data = await client.GetBytesAsync(
                            $"api/export/img?path={ArchiveClient.Q(nodePath)}", ct);
                        if (data.Length == 0)
                        {
                            report.Failed.Add($"{nodePath}: 0 bytes");
                            continue;
                        }
                        Directory.CreateDirectory(dest);
                        string file = UniquePath(dest, SafeName(childName));
                        await File.WriteAllBytesAsync(file, data, ct);
                        report.Written++;
                        report.Bytes += data.Length;
                        report.PerArchive[folder] = report.PerArchive.GetValueOrDefault(folder) + 1;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        report.Failed.Add($"{nodePath}: {Short(ex)}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MapleLib cannot read every split client archive. When one
                // fails, the rest must still run, and the failure has to be
                // reported as itself rather than lost in a summary count.
                report.Failed.Add($"{name}: {Short(ex)}");
            }
            finally
            {
                // Only what this run opened. A dry run reaches here too, and closing an
                // archive the user already had open would discard their unsaved work
                // while the report says nothing was written.
                if (fileId is not null && openedHere)
                {
                    // Cleanup must not turn a successful split into a failure, nor mask
                    // the real error above. Report it and move on.
                    try
                    {
                        await client.CloseAsync(fileId, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        report.Failed.Add($"{name}: could not close after splitting — {Short(ex)}");
                    }
                }
            }
        }

        return report.Render(dryRun);
    }

    // ------------------------------------------------------------- internals

    private sealed class SplitReport
    {
        public int Written { get; set; }
        public long Bytes { get; set; }
        public long WouldWrite { get; set; }
        /// <summary>
        /// Set when limitFiles stopped the run. Without this the report reads
        /// "Wrote N .img files" for output that is deliberately incomplete, and
        /// N looks like a total rather than a cap.
        /// </summary>
        public bool HitLimit { get; set; }
        public List<string> Failed { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> DryRunDetail { get; } = [];
        public Dictionary<string, int> PerArchive { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stops before the disk fills. 67 GB of archives can produce far more
        /// than the drive has room for, and a full disk fails mid-write with a
        /// stack trace that names neither the cause nor the archive.
        /// </summary>
        public bool NoteOpenSize(FileInfo info, string outDir)
        {
            string root = Path.GetPathRoot(Path.GetFullPath(outDir)) is { Length: > 0 } r
                ? r : Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
            DriveInfo drive = new(root);
            if (!drive.IsReady || drive.AvailableFreeSpace > 8L * 1024 * 1024 * 1024)
                return true;
            Skipped.Add(
                $"{info.Name}: stopped — {root} has only "
                + $"{drive.AvailableFreeSpace / 1073741824.0:N1} GB free. "
                + "Free space before splitting a large archive.");
            return false;
        }

        public bool Alive(ArchiveClient client, CancellationToken ct)
        {
            if (client.IsUpAsync(ct).GetAwaiter().GetResult()) return true;
            Failed.Add("MapleBench stopped responding — the remaining archives were not attempted.");
            return false;
        }

        public string Render(bool dryRun)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(dryRun
                ? $"DRY RUN — nothing written. {WouldWrite:N0} .img would be written."
                : HitLimit
                    ? $"PARTIAL RUN — stopped at the limitFiles cap. Wrote {Written:N0} .img files, "
                      + $"{Bytes / 1073741824.0:N2} GB. This is NOT the complete set."
                    : $"Wrote {Written:N0} .img files, {Bytes / 1073741824.0:N2} GB.");

            foreach ((string folder, int n) in PerArchive.OrderByDescending(kv => kv.Value).Take(20))
                sb.AppendLine($"  {folder}: {n:N0}");
            if (PerArchive.Count > 20) sb.AppendLine($"  ... and {PerArchive.Count - 20} more archives");

            if (DryRunDetail.Count > 0)
            {
                sb.AppendLine();
                foreach (string line in DryRunDetail.Take(40)) sb.AppendLine("  " + line);
                if (DryRunDetail.Count > 40) sb.AppendLine($"  ... and {DryRunDetail.Count - 40} more");
            }
            if (Skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Skipped {Skipped.Count}:");
                foreach (string line in Skipped.Take(25)) sb.AppendLine("  " + line);
                if (Skipped.Count > 25) sb.AppendLine($"  ... and {Skipped.Count - 25} more");
            }
            if (Failed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"FAILED {Failed.Count} — these are NOT in the output:");
                foreach (string line in Failed.Take(25)) sb.AppendLine("  " + line);
                if (Failed.Count > 25) sb.AppendLine($"  ... and {Failed.Count - 25} more");
            }
            return sb.ToString().TrimEnd();
        }
    }

    private static bool IsCanvasPath(string archive) =>
    archive.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
           .Any(seg => string.Equals(seg, "_Canvas", StringComparison.OrdinalIgnoreCase));

private static string SafeName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char c in bad) name = name.Replace(c, '_');
        return name.Length == 0 ? "unnamed" : name;
    }

    /// <summary>
    /// A path no earlier write in this run has taken.
    /// </summary>
    /// <remarks>
    /// SafeName is lossy on purpose — it strips characters the filesystem rejects —
    /// so two different node names can collapse onto one filename ("a/b" and "a?b"
    /// both become "a_b"). Writing both to the same path silently keeps only the
    /// last, and the report would still count both. Suffixing on collision keeps
    /// every file, and the count then means what it says.
    /// </remarks>
    private static string UniquePath(string dir, string fileName)
    {
        string candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int n = 2; ; n++)
        {
            candidate = Path.Combine(dir, $"{stem}~{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
            if (n > 10000) throw new IOException($"Could not find a free name for {fileName}.");
        }
    }

    private static string Short(Exception ex) =>
        ex is ArchiveClient.BackendError b ? b.Message
        : $"{ex.GetType().Name}: {ex.Message}";
}