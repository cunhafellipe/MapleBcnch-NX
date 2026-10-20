using System.Globalization;
using MapleBench.Models;
using MapleBench.Services;
using MapleBench.Services.Look;
using MapleBench.Services.MapEditor;
using MapleBench.Services.Simulate;

namespace MapleBench.Api;

/// <summary>
/// The simulator's HTTP surface — a SEPARATE surface from the map editor.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not <c>/api/mapedit/simulate</c>. The map editor is
/// where a person edits a map: it opens documents, tracks dirty state, and owns
/// undo. None of that belongs to a read-only question about walkability, and
/// mounting it under the editor would make the simulator depend on having a map
/// open in an editing session — which is exactly the thing an agent verifying a
/// freshly generated map does not have.
/// </para>
/// <para>
/// The simulator answers about any map the BACKEND can see, and never mutates
/// one. It borrows the map model, not the editing session.
/// </para>
/// </remarks>
public static class SimulatorEndpoints
{
    public static void MapSimulator(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/simulate");

        // Diagnostics for the swing chain. The hit test compares four values
        // that no surface exposed, so a swing that missed for any of four
        // different reasons looked identical from outside: no box, no mob
        // geometry, no overlap, or no draw. These answer each one by name.
        // They read only; they step nothing.
        //
        // `sessionId` is not optional sugar. `Any()` returns the FIRST session in
        // the dictionary, so a server that still holds a session nobody steps --
        // one a probe leaked and never deleted -- answers with THAT session's log,
        // which is empty because it has never run a frame. MEASURED: that is why
        // a mob standing two pixels from the player with `aggro` set reported no
        // swing-log lines at all. Naming the session is the difference between a
        // diagnostic that answers and one that only ever says "nothing".
        group.MapGet("/debug/mob-swing", (MapSimSessionStore sessions, string? sessionId) =>
        {
            MapSimSession? live = sessionId is null ? sessions.Any() : sessions.Find(sessionId);
            return Results.Ok(new
            {
                mobs = live?.MobSwingLog.ToArray(),
                hp = live?.Status.Hp,
                maxHp = live?.Status.MaxHp,
                alive = live is not null,
                sessionId = live?.Id,
                // The scheduled swing, because "the hit never landed" has three
                // different causes that look identical from the frame: the box was
                // empty, the delay never ran out, or the overlap missed. Reading
                // them off the frame is guessing; reading them off the session is
                // not.
                swingPending = live?.SwingHitPending ?? false,
                swingDelayTicks = live?.PendingSwingHitTicks ?? 0,
                swingBoxEmpty = live?.PendingSwingBox is null or { IsEmpty: true },
                lastPoseMs = live?.LastSwingPoseMs ?? 0,
                lastSwingBox = live?.LastSwingBox is { } sb
                    ? new { sb.Left, sb.Top, sb.Right, sb.Bottom }
                    : null,
                lastSwingMiss = live?.LastSwingMiss ?? "",
                // Which mob attacks this session has resolved, so the list the
                // archive actually yields is readable instead of only countable.
                mobAttacks = live is null
                    ? Array.Empty<object>()
                    : live.MobAttacks.Select(kv => new
                    {
                        mobId = kv.Key,
                        attacks = kv.Value.Select(a => new
                        {
                            a.Index, a.Action, a.Type, a.Power, a.Magic, a.ConMp,
                            a.DelayMs, a.Inactive, a.DurationMs, a.Radius, a.AreaCount,
                            range = new { a.Range.Left, a.Range.Top, a.Range.Right, a.Range.Bottom },
                        }),
                    }).ToArray(),
                // Every mob's CURRENT animation box. "Why did the swing miss" and
                // "why did the mob not touch me" are both a question about these
                // rectangles, and they are per-FRAME (a mob's box changes with its
                // pose), so a number captured at swing time is stale by the time the
                // hit resolves -- which is one measured way this went wrong.
                mobBoxes = live is null
                    ? Array.Empty<object>()
                    : sessions.MobFrames(live).ToArray(),
                // What the character is standing on, and the nearest ladder.
                // A fall "through a wall" and a climb "that will not start" are
                // both answered by these two lines and neither is visible on the
                // frame.
                footholdAt = live is null ? null : new
                {
                    id = live.Body.StandingOn?.Id ?? "",
                    layer = live.Body.StandingOn?.Layer ?? "",
                    isWall = live.Body.StandingOn?.IsWall ?? false,
                    surfaceY = live.Body.StandingOn is { } fh ? fh.SurfaceAt((int)Math.Round(live.Body.X)) : null,
                    onGround = live.Body.OnGround,
                    rising = live.Body.Rising,
                    onLadder = live.Body.OnLadder is { } l
                        ? new { l.X, l.Top, l.Bottom, l.UpperFoothold }
                        : null,
                    nearestLadder = sessions.NearestLadder(live),
                },
                // The client's own decode report -- see MapSimSession.ClientArt.
                clientArt = live is null
                    ? null
                    : new
                    {
                        live.ClientArt.Attempted,
                        live.ClientArt.Missing,
                        live.ClientArt.MissingSample,
                        live.ClientArt.ReportedAtMs,
                    },
            });
        });

        // What the browser managed to draw. The client POSTs this after it loads a
        // map; it is a REPORT, not a server-side measurement, and it is the only
        // place the browser's own decode failures are visible.
        group.MapPost("/debug/artistats", (MapSimSessionStore sessions, string? sessionId,
            ClientArtReport report) =>
        {
            MapSimSession? live = sessionId is null ? sessions.Any() : sessions.Find(sessionId);
            if (live is null)
                return Results.NotFound(new { error = "no such session" });
            live.ClientArt.Attempted = report.Attempted;
            live.ClientArt.Missing = report.Missing;
            live.ClientArt.MissingSample.Clear();
            foreach (string s in report.MissingSample ?? Array.Empty<string>())
                if (live.ClientArt.MissingSample.Count < 20)
                    live.ClientArt.MissingSample.Add(s);
            live.ClientArt.ReportedAtMs = Environment.TickCount64;
            return Results.Ok(new { ok = true, missing = live.ClientArt.Missing });
        });

        group.MapGet("/debug/portal", (MapSimSessionStore sessions) =>
            Results.Ok(new { failure = sessions.LastPortalFailure }));

        group.MapGet("/debug/attack-box", (string? weapon, CombatService combat) =>
        {
            AttackBox box = combat.AttackBox(weapon);
            return Results.Ok(new
            {
                box.Source,
                box.Path,
                box.Resolved,
                box.Box.Left,
                box.Box.Top,
                box.Box.Right,
                box.Box.Bottom,
                box.Box.IsEmpty,
            });
        });

        group.MapGet("/debug/mob-frame", (string mobId, string action, int frame,
                                          string? kind, CombatService combat) =>
        {
            MobFrameGeometry? g = combat.MobFrame(mobId, action, frame, kind ?? "m");
            return g is null
                ? Results.Ok(new { found = false })
                : Results.Ok(new
                {
                    found = true, g.Left, g.Top, g.Right, g.Bottom,
                    g.HeadX, g.HeadY, g.ActionHeadX, g.ActionHeadY,
                });
        });


        group.MapGet("/debug/mob-hp", (string mobId, CombatService combat) =>
            Results.Ok(new { hp = combat.MobMaxHp(mobId), level = combat.MobLevel(mobId) }));

        // What the simulator can do with what is currently open. The frontend
        // uses this to explain itself when no map archive is loaded, rather than
        // offering a picker that cannot produce a result.
        group.MapGet("/capabilities", (MapEditorService maps) =>
            Results.Ok(maps.SimulatorCapabilities()));

        // List the maps that can be simulated. Same shape as the editor's list,
        // but it does NOT require an editing session to be open for them.
        group.MapGet("/maps", (string? q, int? limit, MapEditorService maps) =>
            // Clamped high on purpose. The picker keeps every map it is given
            // and the search box filters that array in the browser, so a cap
            // here silently hides maps from the screen: at 2,000 the 3,366
            // beyond it could not be picked even by exact id, and nothing in
            // the UI said so. A caller that wants only a page passes a small
            // number; one that wants them all passes a large one.
            Results.Ok(maps.ListMaps(q, Math.Clamp(limit ?? 200, 1, 20000))));

        // How many maps there are, for a caller that wants to cover the whole
        // client rather than the first page. A batch sweep needs all of them.
        group.MapGet("/maps/count", (MapEditorService maps) =>
            Results.Ok(new { total = maps.Capabilities().MapCount }));

        // Walk a map. Read-only: it inspects and reports, and there is no
        // commit parameter anywhere in this group because nothing here writes.
        group.MapPost("/walk", (MapSimulateRequest request, MapEditorService maps) =>
            Results.Ok(maps.Simulate(request.Path, request.StartX, request.StartY)));

        // Walk an actual CHARACTER, under the client's own physics. This is the
        // one that can catch a map the link chain calls intact but that a
        // character physically cannot cross.
        group.MapPost("/character", (MapSimulateRequest request, MapEditorService maps) =>
            Results.Ok(maps.WalkCharacter(request.Path, request.StartX, request.StartY,
                request.Direction, request.JumpEvery, request.DropAtStep, request.Climb)));

        // Turn "the character stopped" into "extend foothold 12 by 80px". A fact an
        // agent cannot act on is a report, not a result.
        group.MapGet("/advice", (string path, int x, int y, int dir,
            MapEditorService maps) =>
            Results.Ok(maps.Advise(path, x, y, dir)));

        // Everything needed to draw a map and what stands on it. The simulator
        // answers WHETHER a map works; this answers where to LOOK at it.
        group.MapGet("/cast", (string path, int? spawnX, int? spawnY, int seed, int steps,
            MapEditorService maps) =>
            Results.Ok(maps.CastMap(path, spawnX, spawnY, seed, steps)));

        // Mobs and NPCs patrol their own foothold chain. Seeded, so a map's answer
        // is the same twice — MapleNecrocer's are not, which is the whole reason
        // this endpoint exists rather than a recommendation to watch them.
        group.MapGet("/mob", (string path, int? spawnX, int? spawnY, int seed, int steps,
            MapEditorService maps) =>
            Results.Ok(maps.PatrolMob(path, spawnX, spawnY, seed, steps)));

        // A single step, with everything it touched. The walk's own output says
        // WHERE the character ended up, never WHY, and "ended where it started
        // after 18,000 steps" is not a diagnosis.
        group.MapGet("/step", (string path, int x, int y, int dir, int climb,
            MapEditorService maps) =>
            Results.Ok(maps.StepOnce(path, x, y, dir, climb)));

        // Walk a bounded number of steps and report the DECISION at each one.
        // /step rebuilds the character per call and so cannot show a sequence;
        // this keeps one character and returns what Walk() decided each tick.
        group.MapGet("/trace", (string path, int x, int y, int dir, int climb, int steps,
            int jumpEvery, int dropAtStep, MapEditorService maps) =>
            Results.Ok(maps.TraceWalk(path, x, y, dir, climb, steps, jumpEvery, dropAtStep)));

        // The ground a walk happens on, as plain geometry. Without it a verdict is a
// sentence with nothing behind it, and a walk that never happened looks exactly
// like one that did -- which is how four bugs in a row passed every check.
        group.MapGet("/geometry", (string path, MapEditorService maps) =>
            Results.Ok(maps.MapGeometry(path)));

        // Why is the character standing where it is? A caller that distrusts the
        // walk's Y needs to see which footholds exist under that X, and this is
        // the only way to ask without opening the map in the editor.
        group.MapGet("/probe", (string path, int x, int y, MapEditorService maps) =>
            Results.Ok(maps.ProbeGround(path, x, y)));
        // A LIVE character, as opposed to the batch endpoints above.
        //
        // Everything else here answers in one shot: give a path, get a verdict.
        // That cannot answer "let me try walking it", because the body is
        // thrown away between questions -- there is nothing holding a speed or a
        // jump. These four keep one alive across calls, keyed by session id:
        // the caller sends what the keys are doing THIS frame and gets where the
        // body ended up.
        //
        // The physics is CharacterPhysics either way. Holding state is the whole
        // difference between this and /step, and it is what makes the client
        // (MapleNecrocer: a live Keyboard.KeyDown per tick) reproducible at all.

        group.MapPost("/session", (MapSimStartRequest request, MapSimSessionStore sessions) =>
        {
            MapSimFrame frame = sessions.Start(request.Path, request.StartX, request.StartY, request.Direction, request.Weapon, request.EquippedWeapon, request.Invulnerable, request.Items);
            // A refusal has to say so. Returning a default frame instead would
            // hand back sessionId="" and a silent 200, and every caller reads
            // that as "placed, but standing nowhere".
            return frame.Error is { } error
                ? Results.BadRequest(new { error })
                : Results.Ok(frame);
        });

        group.MapPost("/session/{id}/frame", (string id, MapSimInput input, MapSimSessionStore sessions) =>
        {
            MapSimFrame frame = sessions.Step(id, input);
            return frame.SessionId.Length == 0
                ? Results.NotFound(new { error = $"no live character with id {id}" })
                : Results.Ok(frame);
        });

        // Bring a dead character back at the map's spawn portal.
        //
        // `Stage::respawn(portalid)` / `Player::respawn(pos, underwater)` —
        // Stage.cpp:497-508 and Player.cpp:210-251, reached from the revive prompt
        // at UIStateGame.cpp:548-556. Without it a corpse is a dead end that reads
        // as a broken build: MEASURED, a 50 HP character killed on frame 0 by
        // 4220000 could not attack, could not cast a skill, could not walk, and
        // had nothing to come back to.
        group.MapPost("/session/{id}/revive", (string id, MapSimSessionStore sessions) =>
        {
            MapSimFrame? frame = sessions.Revive(id);
            return frame is null
                ? Results.NotFound(new { error = $"no live character with id {id}" })
                : Results.Ok(frame);
        });

        // Take damage, or stop taking it, mid-walk.
        //
        // `player.is_invincible()` — UIStateGame.cpp:794 for the damage path and
        // Player.cpp:1252 for the attack path. It was a START parameter only, so
        // nothing a user does could reach it, and a level-1 character (50 HP) on a
        // mob map is dead within seconds: `info/PADamage` is 90..150 there.
        group.MapPost("/session/{id}/invulnerable",
            (string id, MapSimInvulnerableRequest request, MapSimSessionStore sessions) =>
            {
                MapSimFrame? frame = sessions.SetInvulnerable(id, request.Value);
                return frame is null
                    ? Results.NotFound(new { error = $"no live character with id {id}" })
                    : Results.Ok(frame);
            });

        // N physics steps for the price of one round trip, answering with the
        // LAST frame plus every swing that landed in between.
        //
        // The single-frame route is correct but costs one HTTP round trip per
        // 1/60s tick. The browser's accumulator then credits each turn with only
        // the time between turn STARTS, so a body steps slower the more its
        // frames cost on the wire and stops stepping altogether when the round
        // trip exceeds the tick. That is visible as physics that will not jump
        // and mobs that walk in slow motion -- the simulation is not wrong, it is
        // being handed fewer ticks than the wall clock spent.
        //
        // Nothing here is new logic: each entry is one call to the same Step()
        // the one-frame route makes. The steps still happen one at a time, in
        // order, with the same 1/60s fixed step.
        group.MapPost("/session/{id}/frames", (string id, MapSimBatchRequest request, MapSimSessionStore sessions) =>
        {
            // The 60 is the browser's catch-up window and is deliberate, but
            // CLAMPING IT SILENTLY is a lie the caller cannot detect: ask for 600
            // and get 60 with no field saying so. MEASURED the cost of that --
            // a measurement that asked for 600 ticks to see whether mobs patrol
            // read "0 of 50 mobs moved in 10 seconds", and the mobs were fine.
            // The cap is reported, so a caller that loops can tell a clamp from a
            // stall instead of drawing a conclusion from a number that is not what
            // it asked for.
            int requested = request.Count ?? 1;
            int count = Math.Clamp(requested, 1, 60);
            List<MapSimDamageNumber> swings = new();
            MapSimFrame frame = default!;
            for (int i = 0; i < count; i++)
            {
                // Every step but the last repeats the previous input. The
                // browser sends the held keys ONCE; a 12-frame catch-up that
                // waited for 12 round trips would otherwise need 12 copies of
                // the same payload, and the edges (jump, attack) stay true for
                // the first step and false after it, exactly as a held key
                // behaves across a batch.
                MapSimInput input = i == 0
                    ? request.First ?? new MapSimInput()
                    : request.Then is { } held && held.AnyHeld()
                        ? held
                        : request.First ?? new MapSimInput();
                frame = sessions.Step(id, input, request.Manual);
                if (frame.SessionId.Length == 0)
                    return Results.NotFound(new { error = $"no live character with id {id}" });
                if (frame.Damage is { } hit) swings.AddRange(hit);
            }
            frame.Damage = swings.Count == 0 ? null : swings;
            // On the frame itself, not wrapped around it: livewalk.js:253-255
            // returns this body whole and every caller reads f.x / f.mobs off the
            // top level, so a { frame: ... } envelope would break the browser.
            frame.TicksRequested = requested;
            frame.TicksRun = count;
            frame.Clamped = requested > 60;
            return Results.Ok(frame);
        });

        group.MapGet("/session/{id}", (string id, MapSimSessionStore sessions) =>
        {
            MapSimFrame? frame = sessions.Peek(id);
            return frame is null
                ? Results.NotFound(new { error = $"no live character with id {id}" })
                : Results.Ok(frame);
        });

        // The other half of the clock, and the reason it exists.
        //
        // The endpoint above returns where the body IS; this one says what the
        // keys are doing. It does not step anything. A client posts "right is
        // held" once and then polls, and the body keeps walking between the
        // polls -- which is the client's own shape (Keyboard.KeyDown read every
        // tick, RenderFormDraw.cs:181-202) with the clock on this side of the
        // socket for once.
        //
        // The frame endpoint is not deprecated by this. A caller that wants to
        // decide every single frame still posts frames, and the clock stands
        // down while it does (MapSimSessionStore.ManualUntilMs).
        group.MapPost("/session/{id}/input", (string id, MapSimInput input, MapSimSessionStore sessions) =>
            sessions.Hold(id, input)
                ? Results.Ok(sessions.Peek(id))
                : Results.NotFound(new { error = $"no live character with id {id}" }));

        // Swap the weapon a live character is holding.
        //
        // Exists because the swing stances are a property of the WEAPON, not of the
        // character: `info/attack` selects the group (WeaponData.cpp:35 ->
        // CharLook.cpp:648, table :682-696), so picking a different sword in the
        // equip bar has to reach the physics, or the figure draws one weapon and
        // swings with another. See the EquippedWeapon remarks on why this is not
        // the `Weapon` field.
        group.MapPost("/session/{id}/weapon", (string id, MapSimWeaponRequest request, MapSimSessionStore sessions) =>
            sessions.SetEquippedWeapon(id, request.EquippedWeapon)
                // Answered with the same frame shape as every other session route, so
                // the client reads the result the same way whether it equipped or not.
                ? Results.Ok(sessions.Peek(id))
                : Results.NotFound(new { error = $"no live character with id {id}" }));

        // The WHOLE equipped set, as one replacement.
        //
        // /weapon above is a one-slot convenience and stays, because the session's
        // own start request carries a weapon and every existing harness uses it. This
        // is the route that makes the rest of the armour count: the reference's sheet
        // is `base + every equipped item's inc*` (Player.cpp:309-313), and 326 of 840
        // sampled items carry `incPDD`, 123 carry `incSTR`, 134 carry `incDEX` —
        // numbers that change accuracy, max HP and max damage, not just appearance.
        //
        // A REPLACEMENT, not a patch, for the reason the skill list is a replacement
        // (SimulatorEndpoints.cs:402-404): a client that posted only what changed
        // would leave the server unable to tell "removed" from "never equipped".
        group.MapPost("/session/{id}/equip", (string id, MapSimEquipRequest request, MapSimSessionStore sessions) =>
            sessions.SetEquipped(id, request.Items)
                ? Results.Ok(sessions.Peek(id))
                : Results.NotFound(new { error = $"no live character with id {id}" }));

        // Summoning, the reference's MobForm.cs:107-124 spawn button: N mobs at a
        // random x within 100px of the character, dropped when that x falls outside
        // the map's walkable extent. The benchmark holds no session, so it reports
        // nothing about a partial spawn; `SummonResult.Rejected` is how the caller
        // learns that 3 of 5 landed out of bounds.
        group.MapPost("/session/{id}/summon", (string id, MapSimSummonRequest request, MapSimSessionStore sessions) =>
        {
            MapSimSessionStore.SummonResult? result =
                sessions.Summon(id, request.MobId, Math.Clamp(request.Count, 1, 64), request.Kind);
            return result is null
                ? Results.NotFound(new { error = $"no live character with id {id}" })
                : Results.Ok(result);
        });

        // The Remove button, MobForm.cs:126-140.
        group.MapPost("/session/{id}/despawn", (string id, MapSimSessionStore sessions) =>
            sessions.DespawnSummoned(id) is int removed
                ? Results.Ok(new { removed = removed })
                : Results.NotFound(new { error = $"no live character with id {id}" }));

        // The reference's SkillForm hotkey grid, as a POST: SkillForm.cs:166-174.
        //
        // A WHOLE-LIST replacement, not a patch, because the reference rebuilds the
        // entire dictionary from its grid on every change (SkillForm.cs:168-174).
        // The two mutual exclusions -- one skill to one key, one key to one skill
        // (SkillForm.cs:156-162) -- are enforced server-side and rows that lose a
        // collision are reported rather than dropped in silence.
        group.MapPost("/session/{id}/skills", (string id, MapSimSkillsRequest request, MapSimSessionStore sessions) =>
            sessions.BindSkills(id, request.Bindings.Select(b => (b.SkillId, b.Key)).ToList())
                is { } result
                ? Results.Ok(new
                {
                    bindings = result.Bindings,
                    count = result.Bindings.Count,
                    rejected = result.Rejected,
                })
                : Results.NotFound(new { error = $"no live character with id {id}" }));

        // Skill.HotKeyList.Clear(), SkillForm.cs:168 -- the grid losing its rows.
        group.MapPost("/session/{id}/skills/clear", (string id, MapSimSessionStore sessions) =>
            sessions.ClearSkillBindings(id) is { } cleared
                ? Results.Ok(new { cleared = cleared })
                : Results.NotFound(new { error = $"no live character with id {id}" }));

        group.MapDelete("/session/{id}", (string id, MapSimSessionStore sessions) =>
            sessions.End(id) ? Results.Ok(new { ended = true }) : Results.NotFound());

        // The COMPOSED look for one pose: every layer, its zmap rank, its slots,
        // its priority, the anchor that applies to it, and the resulting paint
        // order. This is the route the browser blits; it does not sort, and it
        // does not decide an anchor -- doing either here would be a second
        // implementation that can disagree with CharLook.cpp.
        //
        // 404 rather than an empty look when no character art is open: "the
        // archive has no bodies" and "the archive has bodies but not this pose"
        // are different answers, and the caller draws a placeholder in the first
        // case and says which.
        group.MapGet("/look", (string? fileId, string? hair, string? face,
            string? stance, int? frame, LookService looks) =>
        {
            string pose = string.IsNullOrWhiteSpace(stance) ? "stand1" : stance;
            LookDto? look = looks.Compose(fileId, hair, face, pose, frame ?? 0);
            return look is null
                ? Results.NotFound(new { error = "no archive with character art is open" })
                : Results.Ok(look);
        });

        // The same look with equipment on. A GET cannot carry a slot map — the
        // caller would have to spell 19 optional keys in a query string, and the
        // natural shape of "what is worn" is a body, not a query — so this is a
        // POST that takes one and answers with the identical DTO.
        //
        // POST rather than GET-with-a-map for one reason: an unknown slot name is
        // a mistake the caller wants to SEE (it comes back in `unresolved`), and a
        // GET that quietly ignored it would be indistinguishable from a cap that
        // resolved and drew nothing.
        group.MapPost("/look", (EquippedLookRequest request, LookService looks) =>
        {
            string pose = string.IsNullOrWhiteSpace(request.Stance) ? "stand1" : request.Stance;
            LookDto? look = looks.Compose(request.FileId, request.Equip, pose, request.Frame ?? 0);
            return look is null
                ? Results.NotFound(new { error = "no archive with character art is open" })
                : Results.Ok(look);
        });

        // What a slot may be given. A picker needs this before it can compose
        // anything, and answering it must not mean composing a look.
        group.MapGet("/look/slots", (string? fileId, LookService looks) =>
            Results.Ok(EquipSlots.All.Select(s => new
            {
                slot = s.Name,
                folder = s.Folder,
                headWorn = s.HeadWorn,
                count = looks.SlotImages(fileId, s.Name, int.MaxValue).Count,
                sample = looks.SlotImages(fileId, s.Name, 5),
                            })));

        // Which swing stances a weapon's attacks may use.
        //
        // The client cannot work this out: the answer is the weapon's own
        // `info/attack` INTEGER, read out of the WZ (WeaponData.cpp:35), and it
        // is what CharLook::getattackstance indexes its table by
        // (CharLook.cpp:648, :682-696). One group per weapon — sword 01302000
        // carries attack=1 and gets {stabO1, stabO2, swingO1, swingO2, swingO3};
        // 01402000 carries 5 and gets the swingT group.
        //
        // `attack` accepts the bare integer too, so a caller that already knows
        // it does not have to name a weapon.
        //
        // No weapon is NOT attack 0. It is CharLook.cpp:594's early return, where
        // the stance is left unchanged, and it is reported as an empty group so
        // the caller can tell "unarmed, swing nothing" from "armed, swing this".
        group.MapGet("/look/attack-stances", (string? weapon, int? attack, CombatService combat) =>
        {
            string[] stances = attack is { } a
                ? CombatService.AttackStances(a)
                : combat.AttackStancesFor(weapon);
            return Results.Ok(new
            {
                weapon,
                attack,
                stances,
                // Empty means unarmed (CharLook.cpp:594), not "no data": a weapon
                // whose id does not resolve is a lookup failure and is named.
                armed = stances.Length > 0,
            });
        });

        // Every pose the open character archive carries, and how many frames
        // each one has. The answer a caller needs BEFORE it asks for a frame.
        //
        // This exists because of a measured failure: a sweep over 80
        // stance/frame pairs found 58 that returned nothing, and every one of
        // them was the caller's fault — either a stance name no sheet in
        // Character.nx carries ("sitted", "move", "ladder-rope"), or a frame
        // index past the end of a real pose. Both returned an empty part list
        // Every mob this archive can summon, with a name and a thumbnail path.
        //
        // Exists because the summon cluster only had a free-text id box, and the
        // reference's own MobForm is a LIST (MobForm.cs:107-124) -- a picker over
        // what the archive holds, not a field you have to already know the answer
        // to. Measured on the open Mob.nx: 2,054 images, 2,053 of them usable.
        //
        // The id is the IMAGE name, not a number the caller supplies. That matters:
        // `100100` does not exist as a node -- the archive stores it as
        // `0100100.img` and /simulate/mob-sprite reports it as a link -- so a
        // catalogue built from what a caller might type would be missing it, and
        // a catalogue built from the archive's own children cannot.
        //
        // The thumbnail is the mob's `stand/0`, NOT an icon: `info/icon` is absent
        // on all 2,053 (measured, 16/10 of the same sweep that found bodyAttack on
        // 1,945), so there is no icon to show and the honest stand-in is the pose
        // the creature spends most of its life in. The caller draws it at whatever
        // size it likes; the path is all this endpoint promises.
        group.MapGet("/mob-catalog", (WzSessionService session, StringPoolService strings) =>
        {
            var rows = new List<object>();
            lock (session.Gate)
            {
                foreach (OpenFile file in session.SelectRoleSources("Mob", null))
                {
                    foreach (NodeDto child in session.GetChildren(file.Id))
                    {
                        if (!child.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) continue;
                        string bare = child.Name[..^4];
                        if (!int.TryParse(bare, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int id)) continue;

                        rows.Add(new
                        {
                            id = bare,
                            name = strings.GetMobName(id) ?? "",
                            // Empty when the mob carries no `stand`, so the caller can
                            // show the id alone rather than a broken tile.
                            thumb = session.TryResolve($"{child.Path}/stand/0") is null
                                ? "" : $"{child.Path}/stand/0",
                        });
                    }
                }
            }
            return Results.Ok(new { count = rows.Count, mobs = rows });
        });

        // Every pose the open character archive carries, and how many frames
        // each one has. The answer a caller needs BEFORE it asks for a frame.
        //
        // This exists because of a measured failure: a sweep over 80
        // stance/frame pairs found 58 that returned nothing, and every one of
        // them was the caller's fault -- either a stance name no sheet in
        // Character.nx carries ("sitted", "move", "ladder-rope"), or a frame
        // index past the end of a real pose. Both returned an empty part list
        // and HTTP 200, so they were indistinguishable and unfixable from the
        // outside. With this, a caller clamps `frame % frameCount` itself.
        group.MapGet("/look/stances", (string? fileId, LookService looks) =>
        {
            IReadOnlyDictionary<string, int> frames = looks.StanceFrames(fileId);
            return Results.Ok(new
            {
                count = frames.Count,
                stances = frames.ToDictionary(
                    kv => kv.Key,
                    kv => new { frameCount = kv.Value },
                    StringComparer.Ordinal),
            });
        });

        // Everything one mob can draw, in one response: each action's frames
        // with its own origin and delay, and where its art really lives when
        // info/link moved it. One request rather than one per action, because a
        // map carries 29 mobs and the browser asks about them on every frame.
        //
        // 200 with hasArt:false rather than 404. "No open archive holds this mob"
        // is the ordinary state for a map open without Mob.nx, and the caller has
        // to draw a placeholder AND say which -- which needs a body to read, not
        // an exception to catch.
        group.MapGet("/mob-sprite", (string mobId, string? fileId, string? kind, MobSpriteService mobs) =>
            Results.Ok(mobs.Describe(fileId, mobId, kind)));

        // The mob bestiary page — what MobTooltipRenderer.cs draws.
        //
        // 200 with hasInfo:false rather than 404, for the same reason
        // /mob-sprite does it: "the picker listed an id nothing open draws a
        // body for" is an ordinary state when Mob.nx is not open, and the
        // caller has to be able to say so.
        group.MapGet("/mob-info", (string mobId, string? fileId, MobBestiaryService bestiary) =>
            Results.Ok(bestiary.Describe(fileId, mobId)));

        // What one piece of equipment declares, read off its own info node.
        //
        // The stat nodes are the reference's, not this project's: C++
        // EquipData.cpp:43-57 and the C# Gear.cs:840-854 both take the info
        // child's NAME as the stat it means. 200 with found:false rather than
        // 404 for the same reason /mob-sprite does it.
        group.MapGet("/equip-stats", (string itemId, EquipStatsService equips) =>
            Results.Ok(equips.Describe(itemId)));

        // What one NPC can say, and the balloon it says it in.
        //
        // A TWO-HOP lookup, and that is the reference's shape rather than a
        // convenience: Npc.nx names string KEYS (Npc.cpp:127-128) and String.nx
        // carries the lines (:60). 7,123 String entries measured, and `n0` is on
        // 42 of the first 60 -- idle chatter is the common case, not an edge case.
        group.MapGet("/npc-chat", (string npcId, NpcChatService chats) =>
            Results.Ok(chats.Describe(npcId)));

        // What attacks one mob has, and whether it has any.
        //
        // This exists because "the mob does not attack" was not answerable from
        // the screen. Measured 2026-10-16: 41% of Mob.nx carries an `attack*`
        // node, so on a map of mobs that carry none, NO amount of correct
        // behaviour produces an attack animation — the answer is in the archive,
        // and a viewer cannot see the archive. `f4/Map/Map1/100000004.img` is
        // such a map: both its mob ids (2110200, 2220100) carry none, 0 of 2.
        group.MapGet("/mob-attacks", (string mobId, CombatService combat) =>
            Results.Ok(new
            {
                mobId,
                attacks = combat.MobAttacks(mobId),
            }));

        // What the simulation measured about ITSELF, for one live session.
        //
        // Findings, not failures: a mob with no attack art and a hit pose whose
        // draw origin differs from stand are both facts about the ARCHIVE, and
        // three reports in one evening could not tell them apart from breakage
        // because nothing in the product said. This reports either way, with the
        // numbers attached.
        group.MapGet("/debug/health", (SimAuditService audit, string? sessionId) =>
            Results.Ok(new { sessionId, findings = audit.Audit(sessionId) }));
    }

