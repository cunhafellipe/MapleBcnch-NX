using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The domain editors — skill, mob, NPC, String.wz and the cross-domain
/// database — as one family of six tools that dispatch on a <c>kind</c> argument.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this family exists when <c>wz_asset_*</c> already reaches any node by
/// path.</b> Because those five domains are not just nodes. Each carries its own
/// field validation, its own bulk semantics and its own DTOs: a skill field
/// holding a formula over the level cannot be bulk-added to (<c>SkillService</c>
/// refuses it by name), an NPC key holding a group is not a scalar, and a
/// String.wz id has to be filed under the right Eqp.img category and Map.img
/// region or the entry lands beside the real one. Writing the same leaf through
/// <c>/api/node/value</c> bypasses every one of those checks and can leave a
/// value the domain cannot load — a <c>damage</c> created as a String where the
/// client calls GetInt, or a second String.wz entry under a different spelling
/// of the same id. The archive loads; the client misreads it. These routes are
/// the ones that know the difference.
/// </para>
///
/// <para>
/// <b>An unknown kind throws rather than guessing a route.</b> Guessing would be
/// worse than a missing tool here: these routes write, and writing to the wrong
/// domain is not something a caller can notice from the result. Every refusal
/// below names the valid set, and every refusal is a plain
/// <c>McpException</c> — a problem with the arguments this tool was handed, not
/// with what the backend said about them.
/// </para>
///
/// <para>
/// <b>Six tools, not one per endpoint.</b> The endpoints answer five domains that
/// a caller thinks of as one subject — "the data" — and a tool surface that
/// mirrored the routing table would bury the two or three calls that matter
/// behind two hundred signatures that never get chosen. The kind argument keeps
/// the domain visible where it belongs, in the call.
/// </para>
///
/// <para>
/// <b>Commit is not a uniform concept across these domains, and the difference
/// matters.</b> The bulk DTOs carry their own <c>dryRun</c> and default to true,
/// so a preview of a bulk edit is a real backend answer — per-row before/after
/// and a reason for every row it would refuse — and that is what
/// <c>wz_data_bulk</c> returns. The single-write routes do not have that flag:
/// <c>/api/skill/level</c>, <c>/api/npc/fields</c> and <c>/api/string/write</c>
/// write the moment they are called. <c>wz_data_write</c> therefore previews
/// through the bulk route when the same edit is expressible there, and says so
/// plainly when it is not, rather than pretending an unvalidated local echo is a
/// preview.
/// </para>
/// </remarks>
[McpServerToolType]
public static class DataDomainTools
{
    // The closed kind sets. Each is checked by a switch below as well, and
    // quoted again in the refusal — so a caller who guesses gets the list
    // rather than a route that happens to exist.
    private const string ListKinds = "skill, mob, npc, string, books, cashshop";
    private const string DetailKinds = "skill, mob, npc, string, item";
    private const string WriteKinds = "skill, npc, string";
    private const string BulkKinds = "skill, npc, string";

    /// <summary>
    /// The properties a String.wz write takes, from StringWriteRequest and
    /// StringBulkEntry, which carry the identical set. Category and region only
    /// decide where a created entry is filed; they write nothing themselves.
    /// </summary>
    private static readonly string[] StringFields =
        { "name", "desc", "func", "mapName", "streetName", "category", "region" };

    // ------------------------------------------------------------------
    // 1. What can this backend do?
    // ------------------------------------------------------------------

    [Description(
        "What each domain editor can currently do: whether Skill.wz, Mob.wz and Npc.wz are open, "
        + "whether String.wz is open for names, which String.wz kinds and fields are editable, the "
        + "skill formula dialect, and the accepted bulk operations and rounding modes. Answers "
        + "'why is this tool refusing everything' in one call — an unavailable domain reads "
        + "available:false here rather than as five separate 400s. Read-only, no arguments, writes "
        + "nothing.")]
    [McpServerTool(Name = "wz_data_capabilities")]
    public static async Task<string> Capabilities(MapClient client, CancellationToken ct = default)
    {
        // All five, because the interesting failure is the combination: Mob.wz
        // can be open while String.wz is not, which reads exactly like a backend
        // that has forgotten every name.
        return MapClient.J(new JsonObject
        {
            ["skill"] = await client.GetAsync("api/skill/capabilities", ct),
            ["npc"] = await client.GetAsync("api/npc/capabilities", ct),
            ["string"] = await client.GetAsync("api/string/capabilities", ct),
            ["mob"] = await client.GetAsync("api/mob/capabilities", ct),
            ["db"] = await client.GetAsync("api/db/capabilities", ct),
        });
    }

