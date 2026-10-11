using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Runs an actual character across a map under MapleNecrocer's physics.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between reading the data and simulating it.
/// <see cref="Simulate"/> follows the <c>next</c> chain and can only see
/// whether the LINKS are intact. This places a character on the map, accelerates
/// it with the client's own constants and lets gravity settle it, so a chain
/// that is intact but physically unusable — a ledge too far above the last one,
/// a slope steeper than the character can climb — shows up as a character that
/// stops moving.
/// </para>
///
/// <para>
/// Read-only, like everything else in this file. The character exists only for
/// the length of the call.
/// </para>
/// </remarks>
public sealed partial class MapEditorService
{
    /// <summary>How many fixed steps a walk may take before it is called stuck.</summary>
    private const int MaxWalkTicks = 60 * 60 * 5;   // 5 simulated seconds

    /// <summary>
    /// Walks a real character across a map and reports where it ends up.
    /// </summary>
    public MapWalkRunDto WalkCharacter(string path, int? startX, int? startY, int direction = 1,
            int jumpEvery = 0, int dropAtStep = 0, int climb = 0)
    {
        lock (_gate)
        {
            int dir = direction >= 0 ? 1 : -1;

            MapDocument map = LoadForReading(path);
            FootholdPhysics physics = new(map);

            MapWalkRunDto result = new()
            {
                Path = path,
                Direction = dir,
                JumpEvery = jumpEvery,
                DropAtStep = dropAtStep,
                Climb = climb,
                Ladders = physics.Ladders.Count,
                Footholds = physics.Footholds.Count,
                StepSeconds = CharacterPhysics.StepSeconds,
                Acceleration = CharacterPhysics.Acceleration,
                MaxWalkSpeed = CharacterPhysics.MaxWalkSpeed,
                JumpSpeed = CharacterPhysics.JumpSpeed,
                MaxFallSpeed = CharacterPhysics.MaxFallSpeed,
            };

            if (physics.Footholds.Count == 0)
            {
                result.Reason = "No foothold to stand on.";
                return result;
            }

            (int Left, int Top, int Right, int Bottom) bounds = physics.Bounds!.Value;
            result.Bounds = new MapSimulateBoundsDto(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

            // The start is the edge the character walks FROM, so it depends on direction:
            // walking left has to begin at the right edge or it has nowhere to go.
            int originX = startX ?? (dir > 0 ? bounds.Left : bounds.Right);

            FootholdPhysics.Foot? start = FindStartFoothold(physics, originX, startY);
            if (start is null)
            {
                result.Reason = $"Nothing to stand on at x={originX}.";
                return result;
            }

            result.StartX = start.Value.X1;
            result.StartY = start.Value.Y1;

            // The character starts ON the foothold, exactly as the client spawns
            // it: Player.SpawnNew sets FH from FindBelow and starts grounded
            // (MapleCharacter.cs). No drop, no guess.
            CharacterPhysics who = new(physics, start.Value, dir);

            int lastX = int.MinValue;
            int stuckFor = 0;
            const int StuckTicks = 30;   // half a second without progress

            // Sampled as it happens, not reconstructed afterwards: a walk is
            // thousands of steps and the interesting part is where it went, not
            // every frame of it.
            List<MapWalkPointDto> track = [];
            const int SampleEvery = 6;

            while (who.Ticks < MaxWalkTicks)
            {
                // A walk is a script, not a player, so the client's LeftAlt has
                // no equivalent here — these two make the jump and drop-through
                // paths reachable and therefore testable instead of dead code.
                //
                // `who.Ticks > 0` is required: at tick 0 the modulo is true, the
                // character jumps before it has walked anywhere, lands back on
                // the same foothold, and the walk is reported as over after one
                // step. Measured: every jump cadence ended at tick 1.
                if (jumpEvery > 0 && who.Ticks > 0 && who.Ticks % jumpEvery == 0)
                    who.Jump();
                else if (dropAtStep > 0 && who.Ticks == dropAtStep - 1)
                    who.DropThrough();

                who.Step(dir, climb);

                // A portal beside the path is how this map continues. Checking
                // only after a successful walk missed them: the chain routinely
                // stops short of the door, and the walk then reports "fell out"
                // for a map the character could have left through a portal four
                // pixels away.
                if (result.ReachedPortal.Length == 0
                    && physics.FindPortal((int)Math.Round(who.X), (int)Math.Round(who.Y))
                        is { IsExit: true } doorNow)
                {
                    result.ReachedPortal = doorNow.Name;
                    result.PortalTargetMap = doorNow.ToMap;
                    result.PortalTargetName = doorNow.ToName;
                    result.Reason =
                        $"the character reached the portal '{doorNow.Name}' at x={(int)Math.Round(who.X)}, "
                        + $"y={(int)Math.Round(who.Y)} after {who.Ticks} steps "
                        + $"({who.Ticks * CharacterPhysics.StepSeconds:N1}s), bound for "
                        + $"map {doorNow.ToMap}{(doorNow.ToName.Length > 0 ? $" entry '{doorNow.ToName}'" : "")}.";
                    break;
                }

                int x = (int)Math.Round(who.X);
                if (who.Ticks % SampleEvery == 0)
                    track.Add(new MapWalkPointDto(who.Ticks, x, (int)Math.Round(who.Y)));

                // Airborne AND there is no ladder to catch: the character is
                // falling past the bottom of the world. Continuing would fly it
                // across the void at terminal velocity until the tick cap, which
                // is 18,000 steps of nothing. Measured: a climb attempt that
                // missed its ladder spent the entire budget drifting.
                if (!who.OnGround && !LadderInReach(physics, who))
                {
                    result.FellOut = true;
                    result.Reason =
                        $"the character left the ground at x={x}, y={(int)Math.Round(who.Y)} "
                        + $"after {who.Ticks} steps and there is no ladder within reach — "
                        + "it falls out of the map.";
                    break;
                }

                if (!who.OnGround)
                {
                    // Reaching the edge and then stepping off it is still arriving:
                    // the last foothold's far end IS the map's edge. Measured, a
                    // leftward walk reached x=-546, stepped past the final
                    // foothold, and was reported as having fallen out.
                    bool arrived = dir > 0 ? x >= bounds.Right : x <= bounds.Left;
                    if (arrived)
                    {
                        result.ReachedEdge = true;
                        result.Reason =
                            $"the character crossed the map {(dir > 0 ? "left to right" : "right to left")} "
                            + $"in {who.Ticks} steps ({who.Ticks * CharacterPhysics.StepSeconds:N1}s) "
                            + $"and reached the {(dir > 0 ? "right" : "left")} edge at x={x}.";
                    }
                    else if (RanOutOfGround(physics, who))
                    {
                        // Left the chain with NO foothold anywhere below at this X:
                        // the character is over a void and will fall out of the
                        // world. That is the finding, and it is why the walk
                        // exists — /validate says the links are intact while this
                        // says there is nothing underneath the character.
                        result.FellOut = true;
                        result.Reason =
                            $"the character ran out of ground at x={x}, y={(int)Math.Round(who.Y)} "
                            + $"after {who.Ticks} steps ({who.Ticks * CharacterPhysics.StepSeconds:N1}s) — "
                            + (who.LeftGroundBecause ?? "there is no foothold below it at this X");
                    }
                    else
                    {
                        // The chain simply ended short of the edge. On a
                        // multi-level map that is normal: the rest is reached by
                        // a ladder, a portal or a jump, none of which this models.
                        // Calling it a defect would condemn working maps, so it is
                        // reported as what it is. Measured: 31 of 200 walks ended
                        // here, several of them at 97% of the span.
                        // Ending at the mouth of a door is arriving, not being
                        // stuck: the rest of this map is reached through the
                        // portal. Measured, a map whose only chain ends beside a
                        // portal is fully playable and reported as blocked.
                        FootholdPhysics.Portal? door =
                            physics.FindPortal(x, (int)Math.Round(who.Y));

                        if (door is { IsExit: true } exit)
                        {
                            result.ReachedPortal = exit.Name;
                            result.PortalTargetMap = exit.ToMap;
                            result.PortalTargetName = exit.ToName;
                            result.Reason =
                                $"the character walked into the portal '{exit.Name}' at x={x}, "
                                + $"y={(int)Math.Round(who.Y)} after {who.Ticks} steps "
                                + $"({who.Ticks * CharacterPhysics.StepSeconds:N1}s), bound for "
                                + $"map {exit.ToMap}{(exit.ToName.Length > 0 ? $" entry '{exit.ToName}'" : "")}.";
                            break;
                        }

                        result.ChainEndedShort = true;
                        int span = Math.Max(1, bounds.Right - bounds.Left);
                        int travelled = Math.Abs(x - (dir > 0 ? bounds.Left : bounds.Right));
                        result.Reason =
                            $"the chain ends at foothold {who.StandingOn?.Id ?? "?"} at x={x}, "
                            + $"y={(int)Math.Round(who.Y)}, {100.0 * travelled / span:N0}% of the way across "
                            + $"(after {who.Ticks} steps, {who.Ticks * CharacterPhysics.StepSeconds:N1}s). "
                            + "The rest of the map is reached by a ladder, a portal or a jump, which "
                            + "this walk does not model — treat it as INCONCLUSIVE, not as a broken map.";
                    }
                    break;
                }

                if (x == lastX)
                {
                    if (++stuckFor >= StuckTicks)
                    {
                        result.Reason =
                            $"the character stopped moving at x={x}, y={(int)Math.Round(who.Y)} "
                            + $"after {who.Ticks} steps. The ground ahead is not reachable by walking.";
                        break;
                    }
                }
                else
                {
                    stuckFor = 0;
                    lastX = x;
                }

                // The far edge is the one the character is walking TOWARD, which is the
                // opposite one from where it started.
                bool crossed = dir > 0 ? x >= bounds.Right : x <= bounds.Left;
                if (crossed)
                {
                    result.ReachedEdge = true;
                    result.Reason =
                        $"the character crossed the map {(dir > 0 ? "left to right" : "right to left")} "
                        + $"in {who.Ticks} steps ({who.Ticks * CharacterPhysics.StepSeconds:N1}s) "
                        + $"and reached x={x}.";
                    break;
                }
            }

            result.FinalX = (int)Math.Round(who.X);
            result.FinalY = (int)Math.Round(who.Y);
            result.Ticks = who.Ticks;
            result.Seconds = who.Ticks * CharacterPhysics.StepSeconds;
            result.OnGround = who.OnGround;
            result.Distance = Math.Abs(result.FinalX - result.StartX);
            result.MapWidth = bounds.Right - bounds.Left;
            result.PercentOfMap = result.MapWidth > 0 ? 100.0 * result.Distance / result.MapWidth : 0;
            result.Track = track;

            return result;
        }
    }

    /// <summary>
    /// Whether the character is over a void: no foothold anywhere below it at
    /// this X.
    /// </summary>
    /// <remarks>
    /// The distinction matters because "the chain ended" and "there is nothing
    /// underneath" are different findings. The first is what a multi-level map
    /// does — the remaining ground is reached by a ladder or a portal. The
    /// second is a real hole. Measured: without this test 31 of 200 walks were
    /// reported as falling out of the map, several of them having covered 97% of
    /// the span, because both cases produced the same message.
    /// </remarks>
    /// <summary>
    /// Runs one step and reports every value it touched.
    /// </summary>
    /// <remarks>
    /// The walk reports where the character ended up and never why. When a
    /// 18,000-step walk finishes at its own starting position, "it moved and
    /// came back" and "it never moved" produce the same report and call for
    /// opposite fixes. This exists so the difference is observable.
    /// </remarks>
    public MapStepProbeDto StepOnce(string path, int x, int y, int dir, int climb)
    {
        lock (_gate)
        {
            FootholdPhysics physics = new(LoadForReading(path));
            FootholdPhysics.Foot? start = FindStartFoothold(physics, x, y);
            if (start is null)
                return new MapStepProbeDto { Error = $"nothing to stand on at x={x}, y={y}" };

            CharacterPhysics who = new(physics, start.Value, dir);
            MapStepProbeDto probe = new()
            {
                StartX = x,
                StartY = y,
                StartFoothold = start.Value.Id,
                Ladders = physics.Ladders.Count,
                LadderInReach = physics.Ladders.Count > 0,
                OnGroundBefore = who.OnGround,
            };

            foreach (FootholdPhysics.Ladder l in physics.Ladders)
                probe.LadderList.Add($"{l.X}:{l.Top}-{l.Bottom}");

            who.Step(dir, climb);

            probe.EndX = (int)Math.Round(who.X);
            probe.EndY = (int)Math.Round(who.Y);
            probe.Speed = who.Speed;
            probe.OnGroundAfter = who.OnGround;
            probe.OnLadderAfter = who.OnLadder is not null;
            probe.StandingOn = who.StandingOn?.Id;
            probe.LeftGround = who.LeftGroundBecause;
            return probe;
        }
    }

    /// <summary>
    /// Walks a bounded number of steps, reporting the decision at each one.
    /// </summary>
    /// <remarks>
    /// <c>StepOnce</c> builds a fresh character per call, so it cannot show a
    /// sequence — and a sequence is the whole point when a walk ends where it
    /// started. This keeps one character and returns what <c>Walk</c> decided
    /// each tick: which foothold it was on, whether it reached the end, and
    /// whether a wall stopped it.
    /// </remarks>
    public MapWalkTraceDto TraceWalk(string path, int x, int y, int dir, int climb, int steps)
    {
        lock (_gate)
        {
            FootholdPhysics physics = new(LoadForReading(path));
            var dto = new MapWalkTraceDto { Ladders = physics.Ladders.Count };

            // The foothold under the point is what the walker lands on, and
            // climbing starts from whichever foothold the character is ALREADY
            // standing on — which is not necessarily the one under (x, y).
            // This used to take the foothold under (x, y) and produced traces
            // that disagreed with the walk they were meant to explain.
            FootholdPhysics.Foot? start = FindStartFoothold(physics, x, y);
            if (start is null)
            {
                dto.Error = $"nothing to stand on at x={x}, y={y}";
                return dto;
            }
            dto.StartFoothold = start.Value.Id;
            dto.StartX = start.Value.X1;
            dto.StartY = start.Value.Y1;

            CharacterPhysics who = new(physics, start.Value, dir);
            int n = Math.Clamp(steps, 1, 500);
            for (int i = 0; i < n; i++)
            {
                who.Step(dir, climb);
                CharacterPhysics.WalkDecision? d = who.LastWalk;
                dto.Ticks.Add(new MapWalkTraceTick(
                    i, (int)Math.Round(who.X), (int)Math.Round(who.Y),
                    who.Speed, who.OnGround, who.OnLadder is not null,
                    d?.Foothold ?? "", d?.ReachedEnd ?? false,
                    d?.BlockedByWall ?? false, d?.WallAt,
                    d?.GroundBelow ?? "", who.LastAction,
                    who.OnLadder?.X, who.StandingOn?.X1,
                    who.ClimbEnterX, who.ClimbSnappedX, who.ClimbExitX,
                    who.LeftGroundBecause));
            }
            return dto;
        }
    }

    /// <summary>
    /// Whether a ladder is within reach, using the same wider box the climb uses.
    /// </summary>
    /// <remarks>
    /// Must match <c>CharacterPhysics.FindLadderForClimb</c>. If this test is
    /// narrower, the walk stops as "fell out" on exactly the maps where a ladder
    /// is the way onward; if wider, a character nowhere near a ladder keeps
    /// drifting instead of being told it fell.
    /// </remarks>
    /// <summary>
    /// Runs a mob's patrol and reports whether it stayed on the map.
    /// </summary>
    /// <remarks>
    /// The seed is a parameter, not a constant. MapleNecrocer rolls an unseeded
    /// <c>Random</c> per tick, so its mobs move differently every run and a
    /// question with a different answer each time is not a question. Given the
    /// same seed this replays exactly.
    /// </remarks>
    public MapMobPatrolDto PatrolMob(string path, int? spawnX, int? spawnY, int seed, int steps)
    {
        lock (_gate)
        {
            FootholdPhysics physics = new(LoadForReading(path));
            MapMobPatrolDto dto = new()
            {
                Seed = seed,
                Steps = Math.Clamp(steps, 1, 6000),
            };

            MapDocument doc = LoadForReading(path);
            if (doc.Life.Count == 0)
            {
                dto.Note = "this map has no mob or NPC spawns";
                return dto;
            }

            int pick = Math.Abs(seed) % doc.Life.Count;
            MapLife mob = doc.Life[pick];
            dto.MobId = mob.Id ?? mob.Key ?? "?";
            dto.SpawnX = (int)(mob.X ?? 0);
            dto.SpawnY = (int)(mob.Cy ?? mob.Y ?? 0);

            FootholdPhysics.Foot? start =
                FindStartFoothold(physics, dto.SpawnX, dto.SpawnY);
            if (start is null)
            {
                dto.StayedOnMap = false;
                dto.Reason =
                    $"the spawn at x={dto.SpawnX}, y={dto.SpawnY} has no foothold under it — "
                    + "the mob would fall the moment the map loads.";
                return dto;
            }

            dto.StartFoothold = start.Value.Id;
            MobPatrol mob2 = new(physics, start.Value, seed);
            float maxDx = 0;
            for (int i = 0; i < dto.Steps && mob2.OnGround; i++)
            {
                mob2.Step();
                maxDx = Math.Max(maxDx, Math.Abs(mob2.X - dto.SpawnX));
            }

            dto.EndX = (int)Math.Round(mob2.X);
            dto.EndY = (int)Math.Round(mob2.Y);
            dto.Reversals = mob2.Reversals;
            dto.EndingFoothold = mob2.Foothold.Id;
            dto.MaxDistance = (int)Math.Round(maxDx);
            dto.StayedOnMap = mob2.OnGround;

            dto.Reason = mob2.OnGround
                ? $"the spawn patrolled {dto.Reversals} turn(s) over {maxDx:N0} px in {dto.Steps} frames, "
                  + $"ending on foothold {mob2.Foothold.Id}"
                : mob2.LeftGroundBecause ?? "the spawn left its chain";

            return dto;
        }
    }

    private static bool LadderInReach(FootholdPhysics physics, CharacterPhysics who)
    {
        int x = (int)Math.Round(who.X);
        int y = (int)Math.Round(who.Y);
        foreach (FootholdPhysics.Ladder l in physics.Ladders)
        {
            if (Math.Abs(l.X - x) < 40 && y >= l.Top - 12 && y <= l.Bottom + 12)
                return true;
        }
        return false;
    }

    private static bool RanOutOfGround(FootholdPhysics physics, CharacterPhysics who)
    {
        int x = (int)Math.Round(who.X);
        int y = (int)Math.Round(who.Y);
        return physics.FindBelow(x, y, falling: true) is null
            && physics.FindBelow(x, y, falling: false) is null;
    }

    /// <summary>Every foothold that covers an X, and whether it is below a given Y.</summary>
    /// <remarks>
    /// Exists because "the character is standing at y=119 and I do not believe it"
    /// is a question the walk's own output cannot answer. Without this, checking the
    /// physics means opening the map in the editor by hand.
    /// </remarks>
    public MapGroundProbeDto ProbeGround(string path, int x, int y)
    {
        lock (_gate)
        {
            FootholdPhysics physics = new(LoadForReading(path));
            MapGroundProbeDto probe = new() { Path = path, X = x, Y = y };

            foreach (FootholdPhysics.Foot f in physics.Footholds)
            {
                if (f.IsWall || x < f.Left || x > f.Right)
                    continue;
                int? surface = f.SurfaceAt(x);
                if (surface is not { } sy)
                    continue;
                probe.Covering.Add(new MapGroundSpanDto(f.Id, f.Layer, f.Left, f.Right, sy));
            }

            probe.Covering.Sort((a, b) => a.Surface.CompareTo(b.Surface));

            // Both readings, because the whole point of the probe is to show
            // that they disagree — that disagreement IS the bug.
            FootholdPhysics.Foot? landing = physics.FindBelow(x, y, falling: true);
            FootholdPhysics.Foot? standing = physics.FindBelow(x, y, falling: false);
            probe.LandingY = landing?.Id;
            probe.LandingSurface = landing?.SurfaceAt(x);
            probe.StandingY = standing?.Id;
            probe.StandingSurface = standing?.SurfaceAt(x);
            probe.AboveY = physics.Footholds
                .Where(f => !f.IsWall && x >= f.Left && x <= f.Right && f.SurfaceAt(x) is { } s2 && s2 < y)
                .Select(f => f.SurfaceAt(x)!.Value)
                .DefaultIfEmpty()
                .Max();
            return probe;
        }
    }
}

/// <summary>What is under a point, and what the physics would pick.</summary>
public sealed class MapGroundProbeDto
{
    public string Path { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Every foothold whose span covers X, nearest surface first.</summary>
    public List<MapGroundSpanDto> Covering { get; set; } = [];

    /// <summary>The foothold a FALLING character would land on.</summary>
    public string? LandingY { get; set; }

    /// <summary>Its surface height at X.</summary>
    public int? LandingSurface { get; set; }

    /// <summary>The foothold a GROUNDED character is standing on.</summary>
    public string? StandingY { get; set; }

    /// <summary>Its surface height at X.</summary>
    public int? StandingSurface { get; set; }

    /// <summary>The lowest surface above Y, if any — what the character just fell past.</summary>
    public int AboveY { get; set; } = int.MinValue;
}

/// <summary>One foothold covering the probed X.</summary>
public sealed record MapGroundSpanDto(string Id, string Layer, int Left, int Right, int Surface);

/// <summary>The result of one character walk.</summary>
public sealed class MapWalkRunDto
{
    /// <summary>The map that was walked.</summary>
    public string Path { get; set; } = "";

