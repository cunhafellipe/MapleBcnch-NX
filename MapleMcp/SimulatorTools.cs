using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The walkability simulator, as MCP tools.
/// </summary>
/// <remarks>
/// <para>
/// <c>wz_map_validate</c> checks the map's RULES. These check what a CHARACTER
/// experiences: it walks the foothold chain under the client's own physics and
/// reports where it stops. The two answer different questions, and a map can
/// pass one and fail the other — a chain whose links are all intact still
/// strands a character when the ground runs out partway across.
/// </para>
///
/// <para>
/// <b>Seeded, always.</b> The patrol tool takes a seed and replays exactly.
/// That is the difference between asking a question and watching a demo: an
/// answer that changes every run cannot be acted on.
/// </para>
///
/// <para>
/// Read-only. Nothing here writes, so nothing here takes commit.
/// </para>
/// </remarks>
[McpServerToolType]
public static class SimulatorTools
{
    [McpServerTool(Name = "wz_map_simulate")]
    [Description("Walk a map with a character under the client's physics and report whether it can be crossed. This is NOT the same as wz_map_validate: validate checks the rules, this checks what a character experiences. Returns where it stopped, how far it got, and why. Read-only.")]
    public static async Task<string> Simulate(
        MapClient client,
        [Description("Map address, e.g. f1/Map/Map1/101000200.img - get one from wz_map_list")]
        string path,
        [Description("1 to walk left-to-right, -1 for right-to-left. Both can differ, so try both when one reports blocked.")]
        int direction = 1,
        [Description("Jump every N frames, 0 to never. Use when a gap needs a jump.")]
        int jumpEvery = 0,
        [Description("Drop through the current foothold once, at frame N. 0 to never.")]
        int dropAtStep = 0,
        [Description("Climb: -1 up, 1 down, 0 never. Set this on maps whose chains stop short of a ladder.")]
        int climb = 0,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.PostAsync(
            "/api/simulate/character",
            new { path, direction, jumpEvery, dropAtStep, climb }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_map_simulate_batch")]
    [Description("Simulate many maps at once and list only the ones a character cannot cross. This is the cheap way to find broken maps in a client: run it over a sample, then look at the failures individually. Read-only.")]
    public static async Task<string> SimulateBatch(
        MapClient client,
        [Description("Map addresses. Get them from wz_map_list. 40 is a sensible batch.")]
        string[] paths,
        [Description("1 to walk left-to-right, -1 the other way.")]
        int direction = 1,
        CancellationToken ct = default)
    {
        List<object> blocked = [];
        int crossed = 0, noGeometry = 0;

        foreach (string path in paths)
        {
            try
            {
                JsonNode? result = await client.PostAsync(
                    "/api/simulate/character", new { path, direction }, ct);

                // A link stub has no geometry to walk. Counting it as either a
                // pass or a failure would be a lie.
                if (result?["ticks"]?.GetValue<int>() == 0
                    && result?["footholds"]?.GetValue<int>() == 0)
                {
                    noGeometry++;
                    continue;
                }

                if (result?["fellOut"]?.GetValue<bool>() == true)
                {
                    blocked.Add(new
                    {
                        path,
                        at = $"{result["finalX"]}, {result["finalY"]}",
                        steps = result["steps"]?.GetValue<int>(),
                        reason = result["reason"]?.GetValue<string>(),
                    });
                }
                else crossed++;
            }
            catch
            {
                noGeometry++;
            }
        }

        return JsonSerializer.Serialize(new
        {
            total = paths.Length,
            crossed,
            blockedCount = blocked.Count,
            noGeometry,
            blockedMaps = blocked,
        });
    }

    [McpServerTool(Name = "wz_map_patrol_mob")]
    [Description("Run a mob or NPC patrol and report whether it stays on the map. Seeded and reproducible: the same seed replays exactly. Use when a map's spawns look wrong, or before blaming a map for a mob that disappears. Read-only.")]
    public static async Task<string> PatrolMob(
        MapClient client,
        [Description("Map address")]
        string path,
        [Description("Any integer. The same seed replays the same patrol, which is what makes the answer usable.")]
        int seed,
        [Description("Frames to run. 600 is enough for most maps; 3000 for large ones.")]
        int steps = 600,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync(
            $"/api/simulate/mob?path={MapClient.Q(path)}&seed={seed}&steps={steps}", ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_map_cast")]
    [Description("Get everything needed to draw a map and what stands on it: the character's track, every ladder, every usable portal, and every spawn with whether it has ground under it. Use when a verdict needs a position to look at. Read-only.")]
    public static async Task<string> Cast(
        MapClient client,
        [Description("Map address")]
        string path,
        [Description("Seed for the character's walk")]
        int seed = 1,
        [Description("Frames to walk. 400 is enough to cross most maps.")]
        int steps = 400,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync(
            $"/api/simulate/cast?path={MapClient.Q(path)}&seed={seed}&steps={steps}", ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_map_advise")]
    [Description("Given a point where a walk stopped, say what is wrong and what to change: which foothold ends the chain, whether its link is missing or points at a wall, and how far to extend it. Use after wz_map_simulate reports blocked. Read-only.")]
    public static async Task<string> Advise(
        MapClient client,
        [Description("Map address")]
        string path,
        [Description("X where the character stopped")]
        int x,
        [Description("Y where the character stopped")]
        int y,
        [Description("The direction it was walking: 1 right, -1 left.")]
        int dir = 1,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync(
            $"/api/simulate/advice?path={MapClient.Q(path)}&x={x}&y={y}&dir={dir}", ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_map_ground")]
    [Description("Ask what is under a point on a map: which footholds cover that X and at what height, which are walls, and whether there is ground a character could stand on. Use when a walk stops somewhere unexpected. Read-only.")]
    public static async Task<string> Ground(
        MapClient client,
        [Description("Map address")]
        string path,
        [Description("X in map coordinates")]
        int x,
        [Description("Y in map coordinates")]
        int y,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync(
            $"/api/simulate/probe?path={MapClient.Q(path)}&x={x}&y={y}", ct);
        return JsonSerializer.Serialize(result);
    }
}