    // ------------------------------------------------------------------
    // 2. Browse
    // ------------------------------------------------------------------

    [Description(
        "Browse one domain. kind: skill (/api/skill/list), mob, npc, string, books (the skill images "
        + "in one archive), cashshop. skill/mob/npc return a page with total and truncated; string "
        + "returns the String.wz index for one kind; cashshop returns every Commodity.img entry for "
        + "one fileId — that route has no paging and no on-sale filter, so limit and offset are "
        + "ignored there. Read-only, writes nothing. Paging is always sent, so a paged shape comes "
        + "back rather than a whole archive: names resolve only when String.wz is open.")]
    [McpServerTool(Name = "wz_data_list")]
    public static async Task<string> List(MapClient client,
        [Description("skill | mob | npc | string | books | cashshop. Anything else is refused by name.")] string kind,
        [Description("Restrict to one open archive id from wz_asset_files, e.g. f1. Omit for all open ones.")] string? fileId = null,
        [Description("Name or id substring. Only the string list reads this.")] string? q = null,
        [Description("Narrow skill to one book image, e.g. beginner. Only the skill list reads this.")] string? book = null,
        [Description("String.wz kind for kind=string: item | mob | skill | npc | map. Not the dispatch kind.")] string stringKind = "item",
        [Description("Resolve display names from String.wz. Defaults to true because every one of these endpoints defaults it that way; names come back empty when String.wz is not open.")] bool names = true,
        [Description("Rows to skip. Default 0.")] int offset = 0,
        [Description("Rows to return. Default 50; the backend clamps String.wz to 500.")] int limit = 50,
        CancellationToken ct = default)
    {
        string route = kind switch
        {
            // Both paged routes take the same five parameters; only the book
            // image is skill-specific.
            "skill" => Paged("api/skill/list", fileId, book, names, offset, limit),
            "mob" => Paged("api/mob/list", fileId, null, names, offset, limit),
            "npc" => Paged("api/npc/list", fileId, null, names, offset, limit),

            // The String.wz index. Its own `kind` is mandatory on the route and
            // is not the dispatch kind — asking for "mob" rows of the String.wz
            // index means kind=string with stringKind=mob.
            "string" => $"api/string/list?kind={MapClient.Q(stringKind)}&limit={Math.Clamp(limit, 1, 500)}"
                        + (Blank(q) ? "" : $"&q={MapClient.Q(q!)}"),

            // One skill image is one book: the narrowest way into a skill list
            // without paying for all 4,846 of them.
            "books" => "api/skill/books" + (Blank(fileId) ? "" : $"?fileId={MapClient.Q(fileId!)}"),

            // fileId is REQUIRED here — the route reads one archive's
            // Commodity.img and has no "all archives" answer.
            "cashshop" => CashShop(fileId, names),

            _ => throw Bad("list", kind, ListKinds),
        };

        return MapClient.J(await client.GetAsync(route, ct));
    }

    // ------------------------------------------------------------------
    // 3. Read one entry
    // ------------------------------------------------------------------