    /// <summary>Where to put a live character. A null coordinate means the
    /// map's own spawn portal.</summary>
    /// <summary>Whether a live character takes damage, flipped mid-walk.</summary>
    public sealed class MapSimInvulnerableRequest
    {
        public bool Value { get; set; }
    }

    public sealed class MapSimStartRequest
    {
        public string Path { get; set; } = "";
        public int? StartX { get; set; }
        public int? StartY { get; set; }
        public int Direction { get; set; } = 1;

        /// <summary>
        /// A slot -> item map for the whole equipped set, applied after
        /// <see cref="EquippedWeapon"/>.
        /// </summary>
        /// <remarks>
        /// Both, and in that order, because they answer different questions:
        /// <c>EquippedWeapon</c> is the swing BOX and the stance group (it is read
        /// by <c>ResolveSwingHit</c> and <c>AttackStancesFor</c>), while this map
        /// is the SHEET. A caller that starts a session with a bare weapon id and
        /// no map gets a character that swings correctly and wears nothing.
        /// </remarks>
        public Dictionary<string, string>? Items { get; set; }

        /// <summary>
        /// Weapon image name (e.g. <c>1302000.img</c>) the character swings.
        /// </summary>
        /// <remarks>
        /// Optional on the wire, NOT optional in effect: omit it and every swing
        /// resolves to an empty attack box, so the session walks and jumps and
        /// can never deal damage. The default is deliberately empty rather than
        /// a guessed weapon -- a wrong weapon silently produces wrong damage
        /// numbers, which is worse than obviously dealing none.
        /// </remarks>
        public string? Weapon { get; set; }

