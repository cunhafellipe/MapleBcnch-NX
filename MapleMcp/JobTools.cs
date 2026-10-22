using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The eight long-running job families in one place: audit, dump, compose,
/// import, and the four repairs. Six tools, dispatching on <c>kind</c>.
/// </summary>
/// <remarks>
/// <para>
/// Fifty-six routes is the real number here and exposing them as fifty-six tools
/// would bury the other sixty in this server, so the surface is
/// <c>capabilities / start / progress / result / report / cancel</c> and
/// everything else is a <c>kind</c> string. The table below was read out of the
/// endpoint sources and is checked against them by the stage lookups in this
/// file — a kind that has no such route raises rather than falling through to
/// another kind's route, because a silent fall-through answers about the wrong
/// archive.
/// </para>
/// <para>
/// <b>These jobs run against archives the user has open.</b> A scan or an audit
/// holds file handles for minutes and holds memory for a whole client. Cancelling
/// is cooperative: the run stops at its next checkpoint, so a cancel followed
/// immediately by a poll can still report <c>running</c>.
/// </para>
/// <para>
/// <b><c>repair.*</c> can write.</b> The scan verbs are read-only — they open
/// archives read-only, measure, and write nothing — but each repair family also
/// has a separate <c>/apply</c> route that writes a new archive the size of the
/// source, and <c>genchoice</c> has a <c>/build</c> that does the same. Those
/// routes are deliberately not reachable from this file: <see cref="JobTools.Start"/>
/// only ever posts the start route in the table, and <c>commit=false</c> posts
/// nothing at all.
/// </para>
/// <para>
/// <b>What a false preview does and does not prove.</b> With
/// <paramref name="commit"/> false this tool sends no request at all, so nothing
/// was validated, nothing was reserved, and no progress exists. It proves only
/// that the body you assembled is the body that <i>would</i> be posted, and it
/// proves it without the backend ever parsing your options. A malformed field,
/// a path that does not exist, a busy service, a folder that would be refused —
/// all of those surface only on the real call. Where a kind has its own preview
/// route that preview is used instead, and it is still a separate fact: the dump
/// preflight reports what the backend <i>would</i> refuse, not what the start
/// will do. And a preview never proves an audit, a scan or a prepare is safe to
/// run: only the committed run reads the archives, and only that run can fail
/// halfway.
/// </para>
/// </remarks>
[McpServerToolType]
public static class JobTools
{
    /// <summary>One row of the dispatch table: the routes a kind really has.</summary>
    private sealed record JobRoutes(
        string Kind,
        string Start,
        string Progress,
        string Cancel,
        string? Result,
        string? Report,
        string Options,
        string Stages);