    [Description(
        "Read one entry in full. kind: skill, mob or npc take `path` from wz_data_list and return "
        + "the level/field table or the info card, including the warnings the domain attaches (a "
        + "skill with a common block ignores level/, an NPC whose container needs the Explorer). "
        + "kind=string takes a numeric id and answers for an id with no entry too, with present:false "
        + "— that is the 'my new item has no name' state, a fact to report rather than an error. "
        + "kind=item takes an item id and returns its String.wz entry plus the icon URL; that icon "
        + "route serves PNG bytes and a 404 there means the item has no icon, not that the id is "
        + "wrong. Read-only, writes nothing.")]
    [McpServerTool(Name = "wz_data_detail")]
    public static async Task<string> Detail(MapClient client,
        [Description("skill | mob | npc | string | item. Anything else is refused by name.")] string kind,
        [Description("Entry path from wz_data_list, e.g. f1/522.img/skill/5221017. Required for skill, mob and npc.")] string? path = null,
        [Description("Numeric id. Required for kind=string and kind=item, e.g. 1302000.")] int id = 0,
        [Description("String.wz kind for kind=string: item | mob | skill | npc | map.")] string stringKind = "item",
        CancellationToken ct = default)
    {
        if (kind is "skill" or "mob" or "npc")
        {
            if (Blank(path)) throw new ModelContextProtocol.McpException($"kind '{kind}' needs a `path` from wz_data_list.");
            return MapClient.J(await client.GetAsync(
                $"api/{kind}/detail?path={MapClient.Q(path!)}", ct));
        }

        if (kind == "string")
        {
            Require(id > 0, "kind 'string' needs a positive `id`.");
            return MapClient.J(await client.GetAsync(
                $"api/string/entry?kind={MapClient.Q(stringKind)}&id={id}", ct));
        }

        if (kind == "item")
        {
            // The icon route answers with PNG bytes, which this server does not
            // inline — a base64 image would cost more context than the answer is
            // worth (the same reasoning the csproj gives for keeping every other
            // tool text-only). So the detail of an item is what the JSON routes
            // can answer: the String.wz entry that names it, and where the icon
            // lives for a caller that wants the bytes.
            Require(id > 0, "kind 'item' needs a positive `id`.");
            return MapClient.J(new JsonObject
            {
                ["id"] = id,
                ["entry"] = await client.GetAsync($"api/string/entry?kind=item&id={id}", ct),
                ["iconUrl"] = $"/api/cashshop/icon/{id}",
                ["iconNote"] = "PNG bytes, fetched directly from the backend. "
                             + "A 404 answers \"No icon found for this item.\" — the id is fine, "
                             + "this item has no icon in the open archives.",
            });
        }

        throw Bad("detail", kind, DetailKinds);
    }

    // ------------------------------------------------------------------
    // 4. One edit on one entry
    // ------------------------------------------------------------------

    [Description(
        "Edit ONE entry and commit it. WRITES unless commit is false. kind=npc writes info/<key> "
        + "fields; kind=string writes display text for one id and creates the entry when it has none; "
        + "kind=skill performs one level operation — op add|clone|remove|rename over level/N. The "
        + "three kinds have three different shapes, so `fields` carries the DTO's own property names: "
        + "npc [\"name=哈比\",\"level=30\"], string [\"name=蘑菇村长\",\"func=村长\"], skill "
        + "[\"op=add\",\"level=6\"]. commit=false sends nothing and returns the backend's own "
        + "preview where one exists — an npc or string edit is previewed through the matching bulk "
        + "dry run, which reports before/after and a reason per row. A skill level operation has no "
        + "dry run anywhere in the backend, so its preview is the CURRENT level table plus the exact "
        + "request, and says so rather than claiming validation it did not get.")]
    [McpServerTool(Name = "wz_data_write")]
    public static async Task<string> Write(MapClient client,
        [Description("skill | npc | string. Anything else is refused by name.")] string kind,
        [Description("Semi-separated key=value pairs — \"name=value;level=3\" — using that kind's DTO property names.")] string[] fields,
        [Description("Entry path, e.g. f1/522.img/skill/5221017. Required for skill and npc.")] string? path = null,
        [Description("Numeric id. Required for kind=string.")] int id = 0,
        [Description("String.wz kind for kind=string: item | mob | skill | npc | map.")] string stringKind = "item",
        [Description("Actually write it. Default false — preview first.")] bool commit = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Require(fields.Length > 0, "Give at least one key=value field; nothing else is known what to change.");

        return kind switch
        {
            "skill" => await WriteSkill(client, fields, path, commit, ct),
            "npc" => await WriteNpc(client, fields, path, commit, ct),
            "string" => await WriteString(client, fields, id, stringKind, commit, ct),
            _ => throw Bad("write", kind, WriteKinds),
        };
    }

