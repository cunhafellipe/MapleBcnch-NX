using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// Diagnostics over the backend's own debug surfaces: the browser's console, the
/// simulator's swing chain, the facts about one node, the integrity auditor, the
/// process's memory - and the save pipeline, preflight through commit.
/// </summary>
/// <remarks>
/// <para>
/// The rule that shaped every <c>[Description]</c> here: an agent that cannot
/// tell a read from a write will guess, and on this server a wrong guess is not
/// a 400. It is a rewritten <c>.wz</c>. So each tool states in its own summary
/// whether it writes, and the one that writes to an archive says so first.
/// </para>
/// <para>
/// <b>An open archive is memory-mapped, and it stays mapped.</b> The NX path
/// opens its source with a memory-mapped file rather than reading it onto the
/// heap - <c>Map.nx</c> is 679 MB and <c>Character.nx</c> 195 MB, and neither
/// is ever fully resident. Every canvas decodes straight out of that mapping, so
/// the mapping cannot simply be dropped while the entry is open: the canvases
/// would be left pointing at an unmapped address. That is why checking memory
/// and releasing it are tools at all. Closing an entry would give the mapping
/// back, but closing can discard unsaved work, so it is not something a
/// diagnostic may do on your behalf; a sweep is safe by construction and is the
/// only handle here that hands memory back without that risk. Even a sweep keeps
/// any image holding an unsaved change, an undo reference, or a pack origin with
/// nowhere to be re-read from.
/// </para>
/// <para>
/// It is also why a save attempt after the file is open can fail with a
/// <b>sharing violation</b> rather than with a clean, explanatory error.
/// Windows will not rename or replace a file that is memory-mapped, and an open
/// archive is mapped. The save path drops the mapping around the swap and
/// rebuilds it on the same instance afterwards - every canvas holds a reference
/// to that one object, so remapping in place is what lets the whole tree keep
/// decoding against the new bytes. The consequence is that the protection is a
/// window, not a guarantee: a refusal arriving as a bare <c>IOException</c>
/// instead of the backend's own sentence is that, and not a corrupt file. The
/// backend documents the drop and rebuild in <c>WzSaveService</c>; read it
/// before treating one as a bug.
/// </para>
/// <para>
/// Every route below was read out of the endpoint source. Nothing is guessed
/// into existence, and an unrecognised <c>kind</c> or <c>action</c> is refused
/// by name with the valid list rather than dispatched to a near-miss route that
/// answers 200 with a shape nobody expected.
/// </para>
/// </remarks>
[McpServerToolType]
public static class DiagTools
{
    /// <summary>Query-string helper: the first separator depends on whether one is already there.</summary>
    private static string Query(string route, string name, string? value) =>
        value is null ? route
            : route + (route.Contains('?') ? '&' : '?') + name + "=" + MapClient.Q(value);

    [McpServerTool(Name = "wz_diag_health")]
    [Description("The three cheap health checks in one call: the server's version and licence, the simulator's own findings for a live session, and what the process is holding in memory. Read-only - it opens nothing, writes nothing and saves nothing. Use it first when something downstream is behaving strangely, because it separates 'the backend is up' from 'the backend is up and this session is in trouble'.")]
    public static async Task<string> Health(MapClient client, CancellationToken ct = default)
    {
        JsonNode? about = await client.GetAsync("/api/about", ct);
        JsonNode? health = await client.GetAsync("/api/simulate/debug/health", ct);
        JsonNode? memory = await client.GetAsync("/api/session/memory", ct);

        return JsonSerializer.Serialize(new
        {
            about,
            // Findings, not failures: the endpoint reports a mob with no attack
            // art the same way it reports a broken one, because both are facts
            // about the archive and nothing else said which was which.
            simulationHealth = health,
            memory,
        });
    }

