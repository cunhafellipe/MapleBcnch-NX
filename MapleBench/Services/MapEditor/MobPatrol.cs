using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// A mob that walks its own foothold chain, turning at the ends.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on MapleNecrocer's <c>Client/Mob.cs</c> (MIT, Elem8100), which does
/// three things per tick: pick a direction at random, move along the current
/// foothold's angle, and reverse at the edge. The geometry is the same machinery
/// the player uses — <c>GetAngle256</c> then <c>Sin256</c>/<c>Cos256</c> — which
/// is why a mob stays on the ground on a slope instead of hovering over it.
/// </para>
///
/// <para>
/// <b>The one deliberate difference is the seed.</b> MapleNecrocer calls
/// <c>new Random()</c> every tick, unseeded, so its mob movement is different
/// on every run and cannot be reproduced. Here the generator is supplied, so a
/// patrol replays exactly. That is the whole point of asking a machine whether
/// a map works: the answer has to be the same twice.
/// </para>
///
/// <para>
/// Read-only. A patrol never writes to the map.
/// </para>
/// </remarks>
public sealed class MobPatrol
{
    /// <summary>
    /// The roll distribution MapleNecrocer uses (<c>Random.Next(200)</c>):
    /// left below 50, right below 100, stand below 150, jump at 199.
    /// </summary>
    /// <remarks>
    /// The jump band is deliberately not implemented. A mob that leaps a gap
    /// would move the question this answers — "can a character stand here" —
    /// to a different one, and the same question has to be asked of every spawn
    /// for the answer to mean anything.
    /// </remarks>
    private const int RollRange = 200;
    private const int LeftBelow = 50;
    private const int RightBelow = 100;
    private const int StandBelow = 150;

    /// <summary>How fast a mob walks, per frame. MapleNecrocer's MoveSpeed.</summary>
    public const float MoveSpeed = 0.7f;

    private readonly FootholdPhysics _map;
    private readonly Random _rng;
    private readonly int _rollEvery;

    public MobPatrol(FootholdPhysics map, FootholdPhysics.Foot start, int seed, int rollEvery = 20)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _rng = new Random(seed);
        _rollEvery = Math.Max(1, rollEvery);
        _foothold = start;
        _x = start.X1;
        _y = start.Y1;
        Direction = 1;
    }

    private FootholdPhysics.Foot _foothold;
    private float _x;
    private float _y;
    private int _sinceRoll;

    /// <summary>Current map position.</summary>
    public float X => _x;

    public float Y => _y;

    /// <summary>+1 right, -1 left.</summary>
    public int Direction { get; private set; }

    /// <summary>The foothold being walked.</summary>
    public FootholdPhysics.Foot Foothold => _foothold;

    /// <summary>How many times this mob has turned around.</summary>
    public int Reversals { get; private set; }

    /// <summary>Whether it is still standing on a chain.</summary>
    public bool OnGround { get; private set; } = true;

    /// <summary>Why it left the chain, or null.</summary>
    public string? LeftGroundBecause { get; private set; }

    /// <summary>Run one frame.</summary>
    public void Step()
    {
        if (!OnGround)
        {
            // A mob that walks off its chain falls, and a map that drops its
            // monsters off the edge is worth reporting rather than animating.
            LeftGroundBecause ??= "the mob walked past the end of its foothold chain";
            return;
        }

        if (_foothold.IsWall)
        {
            OnGround = false;
            LeftGroundBecause = "the mob spawned on a wall, which it cannot walk along";
            return;
        }

        if (++_sinceRoll >= _rollEvery)
        {
            _sinceRoll = 0;
            Roll();
        }

        // The client's own motion: along the foothold's direction.
        int angle = Angle256(
            Direction > 0 ? _foothold.X1 : _foothold.X2,
            Direction > 0 ? _foothold.Y1 : _foothold.Y2,
            Direction > 0 ? _foothold.X2 : _foothold.X1,
            Direction > 0 ? _foothold.Y2 : _foothold.Y1);

        _x += (float)(Sin256(angle) * MoveSpeed);
        _y -= (float)(Cos256(angle) * MoveSpeed);

        // The edge of this foothold, and what is past it.
        int edge = Direction > 0 ? _foothold.X2 : _foothold.X1;
        bool past = Direction > 0 ? _x >= edge : _x <= edge;
        if (!past)
            return;

        long nextId = Direction > 0 ? _foothold.Next : _foothold.Prev;

        // MapleNecrocer turns around when there is no next foothold OR when the
        // next one is a wall (Mob.cs:566-577). Both mean "this is the end of
        // what I can walk".
        if (nextId == 0)
        {
            Reverse();
            _x = edge;
            return;
        }

        FootholdPhysics.Foot? next = null;
        foreach (FootholdPhysics.Foot f in _map.Footholds)
        {
            if (!long.TryParse(f.Id, out long fid) || fid != nextId)
                continue;
            next = f;
            break;
        }

        if (next is not { IsWall: false } step)
        {
            Reverse();
            _x = edge;
            return;
        }

        _foothold = step;
        _x = Direction > 0 ? step.X1 : step.X2;
        _y = Direction > 0 ? step.Y1 : step.Y2;
    }

    private void Roll()
    {
        int roll = _rng.Next(RollRange);
        if (roll < LeftBelow)
            Direction = -1;
        else if (roll < RightBelow)
            Direction = 1;
        else if (roll < StandBelow)
            Direction = 0;
        // >= StandBelow: keep the current direction, except the top of the
        // range which the client uses to jump. A patrol does not jump; a mob that
        // leapt across a gap would leave the question "can it walk here"
        // unanswerable.
    }

    private void Reverse()
    {
        Direction = -Direction;
        Reversals++;
    }

    private const float PiConv256 = -40.743665431f;

    private static int Angle256(int sx, int sy, int dx, int dy) =>
        (int)(Math.Atan2(dx - sx, dy - sy) * PiConv256) + 128;

    private static double Cos256(int i) => Math.Cos(i * Math.PI / 128.0);

    private static double Sin256(int i) => Cos256((i + 192) & 255);
}