    /// <summary>
    /// A skill write is a level operation, because that is what /api/skill/level
    /// is: add | clone | remove | rename over <c>level/N</c>. The literal values
    /// inside a level cell are a different route (/api/skill/levels) and a
    /// different DTO, and a caller changing one cell at a time is far better off
    /// in wz_data_bulk, which previews the before/after for each row.
    /// </summary>
    private static async Task<string> WriteSkill(
        MapClient client, string[] fields, string? path, bool commit, CancellationToken ct)
    {
        if (Blank(path)) throw new ModelContextProtocol.McpException("kind 'skill' needs a `path` from wz_data_list.");
        // Every element: op=add;level=6 is two pairs, and reading only the first
        // would send op=add with level 0 and refuse a write the caller asked for.
        Dictionary<string, string> pairs = Merge(fields, "field");

        var body = new JsonObject
        {
            ["path"] = path,
            // 'op' defaults to add server-side; sent explicitly so the body that
            // is previewed is the body that will be posted.
            ["op"] = Text(pairs, "op") ?? "add",
            ["level"] = Int(pairs, "level", 0),
            ["from"] = Int(pairs, "from", 0),
            ["to"] = Int(pairs, "to", 0),
        };
        Allowed(pairs, "skill", "op", "level", "from", "to");

        if (commit)
            return MapClient.J(await client.PostAsync("api/skill/level", body, ct));

        // There is no dryRun on SkillLevelRequest — the route writes the moment
        // it is called, so this must not be posted. What IS worth showing is the
        // table the operation would act on: 'level 6 already exists' and
        // 'level 4 does not exist' are both visible in the current detail, and
        // both are what the backend would refuse on.
        return MapClient.J(new JsonObject
        {
            ["committed"] = false,
            ["route"] = "api/skill/level",
            ["request"] = body,
            ["current"] = await client.GetAsync(
                $"api/skill/detail?path={MapClient.Q(path!)}", ct),
            ["note"] = "Preview only: nothing was sent. /api/skill/level has no dry run, so this is "
                     + "the current level table plus the request, not a backend-computed diff. "
                     + "Check `current.levels` for a level that already exists before committing.",
        });
    }

    /// <summary>
    /// NPC fields go to <c>info/&lt;key&gt;</c>. The write DTO has no dry run
    /// either, but the bulk one does and it writes the same leaves, so a preview
    /// is a real backend answer rather than an echo. One field previews in a
    /// single call; several are previewed one at a time, because NpcBulkRequest
    /// carries a single field for all of its paths.
    /// </summary>
    private static async Task<string> WriteNpc(
        MapClient client, string[] fields, string? path, bool commit, CancellationToken ct)
    {
        if (Blank(path)) throw new ModelContextProtocol.McpException("kind 'npc' needs a `path` from wz_data_list.");

        List<(string Key, string Value)> rows = new(fields.Length);
        foreach (string field in fields)
        {
            Dictionary<string, string> pair = Pairs(field, "field");
            Require(pair.Count == 1,
                $"'{field}' must be exactly one key=value pair; several are allowed, "
                + "but each in its own element of `fields`.");
            rows.Add((Key(pair), Value(pair)));
        }

        if (commit)
        {
            var body = new JsonObject
            {
                ["path"] = path,
                ["fields"] = new JsonArray(rows
                    .Select(r => (JsonNode?)new JsonObject { ["key"] = r.Key, ["value"] = r.Value })
                    .ToArray()),
            };
            return MapClient.J(await client.PostAsync("api/npc/fields", body, ct));
        }

        // round=none: a preview reports the text that would be stored, so it must
        // not round a name to "0" on the way through.
        async Task<JsonNode?> DryRun((string Key, string Value) row)
        {
            var body = new JsonObject
            {
                ["paths"] = new JsonArray(path),
                ["field"] = row.Key,
                ["op"] = "set",
                ["value"] = row.Value,
                ["round"] = "none",
                ["dryRun"] = true,
            };
            return await client.PostAsync("api/npc/bulk", body, ct);
        }

        JsonArray previews = new();
        foreach ((string Key, string Value) row in rows) previews.Add(await DryRun(row));

        return MapClient.J(new JsonObject
        {
            ["committed"] = false,
            ["route"] = "api/npc/bulk",
            ["fields"] = rows.Count,
            ["preview"] = previews,
            ["note"] = rows.Count > 1
                ? "Preview only: nothing was sent. One dry run per field, because a bulk request "
                + "carries a single field for all of its paths. The committed write is one undo batch."
                : "Preview only: nothing was sent. before/after per row is the backend's own answer.",
        });
    }