    /// <summary>Which way the character walked: 1 right, -1 left.</summary>
    public int Direction { get; set; } = 1;

    /// <summary>Jump cadence used, 0 for never. Echoed so a result can be reproduced.</summary>
    public int JumpEvery { get; set; }

    /// <summary>Step at which it dropped through, 0 for never.</summary>
    public int DropAtStep { get; set; }

    /// <summary>The portal the character walked into, or empty.</summary>
    public string ReachedPortal { get; set; } = "";

    /// <summary>Map id that portal leads to.</summary>
    public long PortalTargetMap { get; set; }

    /// <summary>Entry name inside that map, if the portal names one.</summary>
    public string PortalTargetName { get; set; } = "";

    /// <summary>Climb intent used, 0 for never. -1 is up.</summary>
    public int Climb { get; set; }

    /// <summary>How many ladders and ropes the map carries.</summary>
    public int Ladders { get; set; }

    /// <summary>Footholds on the map.</summary>
    public int Footholds { get; set; }

    /// <summary>Whether the character got to the map's right edge.</summary>
    public bool ReachedEdge { get; set; }

    /// <summary>
    /// Whether the character fell out of the world — below every foothold while
    /// still moving. This is a failure, and it is separate from ReachedEdge
    /// because a character can satisfy both conditions at once.
    /// </summary>
    public bool FellOut { get; set; }