    // ------------------------------------------------------------------
    // The table. Every route here was read out of the endpoint source:
    //   AuditEndpoints / DumpEndpoints / CompositionEndpoints / ImportEndpoints
    //   / RepairEndpoints / CanvasDirLinkEndpoints / DonorRestoreEndpoints
    //   / GenerationChoiceEndpoints.
    //
    // Corrections to the table this file was written against, found in the
    // source, and deliberate:
    //
    //  * repair.* has FOUR stages plus an apply, not three. POST .../scan,
    //    GET .../progress, POST .../cancel, GET .../scan (the scan report),
    //    GET .../result (the apply result) — and POST .../apply, which writes.
    //    This file exposes the scan routes only; the apply routes are named in
    //    the remarks as the thing that is deliberately not here.
    //  * repair.genchoice's terminal read is GET /repair/genchoice/result, and
    //    its per-conflict read is GET /repair/genchoice/report — the opposite of
    //    the table's assumption that report was the terminal one. Both exist, so
    //    both are wired: result -> result, report -> report.
    //  * repair.genchoice also has POST /repair/genchoice/build, which WRITES
    //    two new archives. Not reachable from here.
    //  * audit and dump have no result route at all; their terminal read is the
    //    report. wz_job_result falls back to it and says so.
    //  * import has no separate result route: GET /api/import/progress carries
    //    the finished ImportResult on its `result` field. That is the fallback
    //    wz_job_result uses, and it is the same route as progress.
    //  * dump has POST /api/dump/preflight — a real backend-computed preview of
    //    what the start would refuse — so commit=false on dump goes there rather
    //    than posting nothing. audit has GET /api/audit/plan, which is a cheaper
    //    plan rather than a refusal preview, so it is reported alongside.
    private static readonly JobRoutes[] Routes =
    {
        new("audit", "api/audit/start", "api/audit/progress", "api/audit/cancel",
            Result: null, Report: "api/audit/report",
            Options: "AuditOptions: folder (required), families, maxPerCheck, maxImagesPerArchive, mapleVersion, gameVersion",
            Stages: "start, progress, cancel, report"),
        new("dump", "api/dump/start", "api/dump/progress", "api/dump/cancel",
            Result: null, Report: "api/dump/report",
            Options: "DumpJobRequest: path, outputDir, format (\"tree\"|\"png\"), resolveLinks, allowNonEmpty, maxNodes",
            Stages: "start, progress, cancel, report"),
        new("compose", "api/compose/build", "api/compose/progress", "api/compose/cancel",
            Result: "api/compose/result", Report: "api/compose/ledger",
            Options: "CompositionStartRequest: manifest, outputFolder, hashParts, stopOnRefusal",
            Stages: "start, progress, cancel, result, ledger"),
        new("import", "api/import/open", "api/import/progress", "api/import/cancel",
            Result: "api/import/progress", Report: null,
            Options: "OpenSplitRequest: sourceFolder (required), archive (required, name as /api/import/detect reports it)",
            Stages: "start, progress, cancel, progress-as-result"),
        new("repair.canvas-format", "api/repair/canvas-format/scan", "api/repair/canvas-format/progress",
            "api/repair/canvas-format/cancel", Result: "api/repair/canvas-format/result",
            Report: "api/repair/canvas-format/scan",
            Options: "CanvasRepairOptions: path (required), mapleVersion, gameVersion, maxCases",
            Stages: "start, progress, cancel, result, scan"),
        new("repair.canvas-dir-link", "api/repair/canvas-dir-link/scan", "api/repair/canvas-dir-link/progress",
            "api/repair/canvas-dir-link/cancel", Result: "api/repair/canvas-dir-link/result",
            Report: "api/repair/canvas-dir-link/scan",
            Options: "CanvasDirLinkOptions: folder (required), family, links, mapleVersion, gameVersion, maxCases, minimumArchiveBytes",
            Stages: "start, progress, cancel, result, scan"),
        new("repair.donor-restore", "api/repair/donor-restore/scan", "api/repair/donor-restore/progress",
            "api/repair/donor-restore/cancel", Result: "api/repair/donor-restore/result",
            Report: "api/repair/donor-restore/scan",
            Options: "DonorRestoreOptions: folder (required), donors (required), family, links, mapleVersion, gameVersion, maxCases, targetArchive, minimumArchiveBytes",
            Stages: "start, progress, cancel, result, scan"),
        new("repair.genchoice", "api/repair/genchoice/prepare", "api/repair/genchoice/progress",
            "api/repair/genchoice/cancel", Result: "api/repair/genchoice/result",
            Report: "api/repair/genchoice/report",
            Options: "DonorRestoreOptions: folder (required), donors (required)",
            Stages: "start, progress, cancel, result, report"),
    };

    private static readonly string[] Kinds = [.. Routes.Select(r => r.Kind)];

    /// <summary>Finds a kind, or names every kind that does exist.</summary>
    private static JobRoutes Resolve(string kind)
    {
        JobRoutes? row = Routes.FirstOrDefault(
            r => string.Equals(r.Kind, kind?.Trim(), StringComparison.OrdinalIgnoreCase));
        return row ?? throw new ModelContextProtocol.McpException(
            $"unknown kind '{kind}'. These are the kinds: {string.Join(", ", Kinds)}.");
    }

    /// <summary>Resolves a kind and refuses when it has no route for this stage.</summary>
    private static string Route(string kind, string stage, Func<JobRoutes, string?> pick)
    {
        JobRoutes row = Resolve(kind);
        string? route = pick(row);
        if (route is null)
            throw new ModelContextProtocol.McpException(
                $"kind '{row.Kind}' has no {stage} stage. Kinds that do: "
                + string.Join(", ", Routes.Where(r => pick(r) is not null).Select(r => r.Kind))
                + $". '{row.Kind}' has: {row.Stages}.");
        return route;
    }

