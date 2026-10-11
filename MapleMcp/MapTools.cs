using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The map-authoring surface, as MCP tools.
/// </summary>
/// <remarks>
/// <para>
/// Cut by what an agent is actually trying to do, not by what the HTTP API
/// happens to expose. The 31 mapedit endpoints answer to thirty-one verbs;
/// nobody authoring a map chooses between "set-layer-ts" and "foothold-chain"
/// without first asking what they are for.
/// </para>
///
/// <para>
/// <b>Every mutating tool takes commit and does nothing without it.</b>
/// An agent that gets the selection wrong re-runs with commit=false and reads
/// exactly what it would have done — the same rule the older WzMcp applies, and
/// the reason those tools were shaped the way they were.
/// </para>
///
/// <para>
/// The map rules themselves live in <c>docs/MAP-STRUCTURE.md</c>, measured from
/// ten real maps. <c>wz_map_validate</c> is that document's executable copy.
/// </para>
/// </remarks>
[McpServerToolType]
public static class MapTools
{
    // ------------------------------------------------------------------
    // 1. Where am I?
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads a boolean out of a JSON node that may be absent or null.
    /// </summary>
    /// <remarks>
    /// Indexing a <see cref="JsonNode"/> yields <c>bool?</c>, and the difference
    /// between "false" and "not reported" matters here: <c>hasGeometry</c> absent
    /// means the field is not for us, not that the map is empty.
    /// </remarks>
    private static bool Flag(JsonNode? node, string field) =>
        node?[field] is JsonValue v && v.TryGetValue<bool>(out bool b) && b;

    /// <summary>Reads a string, or null when absent.</summary>
    private static string? Text(JsonNode? node, string field) =>
        node?[field] is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;

    [Description(
        "What the MapleBench backend can do right now: which archives are open, " +
        "how many maps they hold, and whether sprites and portal icons are " +
        "available. Call this first — everything else needs an open archive, and " +
        "an agent that skips this discovers it as five separate failures.")]
    [McpServerTool(Name = "wz_map_capabilities")]
    public static async Task<string> Capabilities(MapClient maps, CancellationToken ct)
    {
        JsonNode? files = await maps.GetAsync("api/files", ct);
        JsonNode? caps = await maps.GetAsync("api/mapedit/capabilities", ct);

        var openFiles = new JsonArray();
        if (files is JsonArray arr)
        {
            foreach (JsonNode? f in arr)
            {
                openFiles.Add(new JsonObject
                {
                    ["id"] = f?["id"]?.GetValue<string>(),
                    ["name"] = f?["name"]?.GetValue<string>(),
                    ["kind"] = f?["kind"]?.GetValue<string>(),
                    ["path"] = f?["filePath"]?.GetValue<string>(),
                });
            }
        }

        return MapClient.J(new JsonObject
        {
            ["reachable"] = true,
            ["baseUrl"] = maps.BaseUrl,
            ["openArchives"] = openFiles,
            ["capabilities"] = caps,
            ["note"] = caps is JsonObject c && !Flag(c, "available")
                ? "No map archive is open. Wz_map_open needs an archive path first."
                : null,
        });
    }

    // ------------------------------------------------------------------
    // 2-3. Find and open a map
    // ------------------------------------------------------------------