    /// <summary>
    /// The chain ended before the map's edge, with ground still underneath.
    /// </summary>
    /// <remarks>
    /// Not a defect and not a fall: this is what a multi-level map does, where
    /// the remaining ground is reached by a ladder, a portal or a jump. Kept
    /// separate from <see cref="FellOut"/> because calling a working map
    /// broken is worse than saying nothing.
    /// </remarks>
    public bool ChainEndedShort { get; set; }

    /// <summary>Why the walk ended, in plain words.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Where the character started.</summary>
    public int StartX { get; set; }

    /// <summary>The Y it started at.</summary>
    public int StartY { get; set; }

    /// <summary>Where it ended.</summary>
    public int FinalX { get; set; }

    /// <summary>The Y it ended at.</summary>
    public int FinalY { get; set; }

    /// <summary>Fixed steps taken.</summary>
    public long Ticks { get; set; }

    /// <summary>Simulated seconds those steps represent.</summary>
    public double Seconds { get; set; }

    /// <summary>Whether it was standing on something when it stopped.</summary>
    public bool OnGround { get; set; }

    /// <summary>Horizontal distance covered.</summary>
    public int Distance { get; set; }

    /// <summary>The walkable width.</summary>
    public int MapWidth { get; set; }

    /// <summary>How much of the map it covered.</summary>
    public double PercentOfMap { get; set; }