        /// <summary>
        /// The WZ weapon IMAGE equipped on the character, e.g. <c>01302000</c>.
        /// Decides which SWING STANCES the attack animation may use.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Weapon"/> on purpose: that one names an
        /// <c>Afterimage</c> image for the hit box and falls back to
        /// <c>barehands.img</c>, while this one is the equipped weapon's own data,
        /// whose <c>info/attack</c> integer selects the stance group
        /// (WeaponData.cpp:35 -&gt; CharLook.cpp:648, table :682-696). Leave it
        /// null and the character is unarmed, which is CharLook.cpp:594's early
        /// return — it swings with its bare-hands hit box and shows NO swing pose,
        /// exactly as the reference does.
        /// </remarks>
        public string? EquippedWeapon { get; set; }

        /// <summary>
        /// The character cannot be hurt. A DEBUG switch.
        /// </summary>
        /// <remarks>
        /// Own-design, and off by default. It exists so a mob's attack ANIMATION
        /// can be watched to its end: contact damage is applied every frame the
        /// character overlaps a mob (Stage.cpp:809 has no timer at all), so
        /// without this the character dies on the first touch and the rest of the
        /// attack is never observable.
        ///
        /// <para>
        /// It suppresses the HP spend only. The damage is still reported, so the
        /// number still shows and the mob still counts the hit -- suppressing that
        /// too would hide the thing being tested.
        /// </para>
        /// </remarks>
        public bool Invulnerable { get; set; }
    }