    /// <summary>
    /// String.wz writes are the one case where the single-write DTO and the
    /// bulk DTO carry the same fields, so a dry run through /api/string/bulk is
    /// an exact preview of /api/string/write — including the case where the entry
    /// has to be created, and the Eqp.img category or Map.img region the backend
    /// picked for it.
    /// </summary>
    private static async Task<string> WriteString(
        MapClient client, string[] fields, int id, string stringKind, bool commit, CancellationToken ct)
    {
        Require(id > 0, "kind 'string' needs a positive `id`.");
        // Parsed per element rather than by re-joining: a name may legitimately
        // contain ';', and splitting on it again would cut it in half.
        Dictionary<string, string> pairs = Merge(fields, "field");

        // Checked HERE, before the commit/preview split, not only on the preview
        // path: a misspelled "nmae" that is quietly dropped from the committed
        // body is the one failure this family exists to prevent — the caller
        // would be told the name changed and nothing would have changed.
        Allowed(pairs, "string", new[] { "id" }.Concat(StringFields).ToArray());
        Require(HasText(pairs),
            "Give at least one of name, desc, func, mapName or streetName. "
            + "category and region only say WHERE a new entry goes; on their own they change nothing.");

        if (commit)
        {
            // StringWriteRequest's own properties: the same field set the bulk
            // entry carries, plus the kind. Deep-copied rather than moved — a
            // JsonNode that already belongs to another object throws on reparent,
            // and the entry below is still needed for nothing but this.
            var body = new JsonObject { ["kind"] = stringKind };
            foreach (string field in StringFields)
            {
                if (pairs.TryGetValue(field, out string? value)) body[field] = value;
            }
            return MapClient.J(await client.PostAsync("api/string/write", body, ct));
        }

        return MapClient.J(new JsonObject
        {
            ["committed"] = false,
            ["route"] = "api/string/bulk",
            ["preview"] = await client.PostAsync("api/string/bulk", new JsonObject
            {
                ["kind"] = stringKind,
                ["entries"] = new JsonArray(StringEntry(pairs, id)),
                ["dryRun"] = true,
            }, ct),
            ["note"] = "Preview only: nothing was sent. The dry run and the write take the same "
                     + "fields, so changes, createdEntry and group say exactly what committing does.",
        });
    }

    // ------------------------------------------------------------------
    // 5. Many entries, one call
    // ------------------------------------------------------------------

    [Description(
        "Apply one operation to many entries in a single call, previewed first. kind=skill and "
        + "kind=npc apply ONE field to MANY paths — that is the shape their bulk DTOs take, so "
        + "`entries` is a single entry holding the whole operation "
        + "(\"paths=f1/100.img/1000000,f1/101.img/1000001;field=exp;level=0;op=add;value=10;round=nearest\"). "
        + "kind=string takes one entry PER ROW (\"id=2000000;name=魔刀\"; \"id=2000001;desc=...\"). "
        + "op is set|add|multiply|percent, level 0 means the common block and N means that level. "
        + "Bulk never creates a field that is not already there, and never edits a formula or a "
        + "point — those rows come back skipped with the reason. commit=false is the backend's own "
        + "dry run: per-row before/after and a reason for every row it would refuse. Writes nothing "
        + "on a dry run; in-memory only until the archive is saved.")]
    [McpServerTool(Name = "wz_data_bulk")]
    public static async Task<string> Bulk(MapClient client,
        [Description("skill | npc | string. Anything else is refused by name.")] string kind,
        [Description("Semi-separated key=value entries; see the description for the shape each kind wants.")] string[] entries,
        [Description("String.wz kind for kind=string: item | mob | skill | npc | map.")] string stringKind = "item",
        [Description("Actually write it. Default false — the backend dry run is returned instead.")] bool commit = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return kind switch
        {
            // One operation, many paths: a single entry describes it. The
            // backend applies one field to every path in the list, so there is
            // nothing to be gained by pretending the rows differ.
            "skill" => await BulkSkill(client, entries, commit, ct),
            "npc" => await BulkNpc(client, entries, commit, ct),
            "string" => await BulkString(client, entries, stringKind, commit, ct),
            _ => throw Bad("bulk", kind, BulkKinds),
        };
    }

