using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace MapleMcp;

/// <summary>
/// The LIVE simulator, as MCP tools: one character held across calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not <c>wz_map_simulate*</c> or <c>wz_map_session_*</c>.</b> Those are
/// the MAP EDITOR's physics walker — a batch question with one answer, answered by
/// throwing the body away between calls. This family is the session the viewer's
/// canvas is attached to: the one body that keeps its speed, jump state, ladder
/// contact, HP, buffs and mobs between requests. That persistence is not a
/// convenience, it is the reason this family exists, and it is why it can answer the
/// one question the batch endpoints cannot:
/// <b>"what did the cast actually do to the mob?"</b> Only here does a swing land on
/// a mob that was already standing there, only here can you walk up to a mob, cast,
/// and read the damage number off the frame that resolved it.
/// </para>
///
/// <para>
/// The physics is the same <c>CharacterPhysics</c> either way. What differs is that
/// the state is held, so a claim like "this skill does 40% of max HP" is a
/// measurement rather than a plan.
/// </para>
///
/// <para>
/// <b>No tool here takes <c>commit</c>, and that is deliberate.</b> The other
/// families take it because they write to an archive, which is a durable and shared
/// change. A session is in-memory, keyed by a server-assigned id, and discarded when
/// <c>wz_sim_close</c> deletes it (an untouched one is swept on its own after two
/// idle minutes). Nothing here survives the process, nothing here is visible to
/// anyone but the holder of the id, and nothing here marks an archive dirty. A
/// <c>commit</c> gate on these tools would ask permission for a write that does not
/// exist, and would train an agent to click through a gate that never protects
/// anything. The mutating tools below therefore say plainly that they write — to the
/// SESSION, never to the archive — so the one thing an agent does have to be careful
/// about is not hidden behind the word "read-only".
/// </para>
///
/// <para>
/// Read the frame, do not infer it. Every mutating tool answers with the whole
/// session frame, and the frame carries what the step cost: HP, mobs, swing log,
/// ticks requested, ticks actually run, and whether the server clamped the batch.
/// Those three tick fields exist because a clamped batch is otherwise
/// indistinguishable from a stall, and "the mob never moved" was once a measurement
/// taken on a request the server silently cut from 600 ticks to 60.
/// </para>
/// </remarks>
[McpServerToolType]
public static class LiveSimTools
{
    [McpServerTool(Name = "wz_sim_open")]
    [Description("Put a live character on a map and keep it alive across later calls: the sessionId it returns is what every other wz_sim_* tool takes. Answers where the character is standing, what is under it, and what is already on the map. Does NOT answer whether a map can be walked end to end (wz_map_simulate does, and throws the body away) and does NOT touch the archive. Creates a session — call wz_sim_close when done.")]
    public static async Task<string> Open(
        MapClient client,
        [Description("Map address, e.g. f1/Map/Map1/101000200.img - get one from wz_sim_catalog with kind maps, or from wz_map_list. THE LEADING fN IS NOT STABLE: it is the archive's session id, handed out in the order archives were opened, so a server that restarted or had archives closed and reopened numbers them differently (measured 2026-10-17: the same Map.nx was f4 in one run and f25 in the next). A stale id fails with 'No open file with id fN', which reads like the map is missing and is not. Call wz_asset_files or wz_sim_catalog with kind maps immediately before, and use the id it returns.")]
        string path,
        [Description("X to start at. Leave unset to use the map's own spawn portal.")]
        int? startX = null,
        [Description("Y to start at. Leave unset with startX to use the spawn portal.")]
        int? startY = null,
        [Description("Facing: 1 right, -1 left. Facing only; the character does not walk until you step it.")]
        int direction = 1,
        [Description("Weapon IMAGE name the character swings, e.g. 1302000.img. Omit and every swing resolves to an EMPTY attack box, so the session can walk and jump and can never deal damage - which is worse than obviously dealing none.")]
        string? weapon = null,
        [Description("The equipped weapon's own WZ data id, e.g. 01302000. Its info/attack integer selects the swing STANCE group, so the figure poses correctly while swinging. Omit and the character is unarmed: bare-hands hit box, no swing pose, matching the reference.")]
        string? equippedWeapon = null,
        [Description("Take no contact damage. A debug switch for watching a mob's attack animation to its end; contact damage has no timer, so an unflagged level-1 character dies on first touch. The damage is still reported, only the HP spend is suppressed.")]
        bool invulnerable = false,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.PostAsync("/api/simulate/session", new
        {
            path,
            startX,
            startY,
            direction,
            weapon,
            equippedWeapon,
            invulnerable,
        }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_step")]
    [Description("Advance a live character by N frames (default 1, max 60) under held keys, and return the frame plus every swing that landed in between. Answers 'what did this input do' - movement, landing, a mob's reaction, the damage number. Does NOT answer where the character is WITHOUT stepping (wz_sim_state does). WRITES to the session: the body keeps moving between calls and HP keeps falling. Check ticksRun and clamped: the server caps a batch at 60 and says so rather than pretending it ran.")]
    public static async Task<string> Step(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        [Description("Frames to run, 1-60. 1 is one 1/60s tick. More frames for one round trip; the LAST frame comes back plus every swing in between.")]
        int count = 1,
        [Description("Hold left for the batch")]
        bool left = false,
        [Description("Hold right for the batch")]
        bool right = false,
        [Description("Hold up (climb ladders up; the reference's up key is both up and jump-adjacent)")]
        bool up = false,
        [Description("Hold down (climb down / drop through a one-way foothold)")]
        bool down = false,
        [Description("Jump. True for the first frame of the batch only, then released, exactly as a held key behaves.")]
        bool jump = false,
        [Description("Attack. True for the first frame only; a swing is an edge, not a level.")]
        bool attack = false,
        [Description("Skill to cast on the first frame, e.g. 1121008. Null means no cast this batch. Omit the key entirely rather than sending an empty id.")]
        string? skillId = null,
        CancellationToken ct = default)
    {
        // The server binds MapSimBatchRequest { Count, First, Then }: First drives
        // step 0, and Then drives the rest ONLY while a direction key is held
        // (`Then.AnyHeld()`), otherwise First is repeated for every step.
        //
        // AnyHeld counts left/right/up/down and deliberately NOT jump, attack or
        // skillId, because a cast is not a direction. Two consequences worth knowing
        // before reading a batch of frames, both the server's behaviour rather than a
        // choice made here:
        //
        //   - a 12-frame cast batch does NOT cast 12 times;
        //   - a jump with no direction key held is repeated for the WHOLE batch,
        //     because the server falls back to First. Send count=1 if you want one
        //     jump and not twelve.
        //
        // The edges live on First alone: a held jump or attack is released after the
        // first step, which is what a key does.
        JsonObject first = new()
        {
            ["left"] = left,
            ["right"] = right,
            ["up"] = up,
            ["down"] = down,
            ["jump"] = jump,
            ["attack"] = attack,
        };
        // Added only when asked for. An explicit null would bind to the same null,
        // but omitting it means "no cast this batch" is never a cast of something.
        if (skillId is not null) first["skillId"] = skillId;

        // No skillId here at all: the cast is a single edge, never a held level.
        JsonObject then = new()
        {
            ["left"] = left,
            ["right"] = right,
            ["up"] = up,
            ["down"] = down,
            ["jump"] = false,
            ["attack"] = false,
        };

        JsonNode? result = await client.PostAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}/frames",
            new { count, first, then }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_cast")]
    [Description("One frame with a skill cast requested - the minimum call that answers 'did that cast do anything'. Returns the frame: the cast's own effect, the damage it dealt, and the mob's reaction. Does NOT plan the cast before you spend it (wz_sim_catalog kind='skillplan' does, free) and does NOT return the tick-by-tick TIMELINE, which the skill-cast plan endpoint has. WRITES to the session: one frame of simulation time passes and the body may be moved by the cast's own motion.")]
    public static async Task<string> Cast(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        [Description("Skill id as the archive names it, e.g. 1121008 - not 1121008.img. See wz_sim_catalog kind='skills' for what is castable.")]
        string skillId,
        CancellationToken ct = default)
    {
        // The same /frames route with count=1 and the cast on the first frame.
        // Deliberately a distinct tool rather than a flag on wz_sim_step: the cast is
        // the question most of this family is asked, and an agent reaching for it
        // should not have to remember that a cast lives on the first key of a batch.
        JsonNode? result = await client.PostAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}/frames",
            new
            {
                count = 1,
                first = new { left = false, right = false, up = false, down = false, jump = false, attack = false, skillId },
                then = new { left = false, right = false, up = false, down = false, jump = false, attack = false },
            }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_state")]
    [Description("Read a live session WITHOUT stepping it: where the body is, its HP and max HP, the mobs present, the skill bindings, and what it is standing on. Answers 'where is it now' and 'is it dead yet'. Does NOT move anything, does NOT tick the clock, and does NOT tell you what a cast would do. Use it between steps to separate 'the cast did nothing' from 'the cast was never sent'.")]
    public static async Task<string> State(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.GetAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}", ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_summon")]
    [Description("Spawn N mobs into a live session, at a random x within 100px of the character, dropped where that x falls outside the map's walkable extent. Answers 'what does this mob do to a character standing here' and gives you a target to attack. Does NOT invent a mob: an id the archive does not hold comes back in the REJECTED list rather than silently not spawning, because the reference itself fails silently here. WRITES to the session. kind 'm' is mob, 'n' is NPC; omit to let the archive decide.")]
    public static async Task<string> Summon(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        [Description("Mob id as the Mob archive names it: 100100, NOT 100100.img and NOT Mob/100100.img - the archive stores it as 0100100.img and the reference concatenates 'Mob/' + id + '.img'. Get real ids from wz_sim_catalog kind='mobs'.")]
        string mobId,
        [Description("How many to spawn. The server clamps to 1-64.")]
        int count = 1,
        [Description("'m' for mob, 'n' for NPC. Omit to let the archive decide.")]
        string? kind = null,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.PostAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}/summon",
            new { mobId, count, kind }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_despawn")]
    [Description("Remove every mob this session summoned, and report how many went. Answers 'get the target out of the way' - use it between two casts at the same skill so the second one starts from a clean field. Does NOT remove the character's own HP or bindings, and does NOT affect mobs that came from the map's own spawns. WRITES to the session; the count comes back so you can tell a removal from a miss.")]
    public static async Task<string> Despawn(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.PostAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}/despawn", null, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_skills")]
    [Description("Rebuild a live session's hotkey grid as a WHOLE-LIST replacement, or clear it. Answers 'which keys does this session have bound' and is the prerequisite for casting by key. Does NOT patch: anything you omit is gone, which is the reference's own behaviour (it rebuilds the whole dictionary on every change), and rows that lose a skill/key collision come back in REJECTED rather than being dropped in silence. WRITES to the session. The key is a SINGLE LETTER A-Z, nothing else.")]
    public static async Task<string> Skills(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        [Description("Bindings as 'skillId:key' pairs, e.g. [\"1121008:K\", \"1121000:L\"]. The KEY IS A SINGLE LETTER A-Z, because the reference hardcodes \"A\"..\"Z\" into the combo that supplies it; anything else is refused by name. Split on '=' or ':' - both are accepted. The skill id is the archive name, e.g. 1121008, not 1121008.img. Sending an empty list rebuilds an EMPTY grid, which is the same as clear=true; ignored when clear=true.")]
        string[]? bindings = null,
        [Description("Clear the whole grid instead of setting it, answering with the number of rows removed.")]
        bool clear = false,
        CancellationToken ct = default)
    {
        if (clear)
        {
            JsonNode? cleared = await client.PostAsync(
                $"/api/simulate/session/{MapClient.Q(sessionId)}/skills/clear", null, ct);
            return JsonSerializer.Serialize(cleared);
        }

        // MapSimSkillsRequest.Bindings is a List<MapSimSkillBinding> whose rows are
        // { SkillId, Key } -- so the wire property names are skillId and key, NOT
        // skillid/keys. Rows are projected here because the tool takes flat strings.
        // An omitted list is an empty grid, which is the same whole-list replacement
        // the clear route performs; the two are not distinguished on purpose.
        List<object> rows = [];
        foreach (string pair in bindings ?? [])
        {
            // First separator wins, and only the first: a skill id never contains
            // ':' or '=', so splitting at index+1 keeps a key containing one intact
            // rather than silently dropping the tail.
            int at = pair.IndexOfAny([':', '=']);
            if (at <= 0)
                throw new ModelContextProtocol.McpException(
                    $"binding '{pair}' is not a skillId:key pair, e.g. 1121008:K.");
            rows.Add(new
            {
                skillId = pair[..at].Trim(),
                key = pair[(at + 1)..].Trim(),
            });
        }

        JsonNode? result = await client.PostAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}/skills",
            new { bindings = rows }, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_equip")]
    [Description("Change what a live character is holding. weaponId swaps the equipped WEAPON (its data id, e.g. 01302000 - this is what changes the swing box and the swing stances); slot + itemId replaces the whole equipped SET with exactly that one slot. Answers 'does this weapon change the swing' and 'does this item change my sheet'. Does NOT add to what is worn: /equip is a REPLACEMENT, so a set you send is the WHOLE set and anything you leave out is off the character - that is what makes 'removed' distinguishable from 'never equipped'. WRITES to the session. Passing no slot and an empty weaponId DISARMS, which is a real state, not a default.")]
    public static async Task<string> Equip(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        [Description("Equipped weapon's own WZ data id, e.g. 01302000 - NOT the 1302000.img swing-box name, which is a wz_sim_open parameter and cannot be changed here. Omit to leave the weapon alone; pass an empty string to disarm.")]
        string? weaponId = null,
        [Description("Equip slot for the whole-set replacement, e.g. Coat, Cap-1, Weapon-1. Get the full list from wz_sim_catalog with kind lookslots. Only used with clear=true, because the set route is a replacement and a one-slot call would otherwise strip everything else.")]
        string? slot = null,
        [Description("Item image name to put in that slot, e.g. 01000000, or empty to leave that slot empty. Only used with clear=true.")]
        string? itemId = null,
        [Description("Send the whole-set replacement instead of the weapon route. The mapping given (slot -> itemId) becomes the character's ENTIRE equipped set; every other slot is emptied. Required for slot/itemId to mean anything.")]
        bool clear = false,
        CancellationToken ct = default)
    {
        // Two routes, two different request DTOs, both read rather than assumed:
        //
        //   POST /session/{id}/weapon  MapSimWeaponRequest { EquippedWeapon }  (string?)
        //   POST /session/{id}/equip   MapSimEquipRequest  { Items }          (Dictionary<string,string>?)
        //
        // They are NOT two spellings of one thing. /weapon names the equipped
        // weapon's own data, whose info/attack integer selects the stance group, and
        // answers whether that id resolves. /equip is the SHEET: the reference builds
        // it as base + every equipped item's inc* (Player.cpp:309-313), so a stale
        // entry here is a wrong number rather than a wrong picture -- which is why
        // the set route replaces and never patches.
        //
        // The two never overlap here: `weaponId` is the only thing that can reach
        // /weapon, and `clear` is the only thing that can reach /equip. Leaving both
        // unset is a caller mistake, and saying so beats disarming a character
        // because a slot was left blank.
        if (weaponId is null && !clear)
            throw new ModelContextProtocol.McpException(
                "pass weaponId to change the weapon, or clear=true with slot/itemId to replace the equipped set.");

        if (weaponId is not null)
        {
            // A blank equippedWeapon DISARMS rather than defaulting back to what the
            // session started with (MapSimWeaponRequest's own remarks).
            JsonNode? weapon = await client.PostAsync(
                $"/api/simulate/session/{MapClient.Q(sessionId)}/weapon",
                new { equippedWeapon = weaponId }, ct);
            return JsonSerializer.Serialize(weapon);
        }

        // The whole-set replacement. An omitted slot is refused rather than guessed:
        // sending {} would strip every item and call it a result.
        if (string.IsNullOrWhiteSpace(slot))
            throw new ModelContextProtocol.McpException(
                "clear=true replaces the WHOLE set, so name the slot: slot='Coat', itemId='01000000'.");

        Dictionary<string, string> items = new(StringComparer.OrdinalIgnoreCase)
        {
            [slot] = itemId ?? "",
        };

        JsonNode? replaced = await client.PostAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}/equip",
            new { items }, ct);
        return JsonSerializer.Serialize(replaced);
    }

    [McpServerTool(Name = "wz_sim_control")]
    [Description("Flip a live session's switches: action 'invulnerable' takes damage or stops taking it (value true/false), 'revive' brings a dead character back at the map's spawn portal, 'input' holds the current keys so the body walks between polls instead of per request. Answers 'let me watch this to the end without dying' and 'make it keep walking'. Does NOT step frames - /input holds keys and lets the server-side clock advance the body; use wz_sim_step when you want each tick back. WRITES to the session. An unknown action is refused by name rather than guessed.")]
    public static async Task<string> Control(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        [Description("'invulnerable' (flip take-damage, using value), 'revive' (bring a dead character back at the spawn portal; takes no body), or 'input' (hold keys - pass them as left/right/up/down, e.g. right=true to keep walking). Anything else is refused by name rather than guessed.")]
        string action,
        [Description("For 'invulnerable': true to stop taking damage, false to start. Ignored by 'revive'.")]
        bool value = true,
        [Description("For 'input': hold left while the server-side clock advances the body. Ignored by 'invulnerable' and 'revive'.")]
        bool left = false,
        [Description("For 'input': hold right.")]
        bool right = false,
        [Description("For 'input': hold up (climb up).")]
        bool up = false,
        [Description("For 'input': hold down (climb down / drop through).")]
        bool down = false,
        CancellationToken ct = default)
    {
        string route = $"/api/simulate/session/{MapClient.Q(sessionId)}/";

        return action switch
        {
            // MapSimInvulnerableRequest is one property, named `value`, and it
            // answers with the session frame so a caller reads the HP the same way
            // whether the flip took or not. The damage is still reported when
            // invulnerable; only the HP spend stops.
            "invulnerable" => JsonSerializer.Serialize(
                await client.PostAsync($"{route}invulnerable", new { value }, ct)),

            // POST /revive takes NO body at all - it is (id, store). Posting null
            // keeps the wire identical to what the handler binds.
            "revive" => JsonSerializer.Serialize(
                await client.PostAsync($"{route}revive", null, ct)),

            // The other half of the clock: Hold() stores the DIRECTION keys and
            // returns whether the session exists; the answer is then the current
            // frame. Note the handler binds left/right/up/down ONLY - jump, attack
            // and skillId are NOT held, so an edge like a cast still goes through
            // /frames, and holding all four false is how you stop the body walking.
            "input" => JsonSerializer.Serialize(
                await client.PostAsync($"{route}input", new { left, right, up, down }, ct)),

            _ => throw new ModelContextProtocol.McpException(
                $"unknown action '{action}'. Use 'invulnerable', 'revive' or 'input'."),
        };
    }

    [McpServerTool(Name = "wz_sim_catalog")]
    [Description("Read-only discovery for planning a cast or a session: the maps that can be simulated, the mobs this archive can summon, the skills it can actually cast, the stat block of one item, the equip slots, the swing stances a weapon may use, or the whole tick-by-tick timeline of one skill's cast. Answers 'what CAN I do here' before you spend a session on it. Does NOT write and does NOT touch a session. An unknown kind is refused by name with the valid list rather than guessed into a route.")]
    public static async Task<string> Catalog(
        MapClient client,
        [Description("One of: maps (q + limit), mapcount, mobs, skills, skillplan (skillId), lookslots, stances, attackstances (weapon), equipstats (itemId), mobinfo (mobId), mobattacks (mobId). Nothing else is accepted - the route is chosen from a closed set, never guessed.")]
        string kind,
        [Description("For kind='maps': name or id substring, e.g. \"蘑菇村\" or \"10000\".")]
        string? q = null,
        [Description("For kind='maps': how many map rows. Server default 200, clamped to 20000.")]
        int? limit = null,
        [Description("For kind='skillplan' the skill id, e.g. 1121008; for kind='equipstats' an item image id, e.g. 01000000; for kind='mobinfo'/'mobattacks' a mob id, e.g. 100100.")]
        string? id = null,
        [Description("For kind='attackstances': the weapon's data id, e.g. 01302000, to get its swing stance group.")]
        string? weapon = null,
        CancellationToken ct = default)
    {
        // Every branch is a route that was read out of the endpoint source, and the
        // set is closed on purpose: an unrecognised kind must fail loudly, because a
        // guessed route on this server answers 200 with a shape nobody expected.
        string route = kind switch
        {
            "maps" => "/api/simulate/maps"
                + (q is null ? "" : $"?q={MapClient.Q(q)}")
                + (limit is null ? "" : $"{(q is null ? "?" : "&")}limit={limit}"),
            "mapcount" => "/api/simulate/maps/count",
            "mobs" => "/api/simulate/mob-catalog",
            "skills" => "/api/skill-cast/castable",
            "skillplan" => $"/api/skill-cast/plan?skillId={MapClient.Q(id ?? throw new ModelContextProtocol.McpException("kind='skillplan' needs a skillId."))}",
            "lookslots" => "/api/simulate/look/slots",
            "stances" => "/api/simulate/look/stances",
            // Both params are optional on the route, so a bare request is valid and
            // asks for nothing in particular; `attack` (the raw stance integer) is
            // bound server-side but has no tool argument, so a caller that knows the
            // integer cannot pass it here.
            "attackstances" => "/api/simulate/look/attack-stances"
                + (weapon is null ? "" : $"?weapon={MapClient.Q(weapon)}"),
            "equipstats" => $"/api/simulate/equip-stats?itemId={MapClient.Q(id ?? throw new ModelContextProtocol.McpException("kind='equipstats' needs an itemId."))}",
            "mobinfo" => $"/api/simulate/mob-info?mobId={MapClient.Q(id ?? throw new ModelContextProtocol.McpException("kind='mobinfo' needs a mobId."))}",
            "mobattacks" => $"/api/simulate/mob-attacks?mobId={MapClient.Q(id ?? throw new ModelContextProtocol.McpException("kind='mobattacks' needs a mobId."))}",
            _ => throw new ModelContextProtocol.McpException(
                $"unknown catalog kind '{kind}'. Valid kinds: maps, mapcount, mobs, skills, " +
                "skillplan, lookslots, stances, attackstances, equipstats, mobinfo, mobattacks."),
        };

        JsonNode? result = await client.GetAsync(route, ct);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "wz_sim_probe")]
    [Description("Ask what is under a point on a map: which footholds cover that X and at what height, which are walls, and whether there is ground a character could stand on. Answers 'why is the character standing THERE' and 'why did it not fall'. Read-only, and it needs no session - the point does not have to be the live character's. Does NOT say whether the map is walkable end to end, and does NOT step anything. Use it when a walk stops somewhere that the frame alone does not explain.")]
    public static async Task<string> Probe(
        MapClient client,
        [Description("Map address, e.g. f1/Map/Map1/101000200.img")]
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

    [McpServerTool(Name = "wz_sim_close")]
    [Description("Discard a live session and free the memory it holds, reporting whether it was there. Answers 'I am done with this character'. Does NOT save anything: a session is in-memory and throws itself away here, which is why nothing in this family takes a commit flag. Call it when you are finished - a forgotten session is swept on its own after two idle minutes, but it holds a map reference until then.")]
    public static async Task<string> Close(
        MapClient client,
        [Description("Session id from wz_sim_open")]
        string sessionId,
        CancellationToken ct = default)
    {
        JsonNode? result = await client.DeleteAsync(
            $"/api/simulate/session/{MapClient.Q(sessionId)}", ct);
        return JsonSerializer.Serialize(result);
    }
}