    /// <summary>What the browser managed and failed to decode.</summary>
    public sealed class ClientArtReport
    {
        public int Attempted { get; set; }
        public int Missing { get; set; }
        public string[]? MissingSample { get; set; }
    }

    /// <summary>Which mob summons this session holds.</summary>
    public sealed class MapSimSummonRequest
    {
        /// <summary>
        /// The mob id, as the Mob archive names it — <c>100100</c>, not
        /// <c>100100.img</c> and not <c>Mob/100100.img</c>.
        /// </summary>
        /// <remarks>
        /// MobForm.cs:109 concatenates it as <c>"Mob/" + MobID + ".img"</c>, so the
        /// id is the part in between. A caller that sends the whole path gets the
        /// reference's own silent <c>return</c> — no exception, no mob — which is
        /// why the answer carries a <c>rejected</c> list rather than being empty.
        /// </remarks>
        public string MobId { get; set; } = "";

        /// <summary>How many. The reference parses its count box as an integer.</summary>
        public int Count { get; set; } = 1;

        /// <summary><c>m</c> or <c>n</c>; null lets the archive decide.</summary>
        public string? Kind { get; set; }
    }

    /// <summary>One row of the reference's hotkey grid.</summary>
    public sealed class MapSimSkillBinding
    {
        /// <summary>The skill, as the archive names it — <c>1121008</c>, not <c>1121008.img</c>.</summary>
        public string SkillId { get; set; } = "";