    private static async Task<string> BulkSkill(
        MapClient client, string[] entries, bool commit, CancellationToken ct)
    {
        Require(entries.Length == 1,
            "kind 'skill' takes exactly one entry: one operation applied to every path.");
        Dictionary<string, string> pairs = Pairs(entries[0], "field");
        Allowed(pairs, "skill", "paths", "field", "level", "op", "value", "round");

        string[] paths = Paths(pairs);
        var body = new JsonObject
        {
            ["paths"] = new JsonArray(paths.Select(p => (JsonNode?)p).ToArray()),
            ["field"] = Text(pairs, "field") ?? "",
            // level 0 is the common block. Sent always: there is no "all levels"
            // in this DTO, and a change nobody can see row by row is not
            // previewable anyway.
            ["level"] = Int(pairs, "level", 0),
            ["op"] = Text(pairs, "op") ?? "set",
            ["value"] = Real(pairs, "value", 0),
            ["round"] = Round(pairs),
            ["dryRun"] = !commit,
        };
        return MapClient.J(await client.PostAsync("api/skill/bulk", body, ct));
    }

    private static async Task<string> BulkNpc(
        MapClient client, string[] entries, bool commit, CancellationToken ct)
    {
        Require(entries.Length == 1,
            "kind 'npc' takes exactly one entry: one operation applied to every path.");
        Dictionary<string, string> pairs = Pairs(entries[0], "field");
        Allowed(pairs, "npc", "paths", "field", "op", "value", "round");

        string[] paths = Paths(pairs);
        var body = new JsonObject
        {
            ["paths"] = new JsonArray(paths.Select(p => (JsonNode?)p).ToArray()),
            ["field"] = Text(pairs, "field") ?? "",
            ["op"] = Text(pairs, "op") ?? "set",
            // Text, not a double: "set" writes a name verbatim, and NpcService
            // only parses it when the operation is arithmetic.
            ["value"] = Text(pairs, "value") ?? "",
            ["round"] = Round(pairs),
            ["dryRun"] = !commit,
        };
        return MapClient.J(await client.PostAsync("api/npc/bulk", body, ct));
    }

    private static async Task<string> BulkString(
        MapClient client, string[] entries, string stringKind, bool commit, CancellationToken ct)
    {
        Require(entries.Length > 0, "Give at least one \"id=...\" entry.");
        var rows = new JsonArray();
        foreach (string entry in entries) rows.Add(StringEntry(Pairs(entry, "field"), 0));

        return MapClient.J(await client.PostAsync("api/string/bulk", new JsonObject
        {
            ["kind"] = stringKind,
            ["entries"] = rows,
            ["dryRun"] = !commit,
        }, ct));
    }

    // ------------------------------------------------------------------
    // 6. Cross-domain
    // ------------------------------------------------------------------

