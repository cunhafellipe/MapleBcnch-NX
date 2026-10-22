// The live character, over MCP.
//
// WHY THIS EXISTS
// ---------------
// wz_map_simulate answers "if a character walks this map unattended, where
// does it end up" -- a batch question with one answer. It is the right tool
// for sweeping a client and listing the maps that break, and the wrong tool
// for the question a person actually has while looking at a map, which is
// "let me try walking it".
//
// The endpoint underneath is the same physics, held live between calls
// (POST /api/simulate/session). What is exposed here is deliberately a
// session plus a step, rather than a convenience "walk N frames holding X":
// the point is that the caller controls each frame, so a caller can hold a
// key, release it, reverse, and jump -- the things a person does -- and get
// the same physics the client has at each one.
//
// Stepping one frame per call also keeps the cost honest. A 600-frame sweep is
// 600 round trips here, which is slow on purpose: it means the caller is
// deciding what happens, not asking the server to run something and trusting
// the result. For a sweep, use wz_map_simulate_batch.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// Live-character tools: place a body on a map and drive it a frame at a time.
/// </summary>
[McpServerToolType]
public static class SessionTools
{
    [McpServerTool(Name = "wz_map_session_open")]
    [Description("Put a live character on a map so it can be walked frame by frame with wz_map_session_step. Use this when you want to try a route yourself -- hold a direction, release it, reverse, jump -- rather than ask for a fixed walk. For a one-shot 'can this map be crossed', use wz_map_simulate instead. Read-only: nothing is saved or marked dirty.")]
    public static async Task<string> Open(
        MapClient client,
        [Description("Map address, e.g. f1/Map/Map1/101000200.img - get one from wz_map_list")]
        string path,
        [Description("Starting X, or omit to start at the map's left edge")]
        int? startX = null,
        [Description("Starting Y, or omit to land on the topmost surface")]
        int? startY = null,
        [Description("1 to face right, -1 to face left")]
        int direction = 1,
        [Description("Weapon the character swings, e.g. 1342000.img. Omit for bare hands. Without a weapon the backend resolves no attack box and every swing is a no-op, so a session that is meant to fight must pass one.")]
        string? weapon = null,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.PostAsync(
            "/api/simulate/session",
            new { path, startX, startY, direction, weapon }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_map_session_step")]
    [Description("Advance a live character one frame and return where it ended up. Call this repeatedly while holding a direction: each call is one frame at the client's 60Hz. The body keeps its speed, jump state and ladder contact between calls, which is what makes 'hold right and keep walking' work. Pass attack=true to swing; the hit lands on the mob whose animation rectangle overlaps your attack rectangle, and the frame comes back with a damage entry carrying the number, its x/y and whether it missed. Read-only.")]
    public static async Task<string> Step(
        MapClient client,
        [Description("Session id from wz_map_session_open")]
        string sessionId,
        [Description("Hold left")]
        bool left = false,
        [Description("Hold right")]
        bool right = false,
        [Description("Hold up: climbs up a ladder, or takes a portal")]
        bool up = false,
        [Description("Hold down: climbs down, or drops through a platform")]
        bool down = false,
        [Description("Jump this frame. Edge-triggered -- pass true once, not every frame, or the character hops continuously instead of jumping once")]
        bool jump = false,
        [Description("Swing this frame. Edge-triggered like jump: true for the one frame the key goes down. Returns damage entries (mob id, value, x, y, miss) and the target's hp drops by the rolled value; a mob at 0 hp plays die1 for 45 frames before it reads dead.")]
        bool attack = false,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.PostAsync(
            $"/api/simulate/session/{sessionId}/frame",
            new { left, right, up, down, jump, attack }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_map_session_close")]
    [Description("Discard a live character and free the memory it holds. Do this when you are done with a session; a forgotten one is dropped on its own after two idle minutes.")]
    public static async Task<string> Close(
        MapClient client,
        [Description("Session id from wz_map_session_open")]
        string sessionId,
        CancellationToken ct = default)
    {
        // DELETE, not POST: the endpoint is registered as
        // MapDelete("/session/{id}"). Calling it as a POST reached the route
        // matcher and came back 404 with no reason, which read like a missing
        // endpoint rather than a wrong verb.
        JsonNode? result = await client.DeleteAsync($"/api/simulate/session/{sessionId}", ct);
        return JsonSerializer.Serialize(result);
    }
}