    [Description(
        "List maps inside an open archive. Filter with q (a name or id substring). " +
        "Returns at most `limit` rows, default 50. Map names come from String.wz " +
        "and are Chinese on this data set; open it first or names come back empty.")]
    [McpServerTool(Name = "wz_map_list")]
    public static async Task<string> List(MapClient maps,
        [Description("Name or id substring, e.g. \"蘑菇村\" or \"10000\"")] string? q = null,
        [Description("How many rows. Default 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        string route = $"api/mapedit/maps?limit={Math.Clamp(limit, 1, 500)}";
        if (!string.IsNullOrWhiteSpace(q))
            route += $"&q={MapClient.Q(q)}";

        JsonNode? result = await maps.GetAsync(route, ct);
        if (result is JsonObject obj)
        {
            obj["returned"] = (obj["maps"] as JsonArray)?.Count ?? 0;
            obj["next"] = "Wz_map_open takes `path` from a row here.";
        }
        return MapClient.J(result);
    }

    [Description(
        "Open a map for editing and return its whole structure: bounds, bgm, every " +
        "layer with its tiles and objects, footholds, portals, life and reactors. " +
        "Pass a `path` from wz_map_list. Read-only until a mutating tool commits.")]
    [McpServerTool(Name = "wz_map_open")]
    public static async Task<string> Open(MapClient maps,
        [Description("Session path, e.g. \"f1/Map/Map0/000000001.img\"")] string path,
        CancellationToken ct = default)
    {
        JsonNode? doc = await maps.PostAsync("api/mapedit/open", new { path }, ct);

        // A link stub has no geometry of its own; the shape lives in the target.
        // Saying so here saves the agent from building a map with no floor.
        if (doc is JsonObject d && !Flag(d, "hasGeometry"))
        {
            string? link = Text(d, "link");
            if (!string.IsNullOrEmpty(link))
            {
                d["warning"] =
                    $"This map is a link stub; its geometry lives in '{link}'. " +
                    "Open the target to read or edit the shape, or you will author " +
                    "against an empty map.";
            }
        }
        return MapClient.J(doc);
    }

    // ------------------------------------------------------------------
    // 4-5. Read the shape, choose the material
    // ------------------------------------------------------------------

    [Description(
        "The structural summary of a map that is already open: counts of tiles, " +
        "objects, footholds, portals and spawns, plus which tile sets it uses. " +
        "Cheaper than wz_map_open when you only need to know what is there.")]
    [McpServerTool(Name = "wz_map_doc")]
    public static async Task<string> Doc(MapClient maps,
        [Description("Session path of an open map")] string path,
        CancellationToken ct = default)
    {
        return MapClient.J(await maps.GetAsync($"api/mapedit/doc?path={MapClient.Q(path)}", ct));
    }

    [Description(
        "List the material sets available for building a map: Tile, Obj or Back. " +
        "Each set reports `drawableEntries` — how many of its entries carry actual " +
        "pixels. Prefer a set with a high number; a set with 0 parses but draws " +
        "nothing. Call with no arguments for Tile, the set that builds floors.")]
    [McpServerTool(Name = "wz_map_palette")]
    public static async Task<string> Palette(MapClient maps,
        [Description("Tile, Obj or Back. Default Tile.")] string kind = "Tile",
        CancellationToken ct = default)
    {
        JsonNode? sets = await maps.GetAsync(
            $"api/mapedit/palette/sets?kind={MapClient.Q(kind)}", ct);

        if (sets is JsonArray arr)
        {
            var usable = new JsonArray();
            var empty = new JsonArray();
            foreach (JsonNode? s in arr)
            {
                int drawable = s?["drawableEntries"] is JsonValue dv
                               && dv.TryGetValue<int>(out int d) ? d : 0;
                (drawable > 0 ? usable : empty).Add(new JsonObject
                {
                    ["name"] = Text(s, "name"),
                    ["drawableEntries"] = drawable,
                });
            }

            return MapClient.J(new JsonObject
            {
                ["kind"] = kind,
                ["total"] = arr.Count,
                ["usableCount"] = usable.Count,
                ["usable"] = usable,
                ["emptySets"] = empty,
                ["next"] = "Wz_map_palette_entries lists what is inside one set " +
                           "(pass its `path`).",
            });
        }
        return MapClient.J(sets);
    }

    // ------------------------------------------------------------------
    // 6-10. Change it
    // ------------------------------------------------------------------

    [Description(
        "Read one node by its address, showing each child's name, type and value. " +
        "Needed before any edit: addresses index into the document, and only a " +
        "leaf can be written — pointing setValue at a container is refused. " +
        "Use this to find the child holding the value you want to change.")]
    [McpServerTool(Name = "wz_map_node")]
    public static async Task<string> Node(MapClient maps,
        [Description("Session path of an open map")] string path,
        [Description("Address as a comma-separated index path, e.g. \"0,2,0\"")] string addr,
        CancellationToken ct = default)
    {
        JsonNode? node = await maps.GetAsync(
            $"api/mapedit/node?path={MapClient.Q(path)}&addr={MapClient.Q(addr)}", ct);

        if (node is JsonObject o)
        {
            o["hint"] = "Children listed here are the writable leaves. A container " +
                        "itself cannot be edited; address one of its children instead.";
        }
        return MapClient.J(node);
    }

    [Description(
        "Place one tile, object, foothold or portal on an open map. Returns the " +
        "address it landed at. With commit=false it reports what it would do and " +
        "changes nothing.")]
    [McpServerTool(Name = "wz_map_place")]
    public static async Task<string> Place(MapClient maps,
        [Description("Session path of an open map")] string path,
        [Description("tile, obj, foothold or portal")] string kind,
        [Description("X in map coordinates")] int x,
        [Description("Y in map coordinates")] int y,
        [Description("Layer index 0-7")] int layer = 0,
        [Description("Tile set name, required for kind=tile")] string? set = null,
        [Description("Sub-entry within the set, e.g. \"bsc\"")] string? u = null,
        [Description("Frame index within the sub-entry")] int? no = null,
        [Description("Actually write it. Default false — preview first.")] bool commit = false,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["path"] = path,
            ["kind"] = kind,
            ["x"] = x,
            ["y"] = y,
            ["layer"] = layer,
        };
        if (set is not null) body["set"] = set;
        if (u is not null) body["u"] = u;
        if (no is not null) body["no"] = no;
        if (kind == "tile") body["adoptLayerTs"] = true;

        if (!commit)
        {
            return MapClient.J(new JsonObject
            {
                ["preview"] = true,
                ["would"] = body,
                ["note"] = "Re-run with commit=true to apply.",
            });
        }
        return MapClient.J(await maps.PostAsync("api/mapedit/place", body, ct));
    }

    [Description(
        "Apply many edits as ONE undoable step. Use this to lay a floor: hundreds " +
        "of placements in one call, then one undo takes all of it back. The undo " +
        "history holds 200 entries, so sending these one at a time silently " +
        "discards the history — that is what this tool exists to prevent. " +
        "Stops at the first failure and names it; what already landed stays as " +
        "one undoable step. " +
        "ADDRESSES MUST NAME LEAVES. Pointing one at a container is refused " +
        "(\"is a SubProperty and its value cannot be edited here\"), and so is a " +
        "value of a different type (\"is an Int and 'x' is not a number\"). Read " +
        "the node with wz_map_node first to get a child that holds a real value.")]
    [McpServerTool(Name = "wz_map_batch")]
    public static async Task<string> Batch(MapClient maps,
        [Description("Session path of an open map")] string path,
        [Description(
            "The operations, as JSON. Each is {kind, addr, value} for a scalar " +
            "edit, or one of the shapes the /batch endpoint takes. Get addresses " +
            "from wz_map_open or wz_map_doc.")]
        string opsJson,
        [Description("History label shown in undo")] string? label = null,
        [Description("Actually write them. Default false — preview first.")] bool commit = false,
        CancellationToken ct = default)
    {
        JsonArray ops;
        try
        {
            ops = JsonNode.Parse(opsJson) as JsonArray
                  ?? throw new FormatException("opsJson must be a JSON array.");
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return MapClient.J(new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"opsJson is not a JSON array: {ex.Message}",
                ["expected"] = """[{"kind":"setValue","addr":[0,2,0,1],"value":"bsc"}]""",
            });
        }

        if (!commit)
        {
            return MapClient.J(new JsonObject
            {
                ["preview"] = true,
                ["operationCount"] = ops.Count,
                ["note"] = "Re-run with commit=true to apply. All of it undoes as one step.",
            });
        }

        var body = new JsonObject { ["path"] = path, ["ops"] = ops };
        if (label is not null) body["label"] = label;
        return MapClient.J(await maps.PostAsync("api/mapedit/batch", body, ct));
    }