    [Description(
        "Search every id and name the client knows, across items, mobs, skills, NPCs and maps at "
        + "once, with the id each name belongs to. This is the fastest way to answer \"what is "
        + "1302000\" or \"where is the Blue Sword\", which otherwise means knowing which archive to "
        + "open and guessing a path. `kind` here is the String.wz kind (item|mob|skill|npc|map) — "
        + "narrow the list, do not widen it. Read-only, writes nothing. Needs String.wz open; "
        + "available:false in the answer says it is not.")]
    [McpServerTool(Name = "wz_data_search")]
    public static async Task<string> Search(MapClient client,
        [Description("Name or id to look for, e.g. \"藍劍\" or \"1302000\".")] string q,
        [Description("Restrict to item | mob | skill | npc | map. Omit for all of them.")] string? kind = null,
        [Description("How many hits. Default 50; the backend clamps to 500 and reports `truncated`.")] int limit = 50,
        CancellationToken ct = default)
    {
        Require(!Blank(q), "Give something to search for.");
        string route = $"api/db/search?q={MapClient.Q(q)}&limit={Math.Clamp(limit, 1, 500)}";
        if (!Blank(kind)) route += $"&kind={MapClient.Q(kind!)}";
        return MapClient.J(await client.GetAsync(route, ct));
    }

    // ------------------------------------------------------------------
    // Argument plumbing
    // ------------------------------------------------------------------

    /// <summary>
    /// The cash shop, which is the one list with neither paging nor a filter.
    /// </summary>
    /// <remarks>
    /// Commodity.img is read whole and every route over it — list, next SN,
    /// write — is keyed by fileId alone. There is no on-sale filter either: the
    /// rows carry <c>onSale</c> and a caller that wants only what is for sale
    /// reads them. Inventing a query parameter the endpoint does not bind is the
    /// failure this whole family exists to avoid, so none is sent.
    /// </remarks>
    private static string CashShop(string? fileId, bool names)
    {
        Require(!Blank(fileId),
            "kind 'cashshop' needs a fileId: it reads one archive's Commodity.img and has no "
            + "all-archives answer.");
        return $"api/cashshop/items?fileId={MapClient.Q(fileId!)}&names={names}";
    }

    /// <summary>
    /// A page of skill/mob/npc rows. Offset and limit are always sent, because
    /// these routes return the WHOLE list when both are absent — which for a
    /// v232 client is 10,742 NPCs and 4,846 skills, and is how a caller ends up
    /// with a result too large to read.
    /// </summary>
    private static string Paged(string route, string? fileId, string? book, bool names, int offset, int limit)
    {
        string query = $"fileId={(Blank(fileId) ? "" : MapClient.Q(fileId!))}&names={names}"
                       + $"&offset={Math.Max(offset, 0)}&limit={Math.Clamp(limit, 1, 500)}";
        if (!Blank(book)) query += $"&book={MapClient.Q(book!)}";
        return $"{route}?{query}";
    }

    /// <summary>
    /// One String.wz row. Only the fields that were actually supplied appear:
    /// null means "leave it alone" and an empty string means "make it empty",
    /// and the backend treats those as different requests.
    /// </summary>
    private static JsonObject StringEntry(Dictionary<string, string> pairs, int id)
    {
        Allowed(pairs, "string", new[] { "id" }.Concat(StringFields).ToArray());

        int entryId = id > 0 ? id : Int(pairs, "id", 0);
        Require(entryId > 0, "Every string entry needs an `id`.");

        var entry = new JsonObject { ["id"] = entryId };
        foreach (string field in StringFields)
        {
            // Only what was supplied: null means "leave it alone" and "" means
            // "make it empty", and the backend treats those as different requests.
            if (pairs.TryGetValue(field, out string? value)) entry[field] = value;
        }
        return entry;
    }

