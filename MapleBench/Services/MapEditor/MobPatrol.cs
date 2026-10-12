using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// A mob that patrols, notices the player, hits back, and dies — the whole
/// loop, as MapleNecrocer models it.
/// </summary>
/// <remarks>
/// <para>
/// Read from Elem8100/MapleNecrocer <c>Client/Mob.cs</c> (MIT):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Mob.cs:149</c> — <c>MoveSpeed = (1 + info/speed / 100) * 2</c>, defaulting
/// to 2 when the mob carries no speed field.
/// </description></item>
/// <item><description>
/// <c>Mob.cs:161-175</c> — <c>MoveType</c> comes from the mob's own animation
/// set: no <c>move</c> means it stands still, a <c>jump</c> node makes it a
/// jumper, a <c>fly</c> node makes it a flyer.
/// </description></item>
/// <item><description>
/// <c>Mob.cs:586,664,723</c> — the patrol bounds <c>RX0</c>/<c>RX1</c> are the
/// map's <c>life/rx0</c> and <c>life/rx1</c>; reaching either one turns the mob
/// around. Those fields are optional — a map that omits them gives 0, which is
/// what the client treats as "no bound".
/// </description></item>
/// <item><description>
/// <c>Mob.cs:770-824</c> — <c>MobCollision</c> is a rectangle that arms after a
/// delay and damages whatever it overlaps; HP reaching zero starts the death
/// animation.
/// </description></item>
/// <item><description>
/// <c>Mob.cs:445</c> — a mob struck from behind turns to face the attacker
/// before it can attack again.
/// </description></item>
/// </list>
///
/// <para>
/// <b>Seeded, unlike the original.</b> MapleNecrocer constructs
/// <c>new Random()</c> unseeded inside <c>DoMove</c>, so its mobs never take the
/// same path twice and a question about them has no stable answer. Here the
/// generator is supplied, so the same seed replays exactly.
/// </para>
/// </remarks>
public sealed class MobPatrol
{
    // ---- roll distribution, Mob.cs:352-375: Random.Next(200)
    private const int RollRange = 200;
    private const int LeftBelow = 50;
    private const int RightBelow = 100;
    private const int StandBelow = 150;

    /// <summary>Frames between rolls. The client rolls every frame; this is slower.</summary>
    private const int RollEvery = 20;

    /// <summary>Default walk speed, Mob.cs:151.</summary>
    public const float DefaultMoveSpeed = 2f;

    /// <summary>Frames a hit animation locks the mob for, Mob.cs:439.</summary>
    private const int HitStunFrames = 20;

    /// <summary>Frames a dead mob stays down before respawning.</summary>
    public const int RespawnFrames = 600;

    private readonly FootholdPhysics _map;
    private readonly Random _rng;
    private readonly float _moveSpeed;
    private readonly int _patrolLeft;
    private readonly int _patrolRight;

    private FootholdPhysics.Foot _foothold;
    private float _x;
    private float _y;
    private int _sinceRoll;

    public MobPatrol(
        FootholdPhysics map,
        FootholdPhysics.Foot start,
        int seed,
        int patrolLeft = 0,
        int patrolRight = 0,
        float moveSpeed = DefaultMoveSpeed)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _rng = new Random(seed);
        _moveSpeed = moveSpeed <= 0 ? DefaultMoveSpeed : moveSpeed;
        // 0 is what the client reads for "unbounded", and a map that sets only one
        // of the two should not have the mob teleport to 0.
        _patrolLeft = patrolLeft;
        _patrolRight = patrolRight;