        /// <summary>
        /// The key, one letter.
        /// </summary>
        /// <remarks>
        /// SkillForm.Designer.cs:50 hardcodes "A".."Z" into the combo that supplies
        /// it, so that is the whole set of keys the reference can bind. Anything else
        /// is refused by name rather than accepted as a capability the reference
        /// does not have.
        /// </remarks>
        public string Key { get; set; } = "";
    }

    /// <summary>
    /// The whole binding grid at once, because the reference always sends the whole
    /// grid: it clears and rebuilds (SkillForm.cs:168-174).
    /// </summary>
    public sealed class MapSimSkillsRequest
    {
        /// <summary>
        /// Order matters: a skill or key that appears twice keeps the LAST row,
        /// which is the same outcome the reference gets from rebuilding its grid.
        /// </summary>
        public List<MapSimSkillBinding> Bindings { get; set; } = new();
    }

    /// <summary>
    /// A composed look with equipment on: which slot carries which image.
    /// </summary>
    /// <remarks>
    /// A dictionary rather than 19 nullable fields because the slots are a set,
    /// not a fixed record — the caller wearing a hat and a sword sends two entries
    /// and means those two, and "the other seventeen are empty" is a fact about
    /// the character rather than a thing it has to spell out.
    /// </remarks>
    public sealed class EquippedLookRequest
    {
        public string? FileId { get; set; }
        public string? Stance { get; set; }
        public int? Frame { get; set; }

        /// <summary>Slot name to image name, e.g. <c>{"Cap-1": "01000000"}</c>.</summary>
        public Dictionary<string, string> Equip { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