    /// <summary>The walkable extent.</summary>
    public MapSimulateBoundsDto? Bounds { get; set; }

    /// <summary>Sampled positions along the walk.</summary>
    public List<MapWalkPointDto>? Track { get; set; }

    // The constants the run used, so a result can be reproduced exactly.
    public float StepSeconds { get; set; }
    public float Acceleration { get; set; }
    public float MaxWalkSpeed { get; set; }
    public float JumpSpeed { get; set; }
    public float MaxFallSpeed { get; set; }
}

/// <summary>One sampled position.</summary>
/// <param name="Tick">The fixed step this sample was taken at.</param>
/// <param name="X">Map X.</param>
/// <param name="Y">Map Y.</param>
public sealed record MapWalkPointDto(long Tick, int X, int Y);

/// <summary>One step, with the values it touched.</summary>
public sealed class MapStepProbeDto
{
    public string Error { get; set; } = "";
    public int StartX { get; set; }
    public int StartY { get; set; }
    public string StartFoothold { get; set; } = "";
    public int Ladders { get; set; }
    public bool LadderInReach { get; set; }
    public List<string> LadderList { get; set; } = [];
    public bool OnGroundBefore { get; set; }
    public int EndX { get; set; }
    public int EndY { get; set; }
    public float Speed { get; set; }
    public bool OnGroundAfter { get; set; }
    public bool OnLadderAfter { get; set; }
    public string? StandingOn { get; set; }
    public string? LeftGround { get; set; }
}

/// <summary>A bounded walk, with the decision at each tick.</summary>
public sealed class MapWalkTraceDto
{
    public string Error { get; set; } = "";
    public int Ladders { get; set; }
    public string StartFoothold { get; set; } = "";
    public int StartX { get; set; }
    public int StartY { get; set; }
    public List<MapWalkTraceTick> Ticks { get; set; } = [];
}

/// <summary>One tick of a traced walk.</summary>
public sealed record MapWalkTraceTick(
    int Step, int X, int Y, float Speed, bool OnGround, bool OnLadder,
    string Foothold, bool ReachedEnd, bool BlockedByWall, int? WallAt,
    string GroundBelow, string Action,
    int? LadderX, int? StandingX,
    float ClimbEnterX, float ClimbSnappedX, float ClimbExitX, string? LeftGround);

/// <summary>One spawn's patrol.</summary>
public sealed class MapMobPatrolDto
{
    public int Seed { get; set; }
    public int Steps { get; set; }
    public string Note { get; set; } = "";
    public string MobId { get; set; } = "";
    public int SpawnX { get; set; }
    public int SpawnY { get; set; }
    public string StartFoothold { get; set; } = "";
    public int EndX { get; set; }
    public int EndY { get; set; }
    public int Reversals { get; set; }
    public string EndingFoothold { get; set; } = "";
    public int MaxDistance { get; set; }
    public bool StayedOnMap { get; set; }
    public string Reason { get; set; } = "";
}