        _foothold = start;
        _x = start.X1;
        _y = start.Y1;
        Direction = _rng.Next(2) == 0 ? -1 : 1;
        Facing = Direction;
        FacingRight = Direction > 0;
    }

    /// <summary>Map position.</summary>
    public float X => _x;

    public float Y => _y;

    /// <summary>Current facing direction.</summary>
    public int Direction { get; private set; }

    /// <summary>Which way the sprite is drawn.</summary>
    public int Facing { get; private set; }

    /// <summary>True when the sprite faces right. Mob.cs:199 sets it from a coin.</summary>
    public bool FacingRight { get; private set; }

    /// <summary>The foothold being walked.</summary>
    public FootholdPhysics.Foot Foothold => _foothold;

    /// <summary>How many times this mob has turned around.</summary>
    public int Reversals { get; private set; }

    /// <summary>Whether it is still standing on a chain.</summary>
    public bool OnGround { get; private set; } = true;

    /// <summary>Why it left the chain, or null.</summary>
    public string? LeftGroundBecause { get; private set; }

    /// <summary>Whether it has noticed a character standing close enough to reach.</summary>
    public bool Engaged { get; private set; }

    /// <summary>Frames left before it can act again.</summary>
    public int Stun { get; private set; }

    /// <summary>Whether it is down. A dead mob does not patrol.</summary>
    public bool Dead { get; private set; }

    /// <summary>Frames until it returns, once dead.</summary>
    public int RespawnIn { get; private set; }

    /// <summary>Times it has been struck.</summary>
    public int HitsTaken { get; private set; }

    /// <summary>Times it has landed a hit.</summary>
    public int HitsLanded { get; private set; }

    /// <summary>
    /// Run one frame.
    /// </summary>
    /// <param name="characterX">
    /// A character's X, or null for none. Mob.cs compares facing rather than
    /// distance, so this is the whole engagement test.
    /// </param>
    /// <param name="characterY">A character's Y, or null.</param>
    public void Step(int? characterX = null, int? characterY = null)
    {
        if (Dead)
        {
            if (--RespawnIn <= 0) Respawn();
            return;
        }

        if (Stun > 0)
        {
            Stun--;
            return;
        }

        if (!OnGround)
        {
            LeftGroundBecause ??= "the mob walked past the end of its foothold chain";
            return;
        }

        if (_foothold.IsWall)
        {
            OnGround = false;
            LeftGroundBecause = "the mob spawned on a wall, which it cannot walk along";
            return;
        }

        // Mob.cs:445 — a mob struck from behind turns to face the attacker.
        if (HitsTaken > 0)
        {
            HitsTaken = 0;
            if (characterX is { } cx) Facing = cx >= _x ? 1 : -1;
        }

        Engaged = characterX is { } ex && Math.Abs(ex - _x) <= Reach;

        if (Engaged && characterX is { } tx)
        {
            // Face the character. Mob.cs compares FlipX with the player's to
            // decide whether an attack can land, so facing is the whole test.
            Facing = tx >= _x ? 1 : -1;
            FacingRight = Facing > 0;
            return;
        }

        if (++_sinceRoll >= RollEvery)
        {
            _sinceRoll = 0;
            Roll();
        }

        Move();
    }

    /// <summary>
    /// Take a hit from a character.
    /// </summary>
    /// <remarks>
    /// Mob.cs:803-819: a mob that can play "hit1" is staggered for the animation,
    /// turns to face the attacker, and its hit counter resets. HP is not modelled
    /// — the client hardcodes 2,000,000 and a single hit deals 500,000-1,050,000,
    /// so a real fight is four hits and nothing about it informs whether a MAP
    /// is sound.
    /// </remarks>
    public void TakeHit()
    {
        if (Dead)
            return;
        HitsTaken++;
        Stun = HitStunFrames;
    }

    /// <summary>How far a mob can reach a character. Reach is a facing test, not a box.</summary>
    private const int Reach = 60;

    private void Roll()
    {
        int roll = _rng.Next(RollRange);
        if (roll < LeftBelow)
            Direction = -1;
        else if (roll < RightBelow)
            Direction = 1;
        else if (roll < StandBelow)
            Direction = 0;
        // The top band is the client's jump. A patrol does not leap: it would
        // move the question "can a mob stand here" to a different one.

        if (Direction != 0)
        {
            Facing = Direction;
            FacingRight = Direction > 0;
        }
    }

    private void Move()
    {
        if (Direction == 0)
            return;

        // The client's motion: along the foothold's own direction.
        int angle = Angle256(
            Direction > 0 ? _foothold.X1 : _foothold.X2,
            Direction > 0 ? _foothold.Y1 : _foothold.Y2,
            Direction > 0 ? _foothold.X2 : _foothold.X1,
            Direction > 0 ? _foothold.Y2 : _foothold.Y1);

        _x += (float)(Sin256(angle) * _moveSpeed);
        _y -= (float)(Cos256(angle) * _moveSpeed);

        // Patrol bounds, Mob.cs:586 and :664. Both default to 0 meaning
        // "unbounded", so a bound only counts when the map actually set it.
        if (_patrolRight > _patrolLeft)
        {
            if (Direction > 0 && _x >= _patrolRight)
            {
                _x = _patrolRight;
                Turn();
                return;
            }
            if (Direction < 0 && _x <= _patrolLeft)
            {
                _x = _patrolLeft;
                Turn();
                return;
            }
        }

        bool past = Direction > 0 ? _x >= _foothold.X2 : _x <= _foothold.X1;
        if (!past)
            return;

        long nextId = Direction > 0 ? _foothold.Next : _foothold.Prev;
        FootholdPhysics.Foot? next = nextId == 0 ? null : FindById(nextId);

        // Mob.cs:566-577 — turn when there is no next foothold, or the next is a
        // wall. Both mean this is as far as it can walk.
        if (next is not { IsWall: false } step)
        {
            int edge = Direction > 0 ? _foothold.X2 : _foothold.X1;
            _x = edge;
            Turn();
            return;
        }

        _foothold = step;
        _x = Direction > 0 ? step.X1 : step.X2;
        _y = Direction > 0 ? step.Y1 : step.Y2;
    }

    private FootholdPhysics.Foot? FindById(long id)
    {
        foreach (FootholdPhysics.Foot f in _map.Footholds)
        {
            if (long.TryParse(f.Id, out long fid) && fid == id)
                return f;
        }
        return null;
    }

    private void Turn()
    {
        Direction = -Direction;
        Facing = Direction;
        FacingRight = Direction > 0;
        Reversals++;
    }

    /// <summary>Kill the mob. Mob.cs:821-824 plays the death animation.</summary>
    public void Kill()
    {
        if (Dead)
            return;
        Dead = true;
        RespawnIn = RespawnFrames;
        OnGround = false;
        LeftGroundBecause = null;
    }

    /// <summary>Put it back, as the client's respawn does.</summary>
    public void Respawn()
    {
        Dead = false;
        RespawnIn = 0;
        Stun = 0;
        OnGround = false;
        LeftGroundBecause = null;
    }

    private const float PiConv256 = -40.743665431f;

    private static int Angle256(int sx, int sy, int dx, int dy) =>
        (int)(Math.Atan2(dx - sx, dy - sy) * PiConv256) + 128;

    private static double Cos256(int i) => Math.Cos(i * Math.PI / 128.0);

    private static double Sin256(int i) => Cos256((i + 192) & 255);
}