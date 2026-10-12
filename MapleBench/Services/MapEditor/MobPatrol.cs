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

    /// <summary>
    /// Frames between rolls.
    /// </summary>
    /// <remarks>
    /// 1, because the client rolls every frame: <c>Mob.cs:351</c> constructs the
    /// generator and <c>:354</c> throws immediately, inside <c>DoMove</c>. A mob
    /// moving at 2px per frame crosses its own body length in well under the 20
    /// frames this used to wait, so it turned once per stretch of walking and the
    /// patrol had a rhythm the game does not.
    /// </remarks>
    private const int RollEvery = 1;

    /// <summary>Default walk speed, Mob.cs:151.</summary>
    public const float DefaultMoveSpeed = 2f;

    /// <summary>
    /// Frames a hit animation locks the mob for, Mob.cs:439.
    /// </summary>
    /// <remarks>
    /// The client's real duration is however long the mob's <c>hit1</c> animation
    /// runs (<c>Mob.cs:399-404</c>), which lives in the Mob archive rather than
    /// the map file. 20 is the fallback a caller can override.
    /// </remarks>
    private const int HitStunFrames = 20;

    /// <summary>How a mob moves, from its animation set. Mob.cs:166-175.</summary>
    public enum MobMoveType
    {
        /// <summary>Has a <c>move</c> node. Ignores rx0/rx1; turns at the chain end.</summary>
        Move,
        /// <summary>Has a <c>jump</c> node. Honours rx0/rx1 and leaps gaps.</summary>
        Jump,
        /// <summary>Has a <c>fly</c> node. Ignores footholds entirely; honours rx0/rx1.</summary>
        Fly,
    }

    /// <summary>The type of mob this patrol models.</summary>
    public MobMoveType MoveType { get; }

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
        float moveSpeed = DefaultMoveSpeed,
        MobMoveType moveType = MobMoveType.Move)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _rng = new Random(seed);
        _moveSpeed = moveSpeed <= 0 ? DefaultMoveSpeed : moveSpeed;
        // 0 is what the client reads for "unbounded", and a map that sets only one
        // of the two should not have the mob teleport to 0.
        _patrolLeft = patrolLeft;
        _patrolRight = patrolRight;
        MoveType = moveType;

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

    /// <summary>
    /// Whether this mob is currently redirecting toward an attacker.
    /// </summary>
    /// <remarks>
    /// Not a distance test. A mob notices a character only after being hit
    /// (<c>Mob.cs:434-460</c>) — there is no aggro radius anywhere in the client.
    /// This is true for the tick in which that reaction runs and false otherwise.
    /// </summary>
    public bool Engaged { get; private set; }

    /// <summary>Frames left before it can act again.</summary>
    public int Stun { get; private set; }

    /// <summary>Whether it is down. A dead mob does not patrol.</summary>
    public bool Dead { get; private set; }

    /// <summary>
    /// Always zero: the client has no respawn, so there is nothing to count down.
    /// </summary>
    /// <remarks>
    /// Kept so callers reading the field still compile; <c>Mob.cs:443-476</c> and
    /// <c>SpriteEngine.cs:486-492</c> free the sprite and never recreate it.
    /// </remarks>
    public int RespawnIn => 0;

    /// <summary>Times it has been struck.</summary>
    public int HitsTaken { get; private set; }

    /// <summary>Times it has landed a hit.</summary>
    public int HitsLanded { get; private set; }

    /// <summary>
    /// Run one frame.
    /// </summary>
    /// <param name="characterX">
    /// A character's X, or null for none. The client has no proximity test — this
    /// is used only by the post-hit reaction (Mob.cs:434-460).
    /// </param>
    /// <param name="characterY">A character's Y, or null.</param>
    public void Step(int? characterX = null, int? characterY = null)
    {
        // A dead mob is gone. Mob.cs:443-476 plays the die animation and then
        // SpriteEngine.cs:486-492 puts the sprite on DeadList and frees it. There
        // is no timer and no respawn — the map keeps its hole until it reloads.
        // This used to bring it back after 600 frames, which is a fact about
        // nothing in the game.
        if (Dead)
            return;

        // The stun is the hit ANIMATION, not a pause before the mob reacts.
        // Mob.cs:424 stops movement while GetHit1 is set, but :434-460 runs the
        // turn-and-redirect in the same file — so the reaction happens on the
        // tick the animation ends, and Engaged reports exactly that tick.
        if (Stun > 0)
        {
            Stun--;
            return;
        }

        // Mob.cs:445 — a mob struck from behind turns to face the attacker.
        if (HitsTaken > 0)
        {
            HitsTaken = 0;
            Engaged = true;
            if (characterX is { } cx)
            {
                Facing = cx >= _x ? 1 : -1;
                FacingRight = Facing > 0;
                // Mob.cs:434-460 — the attacker also REDIRECTS the mob, not just
                // its sprite. The hit makes it walk toward whoever struck it.
                if (Facing != 0)
                {
                    Direction = Facing;
                    return;
                }
            }
            Engaged = false;
            return;
        }

        Engaged = false;

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


        // There is no aggro radius. Mob.cs:350-375 decides direction from
        // Random.Next(200) every frame and never reads Game.Player.X; the only
        // player-dependent branch in the whole file is the hit reaction above.
        // The 60px box this replaced made every mob appear to notice a
        // character that the game has no mechanism for noticing.

        if (++_sinceRoll >= RollEvery)
            Roll();

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

        // Patrol bounds, Mob.cs:586 and :664 — but ONLY inside the Jump and Fly
        // branches. The Move branch (Mob.cs:566-582) clamps to the foothold's own
        // edge and never reads rx0/rx1 at all, so for the most common mob type
        // these fields are dead. Applying them to a walker made it turn at a
        // coordinate the game would have walked straight past.
        bool honoursBounds = MoveType is MobMoveType.Jump or MobMoveType.Fly;
        if (honoursBounds && _patrolRight > _patrolLeft)
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
        OnGround = false;
        LeftGroundBecause = null;
    }

    private const float PiConv256 = -40.743665431f;

    private static int Angle256(int sx, int sy, int dx, int dy) =>
        (int)(Math.Atan2(dx - sx, dy - sy) * PiConv256) + 128;

    private static double Cos256(int i) => Math.Cos(i * Math.PI / 128.0);

    private static double Sin256(int i) => Cos256((i + 192) & 255);
}