    [Description(
        "Check an open map against the rules a generated map has to obey, and " +
        "report every problem at once. Catches severed foothold chains (which " +
        "look fine on screen and strand the character), spawns bound to a " +
        "foothold that does not exist, tiles and objects that cannot resolve to " +
        "art, and link stubs. Run this before wz_map_save — it is the difference " +
        "between a map that looks right and one that is playable.")]
    [McpServerTool(Name = "wz_map_validate")]
    public static async Task<string> Validate(MapClient maps,
        [Description("Session path of an open map")] string path,
        CancellationToken ct = default)
    {
        return MapClient.J(await maps.PostAsync("api/mapedit/validate", new { path }, ct));
    }

    [Description(
        "Write the map back to its archive. Verifies by re-reading the saved file " +
        "and comparing the tree, and reports the time, whether history survived, " +
        "and any differences it found. Run wz_map_validate first: this verifies " +
        "that the file matches the model, not that the map is playable.")]
    [McpServerTool(Name = "wz_map_save")]
    public static async Task<string> Save(MapClient maps,
        [Description("Session path of an open map")] string path,
        [Description("Actually write. Default false — preview first.")] bool commit = false,
        CancellationToken ct = default)
    {
        if (!commit)
        {
            return MapClient.J(new JsonObject
            {
                ["preview"] = true,
                ["path"] = path,
                ["note"] = "Saving rewrites the archive and keeps a timestamped backup. " +
                           "Re-run with commit=true to write.",
            });
        }
        return MapClient.J(await maps.PostAsync("api/mapedit/save", new { path }, ct));
    }
}