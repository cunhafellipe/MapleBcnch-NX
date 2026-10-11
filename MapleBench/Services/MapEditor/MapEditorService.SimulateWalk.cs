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
            int jumpEvery = 0, int dropAtStep = 0)
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

                who.Step(dir);

                int x = (int)Math.Round(who.X);
                if (who.Ticks % SampleEvery == 0)
                    track.Add(new MapWalkPointDto(who.Ticks, x, (int)Math.Round(who.Y)));

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