    [McpServerTool(Name = "wz_diag_clientlog")]
    [Description("Read, clear or append to the browser page's own console report (GET/POST/DELETE /api/clientlog). This is the ONLY place a decode failure that happened in the browser is visible, so read it before concluding the archive is fine. Reads are read-only; action='clear' DELETES every buffered entry; action='post' WRITES one new entry into that in-memory log (it does not touch any archive, and the log is capped at 200 entries with the oldest trimmed). Nothing here is durable across a server restart.")]
    public static async Task<string> ClientLog(
        MapClient client,
        [Description("read (GET, returns entries oldest-first), clear (DELETE, empties the whole log), or post (POST, appends one entry).")]
        string action,
        [Description("For read: return only entries whose body contains this text, e.g. 'TypeError'. Ignored by the other actions.")]
        string? kind = null,
        [Description("For read: how many entries to return. The server buffers at most 200 and does not page, so values above 200 change nothing.")]
        int limit = 100,
        [Description("For post: the text to append. The endpoint stores the raw request body verbatim, so what lands in the log is this string as JSON - quotes included. Required for post, ignored by the others.")]
        string? message = null,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case "read":
            {
                JsonNode? raw = await client.GetAsync(Query("/api/clientlog", "kind", kind), ct);
                // The route cannot page, so the cap is applied here. Both numbers
                // are reported: a caller that got 40 of 61 must be able to say so.
                int take = Math.Clamp(limit, 1, 200);
                JsonArray kept = new();
                int held = 0;
                if (raw is JsonObject obj)
                {
                    held = obj["count"]?.GetValue<int>() ?? 0;
                    if (obj["entries"] is JsonArray entries)
                        foreach (JsonNode? entry in entries.Take(take))
                            kept.Add(entry?.DeepClone());
                }
                return JsonSerializer.Serialize(new
                {
                    action, wrote = false, buffered = held, returned = kept.Count, entries = kept,
                });
            }

            case "clear":
                return JsonSerializer.Serialize(new
                {
                    action,
                    wrote = true,
                    note = "Clears every buffered entry. The count restarts at zero and the entries are not recoverable.",
                    result = await client.DeleteAsync("/api/clientlog", ct),
                });

            case "post":
                if (string.IsNullOrWhiteSpace(message))
                    throw new ModelContextProtocol.McpException(
                        "action='post' needs a message to append to the client log.");
                return JsonSerializer.Serialize(new
                {
                    action,
                    wrote = true,
                    note = "Appended one entry to the in-memory client log. No archive was touched.",
                    result = await client.PostAsync("/api/clientlog", message, ct),
                });

            default:
                throw new ModelContextProtocol.McpException(
                    $"unknown action '{action}'. Valid actions: read, clear, post.");
        }
    }

    [McpServerTool(Name = "wz_diag_sim")]
    [Description("The simulator's and animation auditor's own /debug routes, chosen by kind: why a swing missed, why a portal failed, whether a weapon resolves an attack box, a mob's frame geometry or max HP, what the browser failed to draw, the animation audit's thresholds, or its verdict on one stance. Reads do NOT write and do NOT step any simulation frame. kind='artifacts' WRITES: it POSTs a client-art report into a live session, overwriting the counts that were there. An unknown kind is refused by name with the valid list.")]
    public static async Task<string> Sim(
        MapClient client,
        [Description("One of: mobswing, portal, attackbox, mobframe, mobhp, artifacts, animrules, animaudit. Nothing else is accepted - the route is chosen from this closed set, never guessed.")]
        string kind,
        [Description("For kind='mobswing' and 'artifacts': which live session to answer about. Name it whenever you know it - with no id the server answers about the FIRST session it holds, which is a probe somebody leaked and never deleted, and that one's log is empty because it never ran a frame.")]
        string? sessionId = null,
        [Description("For kind='mobframe' and 'mobhp': the mob id as the Mob archive names it, e.g. 100100 - not 0100100.img and not Mob/100100.img.")]
        string? mobId = null,
        [Description("For kind='mobframe': the action name to measure, e.g. stand, move, attack1.")]
        string? action = null,
        [Description("For kind='mobframe': the frame index within that action.")]
        int frame = 0,
        [Description("For kind='attackbox': the weapon whose swing box to resolve, e.g. 1342000.img. Omit to resolve the bare-hand box.")]
        string? weapon = null,
        [Description("For kind='animaudit': the STANCE node to audit, e.g. f7/0100100.img/stand - not the image, not a frame. Ignored by the other kinds.")]
        string? path = null,
        [Description("The one free-text qualifier each of the three routes that has one: for kind='mobframe' the mob sprite kind ('m' or 'n', backend default 'm'); for kind='animaudit' the subject to focus the verdict on; for kind='artifacts' the comma-separated art the client failed to load. Ignored by the other kinds.")]
        string? subject = null,
        CancellationToken ct = default)
    {
        if (kind == "artifacts")
        {
            // There is no honest way to post the BROWSER's own numbers from
            // here: this tool carries no attempted/missing counts, and posting
            // zeros would quietly destroy the real report the page filed. So the
            // sample list is the payload and the counts are derived from it,
            // which the result says out loud rather than implying a
            // measurement that was never taken.
            if (string.IsNullOrWhiteSpace(subject))
                throw new ModelContextProtocol.McpException(
                    "kind='artifacts' needs subject: the comma-separated list of art the client failed to load. " +
                    "This tool cannot post the browser's own attempted/missing counts, and posting zeros would " +
                    "overwrite the real report - so the counts it sends are the size of the sample you give.");

            string[] missing = subject
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string artRoute = Query("/api/simulate/debug/artistats", "sessionId", sessionId);

            return JsonSerializer.Serialize(new
            {
                kind,
                route = artRoute,
                wrote = true,
                note = "Reported art as missing. The session keeps this for /debug/mob-swing; nothing on disk was touched.",
                reportedMissing = missing.Length,
                result = await client.PostAsync(artRoute, new
                {
                    attempted = missing.Length,
                    missing = missing.Length,
                    missingSample = missing,
                }, ct),
            });
        }

        string route = kind switch
        {
            "mobswing" => Query("/api/simulate/debug/mob-swing", "sessionId", sessionId),
            "portal" => "/api/simulate/debug/portal",
            "attackbox" => Query("/api/simulate/debug/attack-box", "weapon", weapon),
            "mobframe" => BuildMobFrameRoute(mobId, action, frame, subject),
            "mobhp" => $"/api/simulate/debug/mob-hp?mobId={MapClient.Q(Require(mobId, "kind='mobhp' needs a mobId."))}",
            "animrules" => "/api/animation/audit/rules",
            "animaudit" => BuildAuditRoute(path, subject),
            _ => throw new ModelContextProtocol.McpException(
                $"unknown kind '{kind}'. Valid kinds: mobswing, portal, attackbox, mobframe, mobhp, " +
                "artifacts, animrules, animaudit."),
        };

        return JsonSerializer.Serialize(new
        {
            kind, route, wrote = false, result = await client.GetAsync(route, ct),
        });
    }

    [McpServerTool(Name = "wz_diag_facts")]
    [Description("What the backend can assert about ONE node, from GET /api/node/facts?path=. Read-only: it inspects and reports, and it opens nothing new. Answers 'is this value really a string, what references point at it, is it a folder that really resolves' in one call, where wz_asset_node gives the node and wz_asset_search gives everything matching a text.")]
    public static async Task<string> Facts(
        MapClient client,
        [Description("Node path, e.g. f1/UIWindow.img/BtUIClose. Use a path the session can actually resolve.")]
        string path,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync("/api/node/facts?path=" + MapClient.Q(path), ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_diag_audit")]
    [Description("The client integrity auditor's finished report (GET /api/audit/report) and, with plan=true, what a run WOULD look at (GET /api/audit/plan). Read-only: it reports on a run that already happened, and the plan answers without opening anything. It does NOT start, watch or cancel an audit - there is no write here at all. 404 from the report means no run has finished in this session, which is NOT the same as a clean client.")]
    public static async Task<string> Audit(
        MapClient client,
        [Description("How many findings to include. The report carries every finding; this trims the list and reports how many were dropped.")]
        int limit = 100,
        [Description("Also fetch /api/audit/plan for the folder the last finished run audited: the archives it would group, and the ones it would skip with the reason. Uses the report's own folder, so it needs a finished run.")]
        bool plan = false,
        CancellationToken ct = default)
    {
        int take = Math.Max(1, limit);

        // The plan route is /audit/plan?path=<folder> and the folder it means is
        // the one the last run covered. Passing anything else - a guess, a
        // default - would describe a folder nobody audited.
        JsonNode? report = await client.GetAsync("/api/audit/report", ct);
        JsonNode? planNode = null;
        if (plan)
        {
            string folder = report is JsonObject r && r["folder"] is JsonValue v
                ? v.ToString()
                : throw new ModelContextProtocol.McpException(
                    "plan=true needs a finished report to take the folder from, and /api/audit/report did not " +
                    "return one. Run the audit first, or call /api/audit/plan directly with a folder path.");

            planNode = await client.GetAsync("/api/audit/plan?path=" + MapClient.Q(folder), ct);
        }

        JsonArray findings = new();
        int total = 0;
        if (report is JsonObject obj && obj["findings"] is JsonArray all)
        {
            total = all.Count;
            foreach (JsonNode? finding in all.Take(take))
                findings.Add(finding?.DeepClone());
        }

        return JsonSerializer.Serialize(new
        {
            wrote = false,
            findingsReturned = findings.Count,
            findingsTotal = total,
            report,
            plan = planNode,
        });
    }

    [McpServerTool(Name = "wz_save_preflight")]
    [Description("The backend's own refusal preview for one archive: GET /api/files/{fileId}/preflight. It does NOT write, does NOT save and does NOT mark anything dirty - it parses the dirty images under the session lock and reports what cannot be re-serialised, plus how many dirty images there are. A CLEAN preflight DOES NOT PROVE THE SAVE WILL SUCCEED: it checks re-serialisability, not whether the destination can be replaced, not whether the machine has the memory for a whole-archive rewrite, and it does not run with the stricter every-image pass a real save may use. It is the first gate, not the last.")]
    public static async Task<string> Preflight(
        MapClient client,
        [Description("File id from wz_asset_files. Not a path - the session's own handle.")]
        string fileId,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync("/api/files/" + MapClient.Q(fileId) + "/preflight", ct);
        return JsonSerializer.Serialize(new
        {
            fileId,
            wrote = false,
            note = "The backend's refusal preview. A clean result is necessary, not sufficient.",
            preflight = result,
        });
    }

    [McpServerTool(Name = "wz_save_commit")]
    [Description("Save an archive in place: POST /api/files/save. commit=false POSTS NOTHING - it returns exactly what the save would do, including the preflight the same call runs, because the save route has no dry-run flag and inventing one would be a guess. commit=true REWRITES THE WHOLE ARCHIVE: it is not a patch, the file on disk is replaced wholesale rather than edited in place, and although the backend keeps a timestamped <archive>.bak beside it first, that is a safety net and not a rollback plan. Only the images you changed are re-serialised UNLESS the archive is under a custom MapleStory user key or is being re-encrypted, in which case every image in it is and the result says so in its warnings. Never commit to a user's production client without approval. This tool runs the preflight in the same call and returns both, and refuses to post at all when the preflight reports blockers.")]
    public static async Task<string> Commit(
        MapClient client,
        [Description("File id from wz_asset_files. Not a path - the session's own handle.")]
        string fileId,
        [Description("false = preview: no request is posted. true = rewrite the archive now.")]
        bool commit = false,
        CancellationToken ct = default)
    {
        string route = "/api/files/save";
        string preflightRoute = "/api/files/" + MapClient.Q(fileId) + "/preflight";

        // Not optional and not silent: the save service runs its own preflight
        // and throws on blockers, so a save that was never going to work is a
        // rewrite attempted for nothing. The report comes back either way.
        JsonNode? preflight = await client.GetAsync(preflightRoute, ct);

        var body = new { fileId, targetPath = (string?)null, backup = true };

        if (!commit)
        {
            return JsonSerializer.Serialize(new
            {
                fileId,
                committed = false,
                route,
                wrote = false,
                request = body,
                preflightRoute,
                preflight,
                note = "Preview only: nothing was posted. A commit would rewrite the whole archive in place - "
                     + "replacing the file on disk wholesale, after writing a timestamped .bak beside it.",
            });
        }

        bool blocked = preflight is JsonObject o && o["ok"]?.GetValue<bool>() == false;
        if (blocked)
        {
            return JsonSerializer.Serialize(new
            {
                fileId,
                committed = false,
                route,
                wrote = false,
                preflightRoute,
                preflight,
                note = "Not posted. The backend's own preflight reported blockers, and its save path runs the "
                     + "same check and throws on them - nothing would have been written.",
            });
        }

        return JsonSerializer.Serialize(new
        {
            fileId,
            committed = true,
            route,
            wrote = true,
            preflightRoute,
            preflight,
            note = "The archive was re-serialised and replaced on disk. A timestamped .bak sits beside it; "
                 + "imagesRewritten and backupPath in the result say how much moved and where the copy is. "
                 + "A warning about every image being re-serialised means the whole file was re-encoded, not "
                 + "just the edited images.",
            result = await client.PostAsync(route, body, ct),
        });
    }

    [McpServerTool(Name = "wz_save_memory")]
    [Description("The four handles on what the process is holding: what it has (GET /api/session/memory), releasing it (POST /api/session/sweep), checking under memory pressure on demand (POST /api/session/memory-check), and marking an open archive reference-only or editable again (POST /api/files/{id}/readonly). action='memory' is read-only. action='sweep' and action='readonly' WRITE: a sweep releases parsed images and thumbnail caches, and readonly changes how a file is opened - a file with unsaved work will be refused rather than locked. action='check' is a POST that only acts when the machine is under pressure, and its own reason field says whether it did. None of these saves an archive.")]
    public static async Task<string> Memory(
        MapClient client,
        [Description("memory (read only), sweep (releases memory), check (pressure check, may release), or readonly (changes how an archive is opened).")]
        string action,
        [Description("For action='readonly': the file id from wz_asset_files. Required there, ignored by the others.")]
        string? fileId = null,
        [Description("For action='readonly': true marks the archive reference-only (it will not be written), false unlocks it for editing. Ignored by the other actions.")]
        bool value = true,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case "memory":
                return JsonSerializer.Serialize(new
                {
                    action, wrote = false,
                    note = "working set, managed heap, parsed and dirty image counts, thumbnail cache size.",
                    memory = await client.GetAsync("/api/session/memory", ct),
                });

            case "sweep":
                return JsonSerializer.Serialize(new
                {
                    action,
                    wrote = true,
                    note = "Released parsed images and caches. Images holding unsaved changes, held by undo, or "
                         + "read out of a pack are KEPT, and cannot be released by any means - see kept/released.",
                    result = await client.PostAsync("/api/session/sweep", new { }, ct),
                });

            case "check":
            {
                // Conditional by design, and reported as such rather than as a
                // flat "did not write": the route is a POST either way, but
                // whether it released anything is the backend's answer, not
                // something this tool decided.
                JsonNode? check = await client.PostAsync("/api/session/memory-check", new { }, ct);
                bool acted = check is JsonObject c && c["acted"]?.GetValue<bool>() == true;
                return JsonSerializer.Serialize(new
                {
                    action,
                    wrote = acted,
                    route = "/api/session/memory-check",
                    note = "Reads free memory first and only drops caches and sweeps if the machine is short. "
                         + "The result's reason says whether anything was released.",
                    result = check,
                });
            }

            case "readonly":
                if (string.IsNullOrWhiteSpace(fileId))
                    throw new ModelContextProtocol.McpException(
                        "action='readonly' needs the fileId whose open mode is changing.");
                return JsonSerializer.Serialize(new
                {
                    action,
                    wrote = true,
                    fileId,
                    readOnly = value,
                    note = value
                        ? "Marked reference-only: this archive is not written by any save path until it is unlocked."
                        : "Unlocked for editing.",
                    result = await client.PostAsync("/api/files/" + MapClient.Q(fileId) + "/readonly",
                        new { readOnly = value }, ct),
                });

            default:
                throw new ModelContextProtocol.McpException(
                    $"unknown action '{action}'. Valid actions: memory, sweep, check, readonly.");
        }
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ModelContextProtocol.McpException(message)
            : value;

    private static string BuildMobFrameRoute(string? mobId, string? action, int frame, string? kind)
    {
        string route = $"/api/simulate/debug/mob-frame?mobId={MapClient.Q(Require(mobId, "kind='mobframe' needs a mobId."))}"
                     + $"&action={MapClient.Q(Require(action, "kind='mobframe' needs an action name, e.g. stand or attack1."))}"
                     + $"&frame={frame}";
        // `kind` on the route is the mob sprite kind (m or n), which the endpoint
        // defaults to "m". It is carried on `subject` so it cannot be confused
        // with this tool's own dispatch kind.
        return Query(route, "kind", kind);
    }

    private static string BuildAuditRoute(string? path, string? subject) =>
        Query("/api/animation/audit?path=" + MapClient.Q(
                Require(path, "kind='animaudit' needs path: the STANCE node to audit, e.g. f7/0100100.img/stand.")),
            "subject", subject);
}