    /// <summary>Merges caller options verbatim over the body. Nothing is dropped.</summary>
    private static JsonObject Merge(JsonObject body, string? options)
    {
        if (string.IsNullOrWhiteSpace(options)) return body;
        JsonNode? given;
        try { given = JsonNode.Parse(options); }
        catch (JsonException bad)
        {
            throw new ModelContextProtocol.McpException(
                $"options is not valid JSON: {bad.Message} Pass an object, e.g. {{\"folder\":\"C:\\\\...\"}}.");
        }
        if (given is not JsonObject obj)
            throw new ModelContextProtocol.McpException("options must be a JSON object, not an array or a scalar.");
        foreach (KeyValuePair<string, JsonNode?> kv in obj) body[kv.Key] = kv.Value?.DeepClone();
        return body;
    }

    /// <summary>
    /// Caps a result body to <paramref name="limit"/> rows in the arrays a
    /// backend report is built from, leaving the counts intact.
    /// </summary>
    /// <remarks>
    /// An audit report over a real client is thousands of findings and would
    /// bury the context of the agent reading it. The caps are applied to the
    /// arrays and the original counts are left in place beside them, so a
    /// truncated list never reads as a complete one.
    /// </remarks>
    private static JsonNode? Cap(JsonNode? node, int limit)
    {
        if (node is not JsonObject obj || limit <= 0) return node;
        var kept = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (kv.Value is JsonArray arr && arr.Count > limit)
            {
                kept[kv.Key] = new JsonObject
                {
                    ["returned"] = limit,
                    ["total"] = arr.Count,
                    ["note"] = $"Truncated to {limit} of {arr.Count}. The count above is the real one.",
                    ["items"] = new JsonArray(arr.Take(limit).Select(n => n?.DeepClone()).ToArray()),
                };
            }
            else kept[kv.Key] = kv.Value?.DeepClone();
        }
        return kept;
    }

    // ---------------------------------------------------------------- tools

    /// <summary>What each kind is called, what it accepts, and which stages it has.</summary>
    [McpServerTool(Name = "wz_job_capabilities"),
     Description("The job families this server can drive: the eight valid 'kind' values, the route behind each stage (start/progress/cancel/result/report), the option fields each start route accepts, and what the backend reports about dumps and composition folders. Start here — the other five wz_job_* tools all take one of these kinds.")]
    public static async Task<string> Capabilities(MapClient client, CancellationToken ct = default)
    {
        var kinds = new JsonArray();
        foreach (JobRoutes row in Routes)
        {
            kinds.Add(new JsonObject
            {
                ["kind"] = row.Kind,
                ["start"] = row.Start,
                ["progress"] = row.Progress,
                ["cancel"] = row.Cancel,
                ["result"] = row.Result,
                ["report"] = row.Report,
                ["stages"] = row.Stages,
                ["options"] = row.Options,
            });
        }

        // Both of these need a folder path and both refuse without one, so they
        // are called with a placeholder: the point is the shape of the answer,
        // and a caller that has a real folder re-reads it there.
        JsonNode? dumpOptions = await Probe(client, "api/dump/options", "path=C:\\MUWORK\\GAME\\client", ct);
        JsonNode? archives = await Probe(client, "api/compose/archives", "path=C:\\MUWORK\\GAME\\client", ct);

        return MapClient.J(new JsonObject
        {
            ["kinds"] = kinds,
            ["dumpOptions"] = dumpOptions,
            ["composeArchives"] = archives,
            ["note"] =
                "dump/options and compose/archives were read with a placeholder folder; "
                + "call them with a real one for a real answer. Every result/report route "
                + "404s until a run of that kind has finished — that is the backend saying "
                + "'nothing has run', not 'nothing was found'.",
        });
    }

    /// <summary>
    /// Reads an endpoint that needs a path, tolerating the refusal it answers
    /// for a placeholder. A capability probe must not fail because there is no
    /// client on this machine yet.
    /// </summary>
    private static async Task<JsonNode?> Probe(
        MapClient client, string route, string query, CancellationToken ct)
    {
        try { return await client.GetAsync($"{route}?{query}", ct); }
        catch (MapClient.BackendError refused)
        {
            return new JsonObject
            {
                ["unavailable"] = refused.Message,
                ["route"] = $"{route}?{query}",
            };
        }
    }

    [McpServerTool(Name = "wz_job_start"),
     Description("Start one of the eight job families by kind. commit=false (the default) posts nothing and returns the body that WOULD be posted, except for dump, which posts to the backend's own /api/dump/preflight. commit=true starts the run: scans and audits write nothing, but they hold open archives and memory for minutes — poll wz_job_progress and finish with wz_job_cancel. The repair APPLY routes are not reachable here; only the read-only scans are.")]
    public static async Task<string> Start(
        MapClient client,
        [Description("One of: audit, dump, compose, import, repair.canvas-format, repair.canvas-dir-link, repair.donor-restore, repair.genchoice")] string kind,
        [Description("False (default) posts nothing. True starts the run for real.")] bool commit = false,
        [Description("A JSON object of the start route's own options, merged verbatim into the POST body — e.g. {\"folder\":\"C:\\\\MUWORK\\\\...\"}. Unknown fields are sent as given rather than dropped, so no endpoint field is silently lost. See wz_job_capabilities for each kind's field list.")] string? options = null,
        CancellationToken ct = default)
    {
        JobRoutes row = Resolve(kind);

        // audit and dump each have a backend-computed preview. audit's is a plan
        // (GET /api/audit/plan) rather than a refusal list, so it is shown next
        // to the body; dump's is POST /api/dump/preflight, which is exactly
        // "what would the start refuse", so it is what commit=false reaches.
        if (!commit && string.Equals(row.Kind, "dump", StringComparison.OrdinalIgnoreCase))
        {
            JsonObject body = Merge(new JsonObject(), options);
            return MapClient.J(new JsonObject
            {
                ["committed"] = false,
                ["kind"] = row.Kind,
                ["previewed"] = "api/dump/preflight",
                ["preflight"] = await client.PostAsync("api/dump/preflight", body, ct),
                ["startRoute"] = row.Start,
                ["note"] =
                    "commit=false reached the backend's own preflight, so the refusals above are "
                    + "the ones the start would hit. It is still not the start: nothing is written, "
                    + "no run is reserved, and a preflight that passes does not promise the write "
                    + "will fit on the disk.",
            });
        }

        if (!commit)
        {
            JsonObject body = Merge(new JsonObject(), options);
            JsonObject preview = new()
            {
                ["committed"] = false,
                ["kind"] = row.Kind,
                ["route"] = row.Start,
                ["request"] = body,
                ["note"] =
                    $"'{row.Kind}' has no refusal-preview route, so NOTHING was sent. "
                    + "The body above is what would be posted, not what the backend would say "
                    + "about it: no field was validated, no path checked, no service reserved. "
                    + "Run it with commit=true to find out whether it actually works.",
            };
            if (string.Equals(row.Kind, "audit", StringComparison.OrdinalIgnoreCase))
            {
                // Read defensively: options is passed through verbatim, so `folder`
                // can legitimately arrive as a non-string and must not throw here.
                string folder = (body["folder"] as JsonValue)?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    preview["plan"] = await Probe(client, "api/audit/plan",
                        "path=" + MapClient.Q(folder), ct);
                }
            }
            return MapClient.J(preview);
        }

        JsonObject real = Merge(new JsonObject(), options);
        JsonNode? started = await client.PostAsync(row.Start, real, ct);
        return MapClient.J(new JsonObject
        {
            ["committed"] = true,
            ["kind"] = row.Kind,
            ["route"] = row.Start,
            ["firstSnapshot"] = started,
            ["pollWith"] = row.Progress,
            ["note"] =
                "Started. The snapshot is the first progress report, not the outcome — poll it. "
                + "Nothing was written to disk by this call; a repair writes only from its own "
                + "/apply route, which this tool does not reach.",
        });
    }

    [McpServerTool(Name = "wz_job_progress"),
     Description("Poll a running job by kind. Returns the backend's own snapshot (state, phase, counts). Cancel is cooperative, so a cancel followed immediately by this call can still report running. state='done' means read wz_job_result or wz_job_report; a 'failed' snapshot carries the error.")]
    public static async Task<string> Progress(
        MapClient client,
        [Description("One of: audit, dump, compose, import, repair.canvas-format, repair.canvas-dir-link, repair.donor-restore, repair.genchoice")] string kind,
        CancellationToken ct = default) =>
        MapClient.J(await client.GetAsync(Route(kind, "progress", r => r.Progress), ct));

    [McpServerTool(Name = "wz_job_result"),
     Description("Read a finished job's outcome by kind. Uses the kind's result route where it has one; for audit and dump, which have none, it reads the report route instead and says which it used. 404 means no run of that kind has finished in this session — that is NOT the same as a clean result. limit caps how many rows of each list come back (0 = no cap); the true count is always reported beside the truncation.")]
    public static async Task<string> Result(
        MapClient client,
        [Description("One of: audit, dump, compose, import, repair.canvas-format, repair.canvas-dir-link, repair.donor-restore, repair.genchoice")] string kind,
        [Description("Cap on rows per list in the result, 0 for no cap. Counts are always reported, so a capped list never reads as a complete one.")] int limit = 50,
        CancellationToken ct = default)
    {
        JobRoutes row = Resolve(kind);
        string? route = row.Result;
        // audit and dump have no result route at all; their terminal read is the
        // report, and this tool says so rather than silently answering from it.
        if (route is null) route = Route(kind, "result", r => r.Report);
        bool fellBack = row.Result is null;

        JsonNode? body = Cap(await client.GetAsync(route, ct), limit);
        var outp = new JsonObject
        {
            ["kind"] = row.Kind,
            ["route"] = route,
            ["body"] = body,
        };
        if (fellBack)
        {
            outp["note"] = $"'{row.Kind}' has no distinct result route; this is its report route. "
                          + "A repair's result route is the APPLY outcome and stays 404 until an "
                          + "apply has run — the read-only scan's verdict is the report route.";
        }
        return MapClient.J(outp);
    }

    [McpServerTool(Name = "wz_job_report"),
     Description("Read a kind's other terminal read by kind: the findings report for audit/dump, the ledger for compose, the scan verdict for a repair, the per-conflict report for repair.genchoice. For a kind with only one terminal read this returns the same body as wz_job_result and names the route it used. 404 means nothing of that kind has finished in this session.")]
    public static async Task<string> Report(
        MapClient client,
        [Description("One of: audit, dump, compose, import, repair.canvas-format, repair.canvas-dir-link, repair.donor-restore, repair.genchoice")] string kind,
        CancellationToken ct = default)
    {
        JobRoutes row = Resolve(kind);
        if (row.Report is null)
        {
            // import has no separate terminal read: its finished ImportResult
            // rides on the progress snapshot's `result` field. Both terminals
            // are that one route, so either resolution finds it; the throw is
            // unreachable for every kind in the table but keeps it honest.
            string route = row.Result ?? Route(kind, "report", r => r.Result);
            return MapClient.J(new JsonObject
            {
                ["kind"] = row.Kind,
                ["route"] = route,
                ["body"] = await client.GetAsync(route, ct),
                ["note"] = $"'{row.Kind}' has no report route; this is its progress route, whose "
                          + "`result` field carries the finished import. Same route wz_job_result uses.",
            });
        }

        JsonNode? body = await client.GetAsync(row.Report, ct);
        var outp = new JsonObject
        {
            ["kind"] = row.Kind,
            ["route"] = row.Report,
            ["body"] = body,
        };
        if (row.Result is null)
        {
            outp["note"] = $"'{row.Kind}' has only one terminal read, so this is the same body "
                          + "wz_job_result returns — the report IS its result.";
        }
        return MapClient.J(outp);
    }

    [McpServerTool(Name = "wz_job_cancel"),
     Description("Ask a running job of any kind to stop. Cooperative: the run finishes its current checkpoint first, so poll wz_job_progress to confirm the terminal state — a cancel is a request, not a fact. Cancelling an audit, scan or dump releases the archives it was holding; it does not undo anything, though these routes never write in the first place.")]
    public static async Task<string> Cancel(
        MapClient client,
        [Description("One of: audit, dump, compose, import, repair.canvas-format, repair.canvas-dir-link, repair.donor-restore, repair.genchoice")] string kind,
        CancellationToken ct = default)
    {
        string route = Route(kind, "cancel", r => r.Cancel);
        return MapClient.J(new JsonObject
        {
            ["kind"] = Resolve(kind).Kind,
            ["route"] = route,
            ["snapshot"] = await client.PostAsync(route, null, ct),
            ["note"] = "Cancel asked for, not confirmed — poll the progress route for the terminal state.",
        });
    }
}