    /// <summary>Splits "a=1;b=2" into its pairs, on the FIRST '=' of each part.</summary>
    private static Dictionary<string, string> Pairs(string entry, string what)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in entry.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int split = part.IndexOf('=');
            if (split <= 0)
                throw new ModelContextProtocol.McpException(
                    $"'{part}' is not a {what}: expected key=value.");
            pairs[part[..split].Trim()] = part[(split + 1)..].Trim();
        }
        return pairs;
    }

    /// <summary>
    /// Every element of <c>fields</c>, parsed into one lookup.
    /// </summary>
    /// <remarks>
    /// Element by element, not by joining on ';' and re-splitting: a name may
    /// contain a semicolon, and a second trip through the separator would cut it
    /// in half — "desc=第一;章" becoming two broken fields.
    /// </remarks>
    private static Dictionary<string, string> Merge(string[] fields, string what)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in fields)
        {
            foreach (KeyValuePair<string, string> pair in Pairs(field, what))
            {
                Require(!merged.ContainsKey(pair.Key),
                    $"'{pair.Key}' was given twice; say it once.");
                merged[pair.Key] = pair.Value;
            }
        }
        return merged;
    }

    /// <summary>
    /// Does this write any text, or only say where a new entry should go?
    /// </summary>
    private static bool HasText(Dictionary<string, string> pairs)
    {
        // The text fields proper. category and region are placement, not content:
        // they say where a created entry goes and write nothing themselves.
        foreach (string field in new[] { "name", "desc", "func", "mapName", "streetName" })
        {
            if (pairs.ContainsKey(field)) return true;
        }
        return false;
    }

    /// <summary>
    /// Refuses a property the DTO does not have, rather than dropping it.
    /// </summary>
    /// <remarks>
    /// A silently ignored "lvel=30" is worse than an error: the caller is told
    /// the write succeeded and gets a preview that does not contain the change
    /// they asked for.
    /// </remarks>
    private static void Allowed(Dictionary<string, string> pairs, string kind, params string[] names)
    {
        foreach (string key in pairs.Keys)
        {
            if (!names.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ModelContextProtocol.McpException(
                    $"'{key}' is not a property the {kind} endpoints take. Use: {string.Join(", ", names)}.");
        }
    }

    private static string[] Paths(Dictionary<string, string> pairs)
    {
        if (!pairs.TryGetValue("paths", out string? list) || Blank(list))
            throw new ModelContextProtocol.McpException("Give `paths`, comma separated, from wz_data_list.");
        return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(p => p.Trim()).ToArray();
    }

    /// <summary>
    /// Rounding is checked here because the backend does not: it falls through
    /// to nearest for anything it does not recognise, so a typo would quietly
    /// integerise every float field in the run instead of failing.
    /// </summary>
    private static string Round(Dictionary<string, string> pairs)
    {
        string round = Text(pairs, "round") ?? "nearest";
        Require(round is "nearest" or "floor" or "none",
            $"'round' is {round}; use nearest, floor or none.");
        return round;
    }

    /// <summary>
    /// A value, or null when the key was not given at all.
    /// </summary>
    /// <remarks>
    /// Present-but-empty returns empty, deliberately. The String.wz DTOs read
    /// null as "leave it alone" and "" as "make it empty", and collapsing the
    /// two here would silently turn a deliberate blank into a no-op. Numeric
    /// fields go through <see cref="Int"/>/<see cref="Real"/>, which do treat an
    /// empty string as absent — a number has no blank reading.
    /// </remarks>
    private static string? Text(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out string? value) ? value : null;

    private static int Int(Dictionary<string, string> pairs, string key, int fallback)
    {
        if (!pairs.TryGetValue(key, out string? raw) || raw.Length == 0) return fallback;
        Require(int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value),
            $"'{key}' must be a whole number, not '{raw}'.");
        return value;
    }

    /// <summary>
    /// Invariant, like every other number this client parses. WzNodeFactory
    /// parses with it too, so a comma-decimal desktop must not be allowed to
    /// disagree with it here.
    /// </summary>
    private static double Real(Dictionary<string, string> pairs, string key, double fallback)
    {
        if (!pairs.TryGetValue(key, out string? raw) || raw.Length == 0) return fallback;
        Require(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value),
            $"'{key}' must be a number, not '{raw}'.");
        return value;
    }

    /// <summary>The single key of a one-pair field, and its value.</summary>
    private static string Key(Dictionary<string, string> pair) =>
        pair.Keys.Single();

    private static string Value(Dictionary<string, string> pair) =>
        pair[pair.Keys.Single()];

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ModelContextProtocol.McpException(message);
    }

    /// <summary>
    /// One refusal shape for every kind in this family: the name it was given,
    /// and the set that would have worked.
    /// </summary>
    private static ModelContextProtocol.McpException Bad(string tool, string kind, string valid) =>
        new($"'{kind}' is not a kind wz_data_{tool} knows. Use one of: {valid}.");
}