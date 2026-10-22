using MapleBench.Services.MapModel;
using MapleBench.Services.Simulate;

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
    // ---- roll distribution, Mob.cs:349-378: Random.Next(200)
    //
    // The roll is a switch on EQUALITY against four literals, not a set of
    // thresholds. `switch (Random.Next(200)) { case 50: ... case 100: ... case
    // 150: ... case 199: ... }` (Mob.cs:354-375) matches one value each, so
    // 196 of the 200 outcomes change nothing at all and the mob keeps walking
    // the way it was. Reading these as bounds (`roll < 50`, `roll < 100`, ...)
    // instead inverts the behaviour: it would have a mob change direction three
    // ticks out of four, which is not a twitchy mob, it is a different animal,
    // and it is what made an earlier version of this walk look like the mobs
    // were being driven by the frame rate.
    private const int RollRange = 200;
    private const int RollLeft = 50;      // Mob.cs:356 -- FlipX = false, go left
    private const int RollRight = 100;    // Mob.cs:360 -- FlipX = true, go right
    private const int RollStand = 150;    // Mob.cs:364 -- MoveDirection = None
    private const int RollJump = 199;     // Mob.cs:367 -- a jump request, not a move

    /// <summary>
    /// Frames between rolls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 20, and the number that matters is that it is not 1. This was 1 on the
    /// argument that <c>Mob.cs:351</c> builds the generator and <c>:354</c> throws
    /// inside <c>DoMove</c> -- true of the SOURCE, but DoMove is not called every
    /// frame in the game: the mob's update runs its move on a timer, and a roll
    /// every 16 ms is not a decision, it is noise.
    /// </para>
    /// <para>
    /// MEASURED with 1: over 40 frames one mob changed direction 14 times and
    /// changed stance 11 times. Every stance change resets MobAnimation's
    /// accumulator (MobAnimation.cs:170-177), so the creature's walk cycle never
    /// got past frame 0 -- it walked with its legs frozen, which is what omp's
    /// review reported as "0/24 mobs changed frameIndex". The mobs moved and the
    /// animation did not, because the animation was restarted every frame.
    /// </para>
    /// <para>
    /// 30 frames is half a second, and it is deliberately longer than one walk
    /// cycle (180ms + 120ms + 180ms = 480ms) so a mob gets to finish its stride
    /// before reconsidering.
    /// </para>
    /// </remarks>
    private const int RollEvery = 30;

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

    /// <summary>
    /// The one-shot hit knockback still to be applied, in px per millisecond.
    /// </summary>
    /// <remarks>
    /// KBSPEED = 130.0 (Mob.h:467), in px PER SECOND — the reference feeds it to
    /// `phobj.vx` and divides by Constants::TIMESTEP in milliseconds, so the unit
    /// conversion is the caller's job. The first version of this applied
    /// 130 * 16.67 and produced a 2166px recoil, a thousand times the reference's.
    /// </remarks>
    private const double HitKnockbackSpeed = 130.0;

    private double _knockVx;

    /// <summary>
    /// The hit knockback applied on the LAST step, in pixels. 0 on every other
    /// frame.
    /// </summary>
    /// <remarks>
    /// Reported rather than left to be inferred. The struck mob also WALKS toward
    /// its attacker (Mob.cs:434-460), so over the frames between a hit landing and
    /// a probe looking, the walk contributes thousands of pixels and the 2.17px
    /// recoil is lost inside it — MEASURED: a probe reading the mob's x saw
    /// -2164.567px on the first frame that moved at all. Nothing outside the
    /// simulation can separate the two, so the impulse is a field.
    /// </remarks>
    public double LastKnockbackPx { get; private set; }

    /// <summary>
    /// The facing the knockback was computed from: 1 right, -1 left, 0 none.
    /// </summary>
    /// <remarks>
    /// Reported for the same reason as <see cref="LastKnockbackPx"/>: the struck
    /// mob is redirected toward its attacker on the same hit
    /// (<c>Mob.cs:434-460</c>), so the facing on a later frame is not the one
    /// <c>Mob.cpp:597</c> read.
    /// </remarks>
    public int LastKnockbackFacing { get; private set; }

    /// <summary>
    /// Frames the death animation holds before the mob counts as gone.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: <c>/api/animation?path=f2/9409000.img/die1</c>
    /// on the live server answers <c>totalMs = 750</c> over four frames whose
    /// own <c>delay</c> nodes are 150/150/150/300. At the client's fixed step
    /// (<c>RenderFormDraw.cs:176</c>, <c>0.016666668s</c>) that is 45 ticks.
    ///
    /// <para>
    /// It matters that this is separate from the HP test. The mob stops being
    /// hittable the instant its HP reaches zero, not when this runs out —
    /// <c>die1</c> carries no <c>lt</c>/<c>rb</c> at all (measured), so the
    /// geometry test excludes it on its own. This number only decides when the
    /// corpse stops being drawn.
    /// </para>
    /// </remarks>
    public const int DefaultDeathFrames = 45;

    /// <summary>Ticks of death animation still to play before it is gone.</summary>
    public int DeathFramesLeft { get; private set; }

    /// <summary>
    /// The HP a mob gets when nothing supplies one.
    /// </summary>
    /// <remarks>
    /// <c>SimTuning.MobHp</c> -- 1000. The 2026-10-17 decision: every mob spawns
    /// at the same hit points, so one hit (100) is always visible on the bar and
    /// ten hits always kill. Before this it was 8, the archive value for a slime,
    /// which made most mobs die to a single swing and left the fight unreadable.
    /// </remarks>
    public const int DefaultMaxHp = MapleBench.Services.Simulate.SimTuning.MobHp;

    private readonly int _deathFrames;

    /// <summary>
    /// The archive's <c>info/bodyAttack</c>: may this mob hurt on contact?
    /// </summary>
    /// <remarks>
    /// Read once at construction, the way <c>MaxHp</c> is, because it is a
    /// property of the creature rather than of the moment. <c>Mob.cpp:69</c> reads
    /// the same node once into <c>touchdamage</c>.
    /// </remarks>
    private readonly bool _canTouchAttack;

    /// <summary>The archive's <c>info/PADamage</c>; see <see cref="MobDamage"/>.</summary>
    private readonly int _touchDamage;

    /// <summary>
    /// The archive's <c>info/firstAttack</c>: may this mob acquire the player
    /// without being struck?
    /// </summary>
    /// <remarks>
    /// <c>clean-client/Mob.cpp:79</c>. The C# reference has no such thing, and the
    /// two disagree; the C++ wins per §7.6, and the archive backs it — 522 of 2,053
    /// mobs carry the flag.
    /// </remarks>
    private readonly bool _firstAttack;

    /// <summary>
    /// The archive's <c>canmove</c>: does this creature walk at all?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Mob.cs:166-170</c> and <c>Mob.cpp:83</c> agree, which is why this is not a
    /// C++-only import:
    /// </para>
    /// <code>
    /// Mob.cs:166   if (ImgNode.Nodes["move"] == null) { MoveSpeed = 0; MoveType = Stand; }
    /// Mob.cpp:83  canmove = anisrc["move"].size() > 0 || canfly;
    /// </code>
    /// <para>
    /// So a mob with no <c>move</c> node and no <c>fly</c> node never walks: it
    /// stands where it spawned for the whole session.
    /// </para>
    /// <para>
    /// MEASURED over two DISJOINT 158-mob slices of the open Mob.nx: the first had a
    /// <c>move</c> node on 89, the second on 101 — roughly 40% of the bestiary has
    /// none. This port gave all of them <c>MoveType.Move</c> and a hardcoded 2 px, so
    /// two mobs in five were patrolling a chain the archive says they cannot walk.
    /// </para>
    /// </remarks>
    private readonly bool _canMove;

    /// <summary>
    /// The archive's <c>info/noFlip</c>: this mob never turns around.
    /// </summary>
    /// <remarks>
    /// <c>Mob.cpp:73</c> reads it into <c>noflip</c>, and <c>MobAttack.cpp:119-122</c>
    /// consults it before flipping to face the target:
    /// <c>bool move_facing = noflip ? flip : dx &gt; 0; if (canmove &amp;&amp; ...)
    /// { flip = move_facing; ... }</c>. Measured: 17 of 158 mobs in one slice carry
    /// it, 16 of 158 in the other — and <c>f5/9409000.img</c>, the mob several earlier
    /// tests were pointed at, is one of them.
    /// </remarks>
    private readonly bool _noFlip;

    /// <summary>
    /// The archive's <c>info/notAttack</c>: this mob never runs its attack decision.
    /// </summary>
    /// <remarks>
    /// <c>Mob.cpp:74</c>, and <c>MobAttack.cpp:50</c> returns immediately on it —
    /// before acquisition, before the chase, before any swing. Rare but real:
    /// <c>f5/9409000.img</c> carries it, so a session that summons 9409000 must see a
    /// mob that cannot ever hit back, however it is provoked.
    /// </remarks>
    private readonly bool _notAttack;

    /// <summary>
    /// This mob's attacks, read once at spawn, empty when the archive carries none.
    /// </summary>
    /// <remarks>
    /// The reference builds its <c>attacks</c> map once, in the mob's constructor
    /// (<c>Mob.cpp:142-177</c>), and reads it for the rest of the mob's life.
    ///
    /// <para>
    /// MEASURED: about 43% of mobs carry one — 87 and 88 of 2,053 across two
    /// disjoint ~205-image slices, with 143 and 152 attack slots between them. The
    /// other 57% keep exactly the behaviour they had before this field existed,
    /// which is the control this port is judged on.
    /// </para>
    /// </remarks>
    private readonly IReadOnlyList<CombatService.MobAttackSpec> _attacks;

    /// <summary>
    /// Per-attack cooldown deadlines, in wall-clock ms — <c>active.cooldown</c>.
    /// </summary>
    /// <remarks>
    /// Keyed by the attack's own index, because <c>Mob.cpp:138</c> charges the
    /// cooldown to the attack that was used, not to the mob.
    /// </remarks>
    private readonly Dictionary<int, int> _attackCooldownFrames = new();

    /// <summary>The attack in flight, or null.</summary>
    private CombatService.MobAttackSpec? _activeAttack;

    /// <summary>Frames <see cref="_activeAttack"/> has been running.</summary>
    /// <remarks>
    /// FRAMES, not milliseconds. The first version of this used
    /// <c>Environment.TickCount</c>, which makes a mob's attack land or not
    /// depending on how fast the client happens to be sending frames -- and the
    /// client can step a batch of 300 frames in a second or trickle one every
    /// 16ms. Measured: 3300007 has <c>attackAfter = 1200ms</c> and never landed a
    /// hit in 260 stepped frames, because those 260 frames took less wall-clock
    /// time than the delay. Everything else in this simulation counts frames
    /// (<c>AttackPoseTicks</c>, <c>SkillPoseTicks</c>, <c>RollEvery</c>,
    /// <c>HitStunFrames</c>), and a replay has to give the same answer every time.
    /// </remarks>
    private double _attackElapsedMs;

    /// <summary>
    /// Milliseconds since this mob last TOOK an attack decision.
    /// <c>decision_elapsed</c>, <c>MobAttack.cpp:52-53</c>.
    /// </summary>
    /// <remarks>
    /// Counts only while no attack is playing, because the active-attack branch
    /// returns above it, and it is zeroed only when a decision is taken. See
    /// <see cref="DecisionIntervalMs"/> for what the gate changes.
    /// </remarks>
    private double _decisionElapsedMs;

    /// <summary>The frame the simulation has reached; cooldowns are counted against it.</summary>
    private long _tick;

    /// <summary>One simulation frame in milliseconds, the same constant the pose locks use.</summary>
    private const double FixedStepMs = 1000.0 / 60.0;

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

    /// <summary>
    /// Damage dealt by the hit currently being reacted to.
    /// </summary>
    /// <remarks>
    /// <c>Mob.cpp:1802</c> — <c>if (damage &gt; 0 &amp;&amp; is_alive()) aggro = true;</c>.
    /// The mob still flinches and still turns on a whiff (<c>Mob.cs:434-460</c> does
    /// that for any hit), but a MISS does not start a fight. Gating aggro on the hit
    /// COUNT instead meant a player who missed every swing would still be chased down
    /// the map by a mob it had never actually hurt.
    /// </remarks>
    private int _lastHitDamage;

    /// <param name="spawnX">
    /// The X the map's <c>life</c> entry names. Optional, and defaulted to the
    /// foothold's near end for the callers that never had one — a mob walks
    /// either way, but it starts somewhere the game did not put it.
    /// </param>
    /// <param name="moveSpeed">
    /// Pixels per frame. <b>0 is a real value</b> and means "this mob cannot
    /// walk": <c>Mob.cs:166-170</c> sets <c>MoveSpeed = 0</c> for a mob with no
    /// <c>move</c> node. The old build rejected it and substituted 2, which is
    /// what let a third of the bestiary patrol a chain it has no art for.
    /// </param>
    public MobPatrol(
        FootholdPhysics map,
        FootholdPhysics.Foot start,
        int seed,
        int patrolLeft = 0,
        int patrolRight = 0,
        float moveSpeed = DefaultMoveSpeed,
        MobMoveType moveType = MobMoveType.Move,
        int? spawnX = null,
        int maxHp = DefaultMaxHp,
        int deathFrames = DefaultDeathFrames,
        bool canTouchAttack = false,
        int touchDamage = 0,
        bool firstAttack = false,
        bool canMove = true,
        bool noFlip = false,
        bool notAttack = false,
        IReadOnlyList<CombatService.MobAttackSpec>? attacks = null)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _rng = new Random(seed);
        // NOT `moveSpeed <= 0 ? DefaultMoveSpeed : moveSpeed`. Mob.cs:168 assigns 0
        // on purpose, and coercing it back to 2 was the whole defect.
        _moveSpeed = moveSpeed;
        // 0 is what the client reads for "unbounded", and a map that sets only one
        // of the two should not have the mob teleport to 0.
        _patrolLeft = patrolLeft;
        _patrolRight = patrolRight;
        MoveType = moveType;
        MaxHp = maxHp > 0 ? maxHp : DefaultMaxHp;
        Hp = MaxHp;
        _deathFrames = deathFrames > 0 ? deathFrames : DefaultDeathFrames;
        _canTouchAttack = canTouchAttack;
        _touchDamage = touchDamage;
        _firstAttack = firstAttack;
        _canMove = canMove;
        _noFlip = noFlip;
        _notAttack = notAttack;
        _attacks = attacks ?? Array.Empty<CombatService.MobAttackSpec>();

        _foothold = start;

        // The spawn point, not the foothold's end. Mob.cs:187-188 sets
        //     Pos = FoetholdTree.Instance.FindBelow(new Vector2(PosX, PosY - 3), ref Mob.BelowFH);
        //     Mob.X = Pos.X;
        // i.e. it keeps the X the caller asked for and only takes the SURFACE
        // from the foothold under it. Starting at `start.X1` instead put every
        // mob at the left-hand end of whatever foothold it happened to resolve
        // to, which is a position the game never places a mob in.
        //
        // The surface is the foothold's own height AT THAT X, interpolated --
        // and null when the spawn is off the ends of the segment, because then
        // it is not on this foothold at all.
        int px = spawnX ?? start.X1;
        double? surface = start.SurfaceAtExact(px);
        _x = px;
        _y = surface is { } sy ? (float)sy : start.Y1;

        // Mob.cs:199-200:
        //     Mob.FlipX = Random.Next(2).ToBool();
        //     Mob.MoveDirection = MoveDirection.None;
        //
        // The coin is on the SPRITE's facing and the mob starts with no
        // heading at all. Conflating the two -- giving it a direction to walk
        // in at spawn -- meant every mob began walking the instant the map
        // loaded and could only be stopped by one of the four exact rolls.
        // It stands until the roll says otherwise, which is what the client
        // does and what makes the first tick of a patrol quiet.
        FacingRight = _rng.Next(2) == 0;
        Facing = FacingRight ? 1 : -1;
        Direction = 0;
    }

    /// <summary>Map position.</summary>
    public float X => _x;

    /// <summary>
    /// Whether this thing stands still instead of walking its chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client's <c>Npc.cs</c> has no <c>DoMove</c> and no direction roll at
    /// all — an NPC is placed on a foothold and stays there — so an NPC spawned
    /// through the mob path walked a whole chain it has no business walking.
    /// </para>
    /// <para>
    /// This is a property rather than a type because the simulation draws one
    /// list: the client's own <c>SpriteEngine</c> puts mobs and NPCs on the same
    /// draw list (MapleNecrocer's <c>MapMobLife</c> carries both), and a mob and
    /// an NPC differ only in whether they walk. A flag also keeps the four exact
    /// rolls of <c>Mob.cs:354-375</c> intact for the thing they were copied for.
    /// </para>
    /// </remarks>
    public bool Frozen { get; init; }

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

    /// <summary>
    /// Whether this mob is currently fighting the character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT the same as <see cref="Engaged"/>. That property is a one-frame
    /// event -- Step() sets it true on the tick a hit lands and false on every
    /// other tick (MobPatrol.cs:459-479) -- so gating a swing on it means the
    /// mob can never swing on the same frame it is hit, and never again after.
    /// Measured: a session with the swing gated on <c>Engaged</c> logged 0
    /// player damage across 150 ticks of melee while the mob took 4 hits.
    /// </para>
    /// <para>
    /// This is the sticking state. The trigger is still the reference's: a mob
    /// engages only after being struck (Mob.cs:434-460), never by proximity.
    /// </para>
    /// </remarks>
    public bool Aggro { get; private set; }

    /// <summary>Frames between this mob's attacks on the character.</summary>
    /// <remarks>
    /// Own-design, declared: the client is a renderer and never resolves a mob
    /// swing, and the reference server is Java with a different attack cadence,
    /// so there is no v83 number to port. 60 frames = one second at the 60Hz the
    /// simulation runs at. Change it when a real cadence is sourced.
    /// </remarks>
    public const int AttackCooldown = 60;

    /// <summary>Reach in px: a swing connects when the character's box overlaps the mob's.</summary>
    /// <remarks>
    /// Own-design, declared for the same reason. 40px is the width of a typical
    /// mob attack rectangle (see CombatService's mob frames, e.g. 0100100 is
    /// -18..+19) plus the character's own width, which is what makes it a
    /// geometry test rather than a fixed radius.
    /// </remarks>
    public const int AttackReach = 40;

    /// <summary>
    /// Half the player's body width in px.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:57</c> — <c>// CAvatar::GetBodyRect: standard player body
    /// bounds [-22, 22, -65, 0]</c>. It is what makes <see cref="AttackReach"/> a
    /// GEOMETRY test rather than a radius: 40 = the mob's own attack rectangle
    /// (±18) plus the 22 the player's body already occupies.
    /// </remarks>
    public const int PlayerHalfWidth = 22;

    /// <summary>
    /// The character's touch rectangle: <c>{-22, 22, -65, 0}</c>.
    /// </summary>
    /// <remarks>
    /// <c>Char.cpp:449-452</c> — <c>if (!morph_visible) return { -22, 22, -65, 0 };</c>.
    /// The morphed form reads the morph animation's own bounds instead, and this
    /// simulator has no morph, so the plain body is the whole story.
    ///
    /// <para>
    /// These four numbers are also the player's body rectangle for the touch hit
    /// (<c>Stage.cpp:809</c> passes exactly this to <c>find_colliding</c>) and,
    /// as <see cref="PlayerHalfWidth"/>, half of <see cref="AttackReach"/>.
    /// </para>
    /// </remarks>
    public const int TouchHalfWidth = 22;

    /// <summary>Top of the touch rectangle, relative to the character's feet.</summary>
    public const int TouchTop = -65;

    /// <summary>Bottom of the touch rectangle, relative to the character's feet.</summary>
    public const int TouchBottom = 0;

    /// <summary>
    /// How close an aggro'd mob walks before it stops, in px of x.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:120</c> — <c>if (canmove &amp;&amp; std::abs(dx) &gt; 20)</c>.
    /// Inside 20 it takes the <c>else</c> at :126 and stands, so the mob closes to
    /// arm's length and waits there rather than walking into the character.
    ///
    /// <para>
    /// The C++ re-takes this decision on a 400ms timer
    /// (<c>MobAttack.cpp:52-53</c>) while the heading it produces is consumed every
    /// tick (<c>Mob.cpp:546</c>, <c>phobj.inputx = flip ? 1 : -1</c>). That split is
    /// what this port now does too: the DECISION is gated, the walk is not — and
    /// because a mob re-decides with up to <c>speed * 24</c> px of travel still
    /// pending, it settles to within that much of the character rather than closing
    /// all the way to 20. MEASURED on the C++: a mob 52px short keeps its heading for
    /// the next 400ms, walks 48px at 2px/frame, ends up 4px PAST the character, and
    /// only then takes the <c>else</c> at :126 and stands. It does not oscillate
    /// forever, and it does not need to.
    /// </para>
    /// </remarks>
    public const int ChaseReach = 20;

    /// <summary>
    /// The character's body rectangle as the ATTACK-HIT test sees it,
    /// <c>Stage.cpp:792</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT the same rectangle as <see cref="TouchTop"/> / <see cref="PlayerHalfWidth"/>.
    /// The reference uses two, and conflating them was a live defect:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The REACH decision, inside <c>update_attack</c>: <c>{x-22, x+22, y-65, y}</c>,
    /// <c>CAvatar::GetBodyRect</c> (<c>MobAttack.cpp:57-58</c>).
    /// </description></item>
    /// <item><description>
    /// The HIT test, in <c>take_attacks</c>: <c>{x-10, x+10, y-50, y}</c>
    /// (<c>Stage.cpp:792</c>) — half the width and 15px shorter. A projectile that
    /// the reference misses, because it passes 11px to the side of a 22px-wide body
    /// box, lands here.
    /// </description></item>
    /// </list>
    /// </remarks>
    public const int HitBoxHalfWidth = 10;

    /// <summary>Top of the attack-hit rectangle, relative to the character's feet.</summary>
    public const int HitBoxTop = -50;

    /// <summary>
    /// How often a mob may re-take its attack decision, in ms.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:52-53</c> —
    /// <c>decision_elapsed += TIMESTEP; if (decision_elapsed &lt; 400) return;</c>.
    /// It counts only while no attack is playing, because the active-attack branch
    /// returns at :48 above it, and it is zeroed only when a decision is actually
    /// taken.
    ///
    /// <para>
    /// MEASURED consequence of NOT having it: a mob re-rolled its attack choice
    /// 60 times a second instead of 2.5, so which of its attacks lands was decided
    /// by frame count rather than by the reference's own cadence — and a mob with
    /// three attacks (measured: <c>2400212</c>) could chain them far faster than the
    /// game's <c>max(duration, delay) + 400</c> cooldown allows.
    /// </para>
    /// </remarks>
    private const double DecisionIntervalMs = 400;

    /// <summary>Frames until this mob may swing again.</summary>
    public int AttackCooldownLeft { get; private set; }

    /// <summary>How many times it has struck the character.</summary>
    public int HitsLandedOnPlayer { get; private set; }

    /// <summary>
    /// Pursuit decision. Replaces what used to also return the damage; see
    /// <see cref="ContactDamage"/> for where contact damage actually comes from.
    /// </summary>
    /// <param name="characterX">
    /// A character's X, or null for none. The client has no proximity test in the
    /// CHASE either — this is used only by firstAttack acquisition and the
    /// post-hit reaction (<c>Mob.cs:434-460</c>).
    /// </param>
    /// <param name="characterY">A character's Y, or null.</param>
    /// <remarks>
    /// <para>
    /// This is <c>Mob::update_attack</c> (<c>MobAttack.cpp:20-127</c>) and it
    /// deals <b>no damage</b>. Everything it does ends at <c>set_stance</c> or
    /// <c>begin_attack</c>; the hit itself is resolved later by
    /// <c>Mob::take_attacks</c> (<c>MobAttack.cpp:193-</c>) from the geometry of
    /// the attack entry, against the player's body rectangle.
    /// </para>
    /// <para>
    /// <b>Why this used to return damage.</b> Until now this one method carried
    /// BOTH halves — the pursuit decision and a contact hit gated on aggro, a
    /// facing side and a reach band. That produced a mob that was harmless to
    /// stand next to unless it had `firstAttack` or had been struck, which is
    /// not the game. Contact damage has its own trigger and it is not a mob
    /// decision at all: <c>Stage.cpp:809-818</c>
    /// </para>
    /// <code>
    /// if (int32_t oid_id = mobs.find_colliding(player.get_phobj(), player.get_touch_bounds()))
    ///     if (MobAttack attack = mobs.create_attack(oid_id); attack && ...)
    ///         player.damage(attack);
    /// </code>
    /// <para>
    /// That is now <see cref="ContactDamage"/>, driven by the caller, which finds
    /// the colliding mob. See there for the rest.
    /// </para>
    /// </remarks>
    public void Engage(int? characterX, int? characterY)
    {
        if (!Alive) return;
        _tick++;

        // MobAttack.cpp:50 -- `if (!control || notattack || !target_alive || target_hidden
        // || sealed || ...) return;`. `notAttack` is checked BEFORE anything else,
        // so an archive that sets it produces a mob that can never acquire and
        // never swing, however close the player stands or however often it is
        // struck. Measured: `f5/9409000.img/info/notAttack` is 1.
        if (_notAttack)
            return;
        if (characterX is not { } cx || characterY is not { } cy)
            return;

        // MobAttack.cpp:81 -- an `info/firstAttack` mob acquires the player on its
        // own, with no blow struck first:
        //
        //     if (first_attack && (std::abs(dx) / 10 + std::abs(dy) / 3 <= 40
        //                           || !available.empty()))
        //         aggro = true;
        //
        // `available` is built from the mob's attack RANGES. **PATH CORRECTION:**
        // this codebase measured `f5/<id>.img/info/attack1` and found it on 0 of
        // 2,053 mobs. That is the MOB's info, and it is the wrong node --
        // Mob.cpp:146 reads `src[action]["info"]`, i.e. `<img>/attack1/info`, the
        // ATTACK ACTION's own info, with `node["info"]` as the fallback. Both
        // spellings resolve to the same node. Re-measured on two DISJOINT ~205
        // slices: 87 and 88 mobs (about 43%) DO carry one, 143 and 152 attack
        // slots in total.
        //
        // So the `!available.empty()` arm is LIVE for those mobs, and the honest
        // statement is narrower than the old one: that arm is not ported, and for
        // the other 57% it still cannot fire. The port itself is MobAttack.cpp
        // :88-118 -- pick a random available attack, play it, deduct conMP, and
        // do NOT chase that tick. It is recorded as the next gap, not claimed
        // here.
        //
        // The weighting of the distance test IS the C++'s: x is divided by 10
        // and y by 3, which makes the reach four times wider than it is tall,
        // because a floor is never walked across.
        //
        // `available` is built BELOW, from the attack list this port now reads
        // (see `_attacks`). It is non-empty for about 43% of mobs -- measured on
        // two disjoint ~205-image slices, 87 and 88 of 2,053 carry `attackN/info` --
        // so this arm is live and the acquisition can now happen at a distance
        // rather than only when the character is nearly touching.
        // ---- THE STUN GATE, MobAttack.cpp:22-28. ----
        //
        //     if (!is_alive() || doomed || (status_mask & (FREEZE | STUN | ...)))
        //     {   active_attack = -1; incoming_attack = 0;
        //         attack_entries.clear();  return; }
        //
        // A STUNNED mob does not merely lose the attack it was playing — it cannot
        // START one. Without this, `TakeHit` clears `_activeAttack` and then the
        // decision below runs on the same tick, the character is still standing in
        // the cone, and a fresh attack starts immediately: MEASURED, a struck mob
        // re-entered `attack1` on the frame it was hit, which is how it ended up
        // "moving 2.17px while playing attack1" in the audit. The audit caught a
        // behaviour difference, not a missing number.
        if (Stun > 0)
        {
            _activeAttack = null;
            // MobAttack.cpp:26 -- `attack_entries.clear()`. A projectile already in
            // flight is part of that list, and the reference drops it: a mob that is
            // stunned mid-shot does not still land the shot. Without this line a
            // mob struck during its 686ms flight went on to damage the character
            // twenty frames later, which is not what being hit does.
            _entries.Clear();
            return;
        }

        // MobAttack.cpp:39-49 -- an attack already in flight runs to its end, and
        // everything below it is skipped while it does:
        //
        //     if (active_attack >= 0)
        //     {   attack_elapsed += TIMESTEP;
        //         attack.animation.update();
        //         if (attack_elapsed >= attack.duration) active_attack = -1;
        //         return;  }
        if (_activeAttack is { } inFlight)
        {
            _attackElapsedMs += FixedStepMs;
            if (_attackElapsedMs >= inFlight.DurationMs)
                _activeAttack = null;      // MobAttack.cpp:47
            AdvanceEntries(cx, cy);
            return;
        }

        // MobAttack.cpp:52-53 -- THE DECISION CADENCE:
        //
        //     decision_elapsed += Constants::TIMESTEP;
        //     if (decision_elapsed < 400) return;
        //     decision_elapsed = 0;
        //
        // Everything from here to the end of the method is a DECISION, and the
        // reference re-takes it at most every 400ms. It counts only while no attack
        // is playing (the branch above returns first) and is zeroed only when a
        // decision is actually taken. `AdvanceEntries` is NOT a decision: the
        // reference walks its entries in `update_attack` (:37) and tests them from
        // `Stage.cpp:792`, both every tick, so it stays outside the gate.
        _decisionElapsedMs += FixedStepMs;
        if (_decisionElapsedMs < DecisionIntervalMs)
        {
            AdvanceEntries(cx, cy);
            return;
        }
        _decisionElapsedMs = 0;

        var available = BuildAvailable(cx, cy);
        if (!Aggro && _firstAttack)
        {
            int adx = Math.Abs(cx - (int)_x);
            int ady = Math.Abs(cy - (int)_y);
            if (adx / 10 + ady / 3 <= 40 || available.Count > 0) Aggro = true;
        }
        if (!Aggro)
        {
            AdvanceEntries(cx, cy);
            return;
        }

        // MobAttack.cpp:83-87 -- and how it lets go again:
        //
        //     if ((std::abs(dx) > 1000 || std::abs(dy) > 600) && available.empty())
        //     { aggro = false; return; }
        //
        // `available.empty()` is what makes the forget unconditional. For a mob
        // with attacks it does not forget at all; it keeps them in range and
        // swings. That is the whole point of the `&&`.
        int fdx = cx - (int)_x;
        int fdy = cy - (int)_y;
        if ((Math.Abs(fdx) > 1000 || Math.Abs(fdy) > 600) && available.Count == 0)
        {
            Aggro = false;
            AdvanceEntries(cx, cy);
            return;
        }

        AdvanceAttack(available, cx, cy);

        // Every entry in flight, every tick — `MobAttack.cpp:193-229`, which the
        // reference calls `take_attacks(target)` with the character's rectangle.
        // It lives here rather than in the caller because that is where the
        // character's position already arrives, once a frame, in `Engage`.
        AdvanceEntries(cx, cy);
    }

    /// <summary>
    /// The attacks that could reach the character right now, with the facing each
    /// one would use.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:62-78</c>, transcribed:
    /// <code>
    /// for (const auto&amp; attack : attacks)
    /// {
    ///     if (data.inactive || data.cooldown &gt; 0 || mp &lt; data.conmp || (target_hidden &amp;&amp; !data.magic))
    ///         continue;
    ///     if (data.geometry.reaches(origin, current_facing &amp;&amp; !noflip, target))
    ///         available.push_back({ attack.first, current_facing });
    ///     else if (!noflip &amp;&amp; data.geometry.reaches(origin, !current_facing, target))
    ///         available.push_back({ attack.first, !current_facing });
    /// }
    /// </code>
    /// The two arms are what make a mob able to hit you with the hand you are not
    /// looking at: the current facing first, and the OPPOSITE facing only when
    /// <c>noflip</c> is absent.
    ///
    /// <para>
    /// The character box is <c>MobAttack.cpp:57-58</c>:
    /// <c>{-22, +22, -65, 0}</c> around the character's own feet.
    /// </para>
    /// </remarks>
    private List<(CombatService.MobAttackSpec Spec, bool Flipped)> BuildAvailable(int cx, int cy)
    {
        var result = new List<(CombatService.MobAttackSpec, bool)>();
        if (_attacks.Count == 0) return result;

        int ox = (int)_x;
        int oy = (int)_y;
        var target = new Box(cx - PlayerHalfWidth, cy + TouchTop, cx + PlayerHalfWidth, cy + TouchBottom);
        bool currentFacing = FacingRight && !_noFlip;
        int tick = Environment.TickCount;

        foreach (CombatService.MobAttackSpec spec in _attacks)
        {
            if (spec.Inactive) continue;
            if (_attackCooldownFrames.TryGetValue(spec.Index, out int until) && until > _tick) continue;

            if (spec.Reaches(ox, oy, currentFacing, target))
                result.Add((spec, currentFacing));
            else if (!_noFlip && spec.Reaches(ox, oy, !currentFacing, target))
                result.Add((spec, !currentFacing));
        }
        return result;
    }

    /// <summary>
    /// Begin an attack, or fall through to the chase.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:88-126</c>:
    /// <code>
    /// if (!available.empty() &amp;&amp; (canfly || phobj.onground))
    /// {
    ///     auto [selected, attack_facing] = available[randomizer.next_int(available.size())];
    ///     flip = attack_facing;  set_stance(Stance::STAND);
    ///     phobj.inputx = phobj.inputy = 0;  phobj.hspeed = phobj.vx = 0;
    ///     begin_attack(selected, flip, target_info, origin, &amp;physics, target_position);
    ///     mp -= attacks.at(selected).conmp;
    /// }
    /// else if (canmove &amp;&amp; std::abs(dx) &gt; 20) { flip = ...; set_stance(Stance::MOVE); }
    /// else set_stance(Stance::STAND);
    /// </code>
    /// <para>
    /// The CHASE itself lives in <see cref="Step"/>, which is the only place that
    /// knows the foothold chain — and which runs every tick, as the C++ does
    /// (<c>Mob.cpp:546</c>). Only the DECISION is gated, by
    /// <see cref="DecisionIntervalMs"/>; see <see cref="Engage"/>.
    /// </para>
    ///
    /// <para>
    /// <b>MP is read and deliberately NOT spent</b> — the user's ruling of
    /// 2026-10-15. The C++ does spend it (<c>Mob.cpp:113</c>) and the C# reference
    /// has no MP at all; this port follows the C# everywhere else, so spending it
    /// would be an undeclared divergence in the other direction.
    /// </para>
    /// </remarks>
    private void AdvanceAttack(
        List<(CombatService.MobAttackSpec Spec, bool Flipped)> available, int cx, int cy)
    {
        if (available.Count > 0 && OnGround)
        {
            // `randomizer.next_int(available.size())` -- a uniform pick among the
            // attacks that fit, which is the mob's choice of weapon.
            var (spec, flipped) = available[_rng.Next(available.Count)];
            if (!_noFlip)
            {
                Facing = flipped ? 1 : -1;
                FacingRight = flipped;
            }
            Direction = 0;                     // inputx = inputy = 0
            _activeAttack = spec;
            _attackElapsedMs = 0;
            // MobAttack.cpp:112 begin_attack() — the entries are pushed HERE, on
            // the frame the attack begins, and then walked every tick by
            // take_attacks(). Before this the hit was `PendingAttackPower`, a
            // number that went live the instant `attackAfter` elapsed: a type-2
            // attack was an INSTANT cone, so a mob shot the player from 200px with
            // nothing crossing the space between them.
            BeginEntries(spec, flipped, cx, cy);
            // Mob.cpp:138 -- `data.cooldown = max(duration, delay) + 400`, in ms.
            _attackCooldownFrames[spec.Index] =
                (int)(_tick + Math.Ceiling((Math.Max(spec.DurationMs, spec.DelayMs) + 400)
                                           / FixedStepMs));
            return;
        }

        // The chase (MobAttack.cpp:119-126) runs inside Step, which owns the
        // foothold chain; nothing to do here.
    }

    /// <summary>
    /// The attack in flight, or null. Reported so the client can play its pose.
    /// </summary>
    /// <remarks>
    /// The pose is the archive's own action name — <c>attack1</c> … <c>attack8</c>
    /// — and it is what <c>MobAnimation</c> resolves, so the mob plays the attack
    /// it actually chose rather than a stand-in.
    /// </remarks>
    public string AttackStance =>
        _activeAttack is { } a && _attackElapsedMs < a.DurationMs ? a.Action : "";

    /// <summary>Live attack entries, the reference's <c>attack_entries</c>.</summary>
    /// <remarks>
    /// <c>MobAttack.cpp:141-178</c> pushes one entry per attack that is BEGINNING,
    /// and <c>:193-229</c> walks them every tick in <c>take_attacks(target)</c>.
    /// Types 0 and 1 resolve as a static box; type 2 is a PROJECTILE that travels,
    /// which is why it cannot live in the attack spec — the spec is data, the entry
    /// is per-cast.
    /// </remarks>
    private readonly List<AttackEntry> _entries = new();

    /// <summary>One cast attack, in flight. <c>MobAttack.cpp:141</c>.</summary>
    private sealed class AttackEntry
    {
        public required CombatService.MobAttackSpec Spec { get; init; }

        /// <summary>Frames until the entry is testable — <c>attackAfter</c>.</summary>
        public double DelayMs { get; init; }

        /// <summary>0 rect, 1 single-target, 2 projectile, 3/4 ground cells.</summary>
        public int Type { get; init; }

        /// <summary>Where the projectile starts — <c>geometry.launch</c>.</summary>
        public double StartX { get; set; }
        public double StartY { get; set; }

        /// <summary>Where it ends, <c>radius</c> away along the frozen aim.</summary>
        public double FinishX { get; set; }
        public double FinishY { get; set; }

        /// <summary>The previous tick's position, for the swept test.</summary>
        public double PreviousX { get; set; }
        public double PreviousY { get; set; }

        /// <summary>
        /// <c>max(1, radius * 600 / bullet_speed)</c> ms, <c>MobAttack.cpp:157</c>.
        /// </summary>
        public double LifetimeMs { get; set; } = 1;

        /// <summary>The static box for the non-projectile types.</summary>
        public Box Range { get; init; } = Box.Empty;

        public double ElapsedMs { get; set; }

        /// <summary>Type 1 only: was this aimed AT the character? (:145).</summary>
        public bool LocalTarget { get; init; }

        /// <summary>
        /// The facing this entry was launched with — <c>entry.flipped</c>,
        /// <c>MobAttack.cpp:143</c>. The ball art is drawn mirrored by it
        /// (<c>Mob.cpp:1125</c>, <c>data.ball.draw(DrawArgument(point,
        /// entry.flipped), alpha)</c>).
        /// </summary>
        public bool Flipped { get; init; }
    }

    /// <summary>
    /// Build the entries for an attack that is beginning, <c>MobAttack.cpp:141-178</c>.
    /// </summary>
    /// <remarks>
    /// Only type 2 travels. For the others this pushes a single static entry, which
    /// is what makes the hit a per-tick test against the character's rectangle
    /// rather than the decision-time cone — the reference separates them, and so
    /// does this.
    /// </remarks>
    private void BeginEntries(CombatService.MobAttackSpec spec, bool flipped, double cx, double cy)
    {
        var entry = new AttackEntry
        {
            Spec = spec,
            DelayMs = spec.DelayMs,
            Type = spec.Type,
            LocalTarget = true,
            Flipped = flipped,
            // MobAttack.cpp:147 -- `entry.range = MobAttackGeometry::world(
            // data.geometry.range, origin, flipped && !noflip)`. The MIRRORING is
            // the part that was missing: `f5/4220000.img/attack2/info/range` is
            // {-190,-97,50,0}, which reaches 190px to one side and 50 to the
            // other, and an unmirrored shift gives every left-facing mob a box
            // on the wrong side of its own body.
            Range = CombatService.MobAttackSpec.World(
                spec.Range, (int)_x, (int)_y, flipped && !_noFlip),
        };

        if (spec.Type == 2 && spec.Radius > 0)
        {
            // MobAttack.cpp:150-157.
            //   start   = launch(origin, flipped)          [origin + range/sp, X mirrored]
            //   aim     = toward (destination.x, destination.y - 25)
            //   finish  = start + normalise(aim - start) * radius
            //   lifetime= max(1, radius * 600 / bullet_speed)      [ms]
            //
            // So the flight speed is DERIVED, not authored: px/sec = bullet_speed /
            // 0.6. A port that used `bulletSpeed` as a px/sec figure would fly 0.6x
            // too fast, and the error would look like "the projectile is quick".
            double spx = flipped ? -spec.SourceX : spec.SourceX;
            double spy = spec.SourceY;
            double startX = _x + spx;
            double startY = _y + spy;

            double dx = cx - startX;
            double dy = (cy - 25) - startY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance == 0)
            {
                dx = flipped ? 1 : -1;
                distance = 1;
            }
            entry.StartX = startX;
            entry.StartY = startY;
            entry.PreviousX = startX;
            entry.PreviousY = startY;
            entry.FinishX = startX + dx * spec.Radius / distance;
            entry.FinishY = startY + dy * spec.Radius / distance;

            // bullet_speed defaults to 100 and is clamped to at least 1
            // (Mob.cpp:161), so this cannot divide by zero.
            double speed = Math.Max(1, spec.BulletSpeed);
            entry.LifetimeMs = Math.Max(1, spec.Radius * 600.0 / speed);
        }

        _entries.Add(entry);
    }

    /// <summary>
    /// Advance every entry one tick and record any hit, <c>MobAttack.cpp:193-229</c>.
    /// </summary>
    /// <remarks>
    /// The swept test is the point. A projectile is FAST — up to 350 px/s, and it
    /// travels the whole `radius` in a few frames — so a box test at the current
    /// position alone would let it pass straight through a 44px-wide character.
    /// <c>MobAttack.cpp:205-209</c> therefore tests the SEGMENT from the previous
    /// tick's position to this one, against the character's box fattened by 8px,
    /// and remembers the new position as the next segment's start.
    /// </remarks>
    private void AdvanceEntries(double cx, double cy)
    {
        if (_entries.Count == 0) return;

        // The rectangle the HIT is tested against. This is Stage.cpp:792's box --
        //   mobs.take_attacks({ target.x() - 10, target.x() + 10,
        //                       target.y() - 50, target.y() })
        // -- and NOT the 22/65 box `update_attack` uses to decide whether the
        // attack REACHES (MobAttack.cpp:57-58). They are different rectangles in
        // the reference and using one for both made every landing attack 12px
        // too wide and 15px too tall; see HitBoxHalfWidth.
        var target = new Box((int)(cx - HitBoxHalfWidth), (int)(cy + HitBoxTop),
                             (int)(cx + HitBoxHalfWidth), (int)(cy + TouchBottom));

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            AttackEntry entry = _entries[i];
            entry.ElapsedMs += FixedStepMs;
            if (entry.ElapsedMs < entry.DelayMs) continue;

            bool hit;
            bool expired = true;
            if (entry.Type == 2)
            {
                double t = Math.Min(1.0, (entry.ElapsedMs - entry.DelayMs) / entry.LifetimeMs);
                double currentX = entry.StartX + (entry.FinishX - entry.StartX) * t;
                double currentY = entry.StartY + (entry.FinishY - entry.StartY) * t;
                var expanded = new Box(
                    target.Left - 8, target.Top - 8, target.Right + 8, target.Bottom + 8);
                hit = CombatService.MobAttackSpec.Crosses(
                    entry.PreviousX, entry.PreviousY, currentX, currentY, expanded);
                entry.PreviousX = currentX;
                entry.PreviousY = currentY;
                expired = t >= 1.0;
            }
            else
            {
                hit = entry.Type == 1 ? entry.LocalTarget : entry.Range.Overlaps(target);
                expired = false;
            }

            if (hit)
            {
                _pendingAttackDamage += Math.Max(0, entry.Spec.Power);
                _entries.RemoveAt(i);
                continue;
            }
            if (expired) _entries.RemoveAt(i);
        }
    }

    /// <summary>
    /// Damage this attack will deal when its delay expires, or 0.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:193-217</c> resolves the hit once <c>elapsed &gt;= delay</c>
    /// — <c>delay</c> is the archive's <c>attackAfter</c> — and
    /// <c>Mob.cpp:160</c> is where <c>power</c> comes from.
    /// <para>
    /// DIAGNOSTIC ONLY. The damage that actually lands now comes from
    /// <see cref="_pendingAttackDamage"/>, accumulated by the per-tick entry walk —
    /// this getter reports what the SPEC says and ignores where the character is,
    /// so a projectile that has not arrived yet would report a hit. Nothing
    /// consumes it for damage any more.
    /// </para>
    /// </remarks>
    public int PendingAttackPower
    {
        get
        {
            if (_activeAttack is not { } a) return 0;
            return _attackElapsedMs >= a.DelayMs ? a.Power : 0;
        }
    }

    /// <summary>Damage collected by the entry walk this tick. See AdvanceEntries.</summary>
    private int _pendingAttackDamage;

    /// <summary>
    /// Take the attack's damage if its delay has expired, and clear it so it lands once.
    /// </summary>
    /// <remarks>
    /// <c>MobAttack.cpp:193-217</c>: the attack entry is only tested once
    /// <c>elapsed &gt;= delay</c>, and <c>MobAttack.cpp:226</c> retires it — on a
    /// HIT or on expiry, not on expiry alone:
    /// <c>if (hit || expired) entry = attack_entries.erase(entry); else ++entry;</c>.
    /// Consuming it here rather than reading it keeps
    /// those two moments apart -- a getter that kept returning the power would hit
    /// every frame for the rest of the swing.
    /// <para>
    /// The VALUE now comes from the entry walk, not from
    /// <see cref="PendingAttackPower"/>. That is the whole point of the entries:
    /// the hit is decided by a per-tick test against the character's rectangle
    /// (a swept segment, for a projectile), so "the delay elapsed" and "it hit"
    /// stop being the same statement.
    /// </para>
    /// </remarks>
    public int ConsumeAttackDamage()
    {
        if (_pendingAttackDamage <= 0) return 0;
        int power = _pendingAttackDamage;
        _pendingAttackDamage = 0;
        _activeAttack = null;      // landed; the entry is done
        return power;
    }

    /// <summary>One live attack entry, in the shape the frame publishes.</summary>
    public sealed record AttackEntryView(
        double X,
        double Y,
        double ElapsedMs,
        double DelayMs,
        double LifetimeMs,
        int Type,
        string BallAction,
        int BallFrameCount,
        int BallFrameIndex,
        bool Flipped);

    /// <summary>
    /// EVERY entry in flight. W-070.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scalar <see cref="AttackEntries"/> reports <c>_entries[0]</c> and only that
    /// one, and that is why "the ball art never appears" was true of a mob that
    /// carries it. MEASURED on <c>4220000</c>: two attacks -- <c>attack1</c> type 2
    /// (flying, delay 1320 ms, <c>ballAction "attack1/ball"</c>, 2 frames) and
    /// <c>attack2</c> type 0 (static, delay 1200 ms, no ball). When the mob used
    /// attack2, entry 0 WAS attack2, so the frame reported a static entry with an
    /// empty ball action for the whole fight and the art sat unseen in the archive.
    /// </para>
    /// <para>
    /// The reference draws a ball per entry past its delay
    /// (<c>Mob.cpp:1118-1126</c>). One entry per frame cannot express that, so the
    /// list is published ALONGSIDE the scalars rather than replacing them.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AttackEntryView> EveryEntry
    {
        get
        {
            var all = new List<AttackEntryView>(_entries.Count);
            foreach (AttackEntry e in _entries)
                all.Add(Describe(e));
            return all;
        }
    }

    /// <summary>
    /// The ball's frame index, on the ONE rule both publishing paths use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// W-091. The ball's frame advances on the BALL'S OWN delays.
    /// <c>Animation.cpp:64-67</c>: <c>delay = src["delay"]; if (delay == 0)
    /// delay = 100;</c> — the 100 is the FALLBACK for a frame with no delay node,
    /// not the rate every ball runs at. MEASURED 2026-10-18: <c>ballart-verify</c>
    /// reported "the ball frame index never advances" on 1 run in 4, and a ball
    /// whose frames declare 30 ms or 60 ms animated at the wrong rate.
    /// </para>
    /// <para>
    /// ONE rule, because the two paths are two views of the same entry and the
    /// frame publishes BOTH: the scalars (<see cref="AttackEntries"/>) and the
    /// list (<see cref="EveryEntry"/>). They used to disagree — the list path
    /// hard-coded 100 ms per index and the scalar path used the ball's own
    /// delays — so one frame carried TWO different phases for the same ball,
    /// and the viewer (which reads the scalar) and the suites (which read the
    /// list) were looking at different animations. MEASURED 2026-10-22 on 4220000
    /// (a 2-frame ball declaring 60 ms per frame): of 1,369 frames that
    /// published an entry, the two paths disagreed on 24 — exactly the frames
    /// between 60 ms and 100 ms past the delay, which is where the two rules
    /// part company.
    /// </para>
    /// <para>
    /// OWN-DESIGN, and declared because it is a difference from the reference:
    /// <c>MobAttack.cpp:36</c> advances <c>data.second.ball</c> EVERY tick
    /// whether or not the attack is playing, so the reference's ball phase is
    /// effectively free-running. This port starts the ball at frame 0 when the
    /// entry begins and runs it on the ball's own per-frame delays, which is
    /// deterministic and reads the same every replay. The consequence is
    /// bounded: which frame of a 2-3 frame ball is showing, never whether the
    /// shot is there at all.
    /// </para>
    /// </remarks>
    private static int BallFrameIndex(AttackEntry e, bool flying)
    {
        int count = e.Spec.BallFrameCount;
        if (!flying || count <= 0)
        {
            // Before the delay there is nothing in the air yet, so there is no
            // frame to be showing (`Mob.cpp:1121`).
            return 0;
        }

        double since = e.ElapsedMs - e.DelayMs;

        // The ball's own per-frame delays, one entry per frame. `BallFrameCount`
        // and `BallFrameDelays` walk the same frames through the same filter
        // (`CombatService.cs`), so this list is never empty while `count > 0`.
        // MEASURED 2026-10-22 over every attack entry the mob catalog declares
        // (1,667 entries): 0 with `ballFrameCount != len(ballFrameDelays)` and
        // 0 with a ball but no delays at all.
        //
        // There is deliberately NO 100 ms fallback branch here, because the 100
        // is already inside this list: a frame with no delay node contributes
        // 100 (`Animation.cpp:64-67`), so a ball whose frames declare no delay
        // runs through here as well. The branch that used to sit below could
        // never be reached, and its comment claimed the opposite.
        IReadOnlyList<int> delays = e.Spec.BallFrameDelays;
        double acc = 0;
        int index = 0;
        for (int i = 0; i < delays.Count; i++)
        {
            if (since < acc + delays[i])
            {
                index = i;
                break;
            }
            acc += delays[i];
            index = i;
        }
        return index % count;
    }

    /// <summary>
    /// One entry's public shape. The scalar path (<see cref="AttackEntries"/>) and
    /// this must agree, so both read the same helper.
    /// </summary>
    private AttackEntryView Describe(AttackEntry e)
    {
        double t = e.LifetimeMs > 0
            ? Math.Clamp((e.ElapsedMs - e.DelayMs) / e.LifetimeMs, 0.0, 1.0)
            : 0.0;
        bool flying = e.ElapsedMs >= e.DelayMs;
        int ballIndex = BallFrameIndex(e, flying);
        return new AttackEntryView(
            e.StartX + (e.FinishX - e.StartX) * t,
            e.StartY + (e.FinishY - e.StartY) * t,
            e.ElapsedMs, e.DelayMs, e.LifetimeMs, e.Type,
            flying ? e.Spec.BallAction : "",
            e.Spec.BallFrameCount, ballIndex, e.Flipped);
    }

    /// <summary>
    /// The attack entries currently in flight, oldest first — the reference's
    /// <c>attack_entries</c> as an observable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entries exist so the HIT is a per-tick test against the character's
    /// rectangle, but until now nothing outside this class could see one, and every
    /// statement about a projectile had to be inferred from when the character lost
    /// HP. That inference fails whenever anything else can also damage the character:
    /// on a map with mobs the contact hit (<c>Stage.cpp:809</c>) arrives first and
    /// is indistinguishable from the shot. MEASURED while verifying this: a 50 HP
    /// character and a mob with <c>info/PADamage 165</c> — the shot it fired never
    /// got the chance to be timed.
    /// </para>
    /// <para>
    /// A snapshot of the first entry is enough to watch a projectile cross the map:
    /// its position is frozen at the launch point for the whole <c>delay</c>
    /// (<c>entry.delay</c>, <c>MobAttack.cpp:199</c>) and then moves along
    /// <c>start -> finish</c> over <c>lifetime</c> ms (:204-206). That is the
    /// difference between a travelling attack and an instant cone, measured rather
    /// than argued.
    /// </para>
    /// </remarks>
    public (int Count, double X, double Y, double ElapsedMs, double DelayMs,
            double LifetimeMs, int Type, string BallAction, int BallFrameCount,
            int BallFrameIndex, bool Flipped) AttackEntries
    {
        get
        {
            if (_entries.Count == 0)
                return (0, 0, 0, 0, 0, 0, 0, "", 0, 0, false);
            AttackEntry e = _entries[0];
            if (e.Type != 2)
            {
                // A static attack does not fly, so there is no flight position and
                // NO flight time -- reporting the default 1ms here would read as
                // "a projectile that arrives instantly", which is precisely the
                // thing a reader would be checking for. The box centre is the
                // closest honest answer to "where is this attack".
                return (_entries.Count,
                        (e.Range.Left + e.Range.Right) / 2.0,
                        (e.Range.Top + e.Range.Bottom) / 2.0,
                        e.ElapsedMs, e.DelayMs, 0, e.Type,
                        "", 0, 0, false);
            }
            double t = e.LifetimeMs > 0
                ? Math.Clamp((e.ElapsedMs - e.DelayMs) / e.LifetimeMs, 0.0, 1.0)
                : 0.0;

            // Mob.cpp:1121-1125 draws the ball only once the entry is PAST its
            // delay, and Mob.cpp:163-165 draws `data.ball` at the same lerped
            // point. Before the delay there is nothing in the air yet.
            bool flying = e.ElapsedMs >= e.DelayMs;

            // The ball's own frame, on the rule the list path uses too -- one
            // rule, because the frame publishes both views of this entry.
            // See BallFrameIndex for the rule and for the difference from the
            // reference.
            int ballIndex = BallFrameIndex(e, flying);

            return (_entries.Count,
                    e.StartX + (e.FinishX - e.StartX) * t,
                    e.StartY + (e.FinishY - e.StartY) * t,
                    e.ElapsedMs, e.DelayMs, e.LifetimeMs, e.Type,
                    flying ? e.Spec.BallAction : "",
                    e.Spec.BallFrameCount,
                    ballIndex,
                    e.Flipped);
        }
    }

    /// <summary>
    /// One contact hit on the character, or 0 while the cooldown holds.
    /// </summary>
    /// <returns>The damage dealt, already charged against the cooldown.</returns>
    /// <remarks>
    /// <para>
    /// <c>Mob::create_touch_attack</c> (<c>Mob.cpp:1848-1858</c>):
    /// </para>
    /// <code>
    /// if (!touchdamage) return MobAttack();
    /// int32_t power   = watk + status_value(WATK);
    /// int32_t maxattack = power * attack_multiplier(false);
    /// int32_t minattack = maxattack * 0.8f;
    /// int32_t attack  = randomizer.next_int(minattack, maxattack);
    /// </code>
    /// <para>
    /// The caller decides <i>whether</i> this happens — <c>Stage.cpp:809</c> asks
    /// <c>find_colliding</c> for the first alive mob the character's swept body
    /// rectangle overlaps, and only that one. This method is the mob half: the
    /// <c>bodyAttack</c> permission and the damage.
    /// </para>
    /// <para>
    /// <b>The one declared divergence.</b> The reference applies this EVERY frame
    /// the overlap holds — there is no timer anywhere on the <c>Stage.cpp:809</c>
    /// path, and a mob whose <c>power</c> is 50 kills a 50 HP character on the
    /// first frame. This port keeps <see cref="AttackCooldown"/> (60 frames ≈ 1s)
    /// instead, because per-frame contact damage would make every map instantly
    /// lethal and there is no way to observe anything else. It is own-design,
    /// named here rather than buried.
    /// </para>
    /// <para>
    /// The 0.8×–1.0× roll is not applied either, for the same reason the stat is
    /// not added: this port has no stat sheet, and <c>info/PADamage</c> alone is
    /// the honest reading of a formula whose first term is one.
    /// </para>
    /// </remarks>
    public int ContactDamage()
    {
        if (!Alive) return 0;

        // Mob.cpp:1850 -- `if (!touchdamage) return MobAttack();`. The flag is the
        // archive's own `info/bodyAttack`; a mob without it cannot land a contact
        // hit at all. Measured over the open Mob.nx: 1,945 of 2,053 images carry
        // it, so this is the common case and not a rare corner -- but the 108 that
        // do not are genuinely harmless rather than hitting on a formula this
        // codebase invented.
        if (!_canTouchAttack)
            return 0;

        if (AttackCooldownLeft > 0)
        {
            AttackCooldownLeft--;
            return 0;
        }

        AttackCooldownLeft = AttackCooldown;
        HitsLandedOnPlayer++;
        return MobDamage;
    }

    /// <summary>
    /// Damage one contact hit deals, from the archive's own <c>info/PADamage</c>.
    /// </summary>
    /// <remarks>
    /// Was <c>MaxHp / 8</c>, which is in neither the reference nor the archive.
    /// On the mobs this simulator is pointed at that produced 43 and 37 where the
    /// archive says <c>PADamage = 90</c> for both
    /// (<c>f2/2220100.img</c>, <c>f2/2110200.img</c>, measured).
    ///
    /// <para>
    /// The client builds the same number at Mob.cpp:1853-1856
    /// (<c>watk + status_value(WATK)</c>, rolled between 0.8x and 1.0x of the
    /// result). This port has no stat model, so it does not roll and does not add
    /// a stat: the archive's <c>watk</c> on its own is the honest reading of a
    /// formula whose first term is a stat sheet this simulator does not have.
    /// </para>
    /// </remarks>
    public int MobDamage => _touchDamage;

    /// <summary>
    /// Its hit points, out of <see cref="MaxHp"/>.
    /// </summary>
    /// <remarks>
    /// Read from the mob's own <c>info/maxHP</c> (measured on
    /// <c>f2/9409000.img</c>: 8) rather than invented, because the question
    /// "can this fight be won" is a property of the map's spawns. The
    /// reference hardcodes 2,000,000 (<c>Mob.cs:803-819</c> as read by the
    /// original transliteration) so every mob there is a four-hit piñata and
    /// the number says nothing about this map.
    /// </remarks>
    public int Hp { get; private set; }

    /// <summary>The HP this mob spawned with.</summary>
    public int MaxHp { get; }

    /// <summary>Whether the HP has run out and the death animation is playing.</summary>
    public bool Dying => Hp <= 0 && !Dead;

    /// <summary>True when it can be hit at all: alive, and still standing.</summary>
    public bool Alive => !Dead && Hp > 0;

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
        // The death animation, then gone.
        //
        // This is NEW and it sits above every other branch, because a mob at
        // zero HP plays `die1` and nothing else: Mob.cpp:1861-1868
        // `apply_death()` sets `Stance::DIE` and `dying = true`, and
        // `is_alive()` (:1870-1874) is `active && !dying && delayed_kill < 0`
        // -- so a dying mob is excluded from `find_closest`'s hit test before
        // geometry is even consulted.
        //
        // Once the animation runs out the mob is removed, exactly as the
        // reference does at SpriteEngine.cs:486-492 (it goes on a DeadList and
        // the sprite is freed). There is still no respawn.
        if (Dying)
        {
            if (--DeathFramesLeft <= 0)
            {
                Dead = true;
                OnGround = false;
            }
            return;
        }

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
            // Sticky, unlike Engaged above. Set HERE, before the redirect's
            // early return: that branch returns on almost every hit (the mob
            // always has a facing toward whoever struck it), so a line placed
            // after it never ran. MEASURED: with Aggro set after the redirect,
            // a 120-tick fight logged aggro=false on the struck mob for its
            // entire engaged life and it landed 0 swings.
            //
            // Mob.cpp:1802 gates it on `damage > 0` -- see `_lastHitDamage`.
            if (_lastHitDamage > 0)
                Aggro = true;
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

        // An NPC never rolls and never moves. Mob.cs:351 throws the dice inside
        // DoMove, and Npc.cs has no equivalent to call, so placing an NPC here
        // without this would give it a direction the client never gave it.
        if (Frozen)
        {
            Direction = 0;
            return;
        }

        // The HIT knockback, Mob.cpp:595-598:
        //
        //     else if (canmove && stance_entered)
        //     {   phobj.vx = flip ? -KBSPEED : KBSPEED; }
        //
        // KBSPEED is 130.0 (Mob.h:467) and `stance_entered` means it fires ONCE, on
        // the frame the mob ENTERS the hit pose — not every frame it flinches.
        //
        // PLACED HERE, before the chase block, and that placement is load-bearing.
        // The chase redirects a struck mob toward its attacker (Mob.cs:434-460)
        // and RETURNS, so anything below it never runs: put the recoil next to
        // Move() and the first version of it applied to exactly zero mobs while
        // their HP fell correctly. In the reference `phobj.vx` is set in the stance
        // switch and consumed by `physics.move_object(phobj)` at the end of the
        // same call, so no decision branch in between can swallow it.
        //
        // OWN DESIGN, declared: the reference sets a VELOCITY and lets
        // `move_object` and ground friction bleed it off over several frames. This
        // model's Move() is a direct displacement with no velocity and no friction,
        // so the impulse is applied once for one step's worth
        // (KBSPEED * TIMESTEP = 2.17px) rather than invented as a decay curve.
        ApplyHitKnockback();

        // ---- THE CHASE. MobAttack.cpp:119-126, the half that made acquisition
        // observable.
        //
        //     bool move_facing = noflip ? flip : dx > 0;
        //     if (canmove && std::abs(dx) > 20)
        //     {
        //         flip = move_facing;
        //         set_stance(Stance::MOVE);
        //         if (canfly) flydirection = dy < -30 ? UPWARDS : dy > 30 ? DOWNWARDS : STRAIGHT;
        //     }
        //     else set_stance(Stance::STAND);
        //
        // `Aggro` is what the C++ tests. It is set by damage (Mob.cpp:1802) OR by
        // firstAttack acquisition (MobAttack.cpp:81, above), so BOTH routes now drive
        // the mob. Before this the mob only ever turned toward a player who had hit
        // it (`HitsTaken > 0`, the branch above), which is exactly the case
        // firstAttack exists to avoid -- a mob that could see you could not reach
        // you, and one that could reach you had to be provoked first.
        //
        // The attack it would play instead (MobAttack.cpp:88-118) does not exist here:
        // picking a random available attack needs the `attackN/info` list that
        // ~43% of mobs carry and this port does not read. Recorded as the next
        // gap, not claimed as done.
        //
        // NOT ported: the dedicated flight controller at Mob.cpp:664-734, where
        // `chasing` picks a flight target that ignores footholds. A flyer's chase
        // here walks its chain -- the same limitation its patrol already had, and
        // `phobj.inputy`/`flydirection` have no counterpart in this model.
        // An attack in flight replaces the chase. MobAttack.cpp:93-95 does
        // exactly that on the attack branch --
        //     flip = attack_facing; set_stance(Stance::STAND);
        //     phobj.inputx = phobj.inputy = 0; phobj.hspeed = phobj.vx = 0;
        // -- so the body stops walking for the length of the swing.
        if (Aggro && _canMove && AttackStance.Length == 0 && characterX is { } chx)
        {
            int cdx = chx - (int)_x;
            if (Math.Abs(cdx) > ChaseReach)
            {
                // noflip: the mob keeps the facing it had. It still WALKS, because
                // `canmove` is a separate flag and MobAttack.cpp:120 gates only the
                // flip on it.
                if (!_noFlip)
                {
                    Facing = cdx > 0 ? 1 : -1;
                    FacingRight = Facing > 0;
                }
                Direction = cdx > 0 ? 1 : -1;
                Move();
                return;
            }
            Direction = 0;
            return;
        }

        // An attack in flight STOPS the mob. It must return here, and not merely
        // fail the condition above.
        //
        // The chase block above already excludes a swinging mob
        // (`AttackStance.Length == 0`), which reads as though that covers it — but
        // a false condition makes the whole `if` fall THROUGH, and what follows is
        // the patrol, which happily walks the mob along its own route while it is
        // mid-swing. MEASURED 2026-10-16: `9500302` played `attack1` for 285
        // frames and moved on 22 of them, 0.6px per frame, in a straight line.
        //
        // The reference has no such fall-through, because
        // `Mob::update_attack` RETURNS at MobAttack.cpp:48 while
        // `active_attack >= 0` — the move decision at :119-126 is unreachable
        // then — and :93-94 has already zeroed both the input and the speed:
        //
        //     phobj.inputx = phobj.inputy = 0;  phobj.hspeed = phobj.vx = 0;
        //
        // Locomotion here is a separate pass from the decision, so the decision
        // has to stop it explicitly. Zeroing `Direction` also retires the speed:
        // `Move()` returns immediately on a zero direction.
        if (AttackStance.Length > 0)
        {
            Direction = 0;
            return;
        }

        // Mob.cpp:83 / Mob.cs:166-170 -- a mob with no `move` node has no walk and
        // no roll. Before this it patrolled like everything else, at a hardcoded
        // 2px/frame; MEASURED over two disjoint 158-mob slices of Mob.nx, only 89
        // and 101 respectively carry a `move` node, so ~40% of the bestiary was
        // walking a chain the archive says it cannot walk.
        if (!_canMove)
        {
            Direction = 0;
            return;
        }

        if (++_sinceRoll >= RollEvery)
            Roll();

        Move();
    }

    /// <summary>
    /// The one-shot recoil from being hit, consumed as it is applied.
    /// </summary>
    private void ApplyHitKnockback()
    {
        LastKnockbackPx = 0.0;
        LastKnockbackFacing = 0;
        if (_knockVx == 0.0) return;
        double step = _knockVx;
        int facing = _knockVx < 0 ? 1 : -1;   // the side the impulse was sized from
        _knockVx = 0.0;                 // Mob.cpp:600's `break` — once, not per frame
        float delta = (float)(step * FixedStepMs);
        _x += delta;
        LastKnockbackPx = delta;
        LastKnockbackFacing = facing;
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
        if (!Alive)
            return;
        HitsTaken++;
        Stun = HitStunFrames;
        // No damage number exists on this path -- the map-editor endpoint is asking
        // "does a blow turn the mob", and a blow that did not land would be a
        // different question with no answer here. Treated as a landed hit.
        _lastHitDamage = 1;
    }

    /// <summary>
    /// Take <paramref name="damage"/> hit points and react.
    /// </summary>
    /// <remarks>
    /// The overload the live session uses. The parameterless one above is the
    /// original API — it staggers without spending HP, because the map-editor
    /// endpoint that calls it is asking "does a hit turn the mob toward the
    /// attacker" and has no damage to apply.
    ///
    /// <para>
    /// Zero damage is a real answer, not a bug: <c>Mob.cpp:1795-1798</c>
    /// returns <c>{0, false}</c> when the hitchance roll fails, and a miss is
    /// drawn as the word MISS rather than as a number. It is allowed here so
    /// the caller can report a whiff without inventing a separate path.
    /// </para>
    /// </remarks>
    /// <returns>the damage actually applied, which is 0 on a dead mob</returns>
    public int TakeHit(int damage)
    {
        if (!Alive)
            return 0;

        int applied = Math.Max(0, damage);
        Hp = Math.Max(0, Hp - applied);
        HitsTaken++;
        Stun = HitStunFrames;
        _lastHitDamage = applied;

        // A HIT CANCELS AN ATTACK IN FLIGHT. MobAttack.cpp:22-28:
        //
        //     if (!is_alive() || doomed || (status_mask & (FREEZE | STUN | ...)))
        //     {   active_attack = -1;  incoming_attack = 0;
        //         attack_entries.clear();  return; }
        //
        // and Mob.cpp:1815 puts the mob into `Stance::HIT` on damage, so the
        // reference reacts to a hit by ABANDONING its swing and flinching.
        //
        // MEASURED: it did not. A mob kept `attack1` on screen for its whole
        // 2.4s duration while being knocked back 2.17px per hit — attacking and
        // recoiling at the same time, which is a state the reference never
        // produces. It was caught by the audit's own `mobMovesWhileAttacking`
        // finding, which had been green until the knockback existed to trip it.
        //
        // Clearing it here is also what makes the knockback land in the right
        // pose: the impulse belongs to the HIT (Mob.cpp:595-598 is inside
        // `case Stance::HIT`), not to an attack that should no longer be running.
        _activeAttack = null;
        _attackElapsedMs = 0.0;

        // The recoil, Mob.cpp:595-598, and only for a mob that CAN move: the
        // reference gates on `canmove`, so a FIXATED mob (a signboard, a
        // stationary prop) absorbs the hit without shifting. This model's
        // `_canMove` is derived from the animation set, which is the same thing
        // Mob.cpp:81-83 derives `canmove` from.
        //
        // Not for a corpse: Mob.cpp:1861-1868 reaches `die1` on the same tick the
        // HP reaches zero and a dead mob does not flinch, let alone recoil.
        if (Hp > 0 && _canMove)
            _knockVx = FacingRight
                ? -HitKnockbackSpeed / 1000.0
                : HitKnockbackSpeed / 1000.0;

        // Mob.cpp:1861-1868 apply_death(): the mob plays `die1` and is no
        // longer a legal target the same tick its HP reaches zero, rather than
        // one tick later. The stun is dropped with it — a corpse does not
        // flinch.
        if (Hp == 0)
        {
            DeathFramesLeft = _deathFrames;
            Stun = 0;
        }

        return applied;
    }

    private void Roll()
    {
        // The interval counter is cleared HERE, inside Roll, not at the call
        // site. The call site only does `++_sinceRoll >= RollEvery`, so a roll
        // that does not reset it leaves the counter permanently above the
        // threshold and every later tick rolls again -- which is how RollEvery
        // was 60 on paper and one frame in practice.
        //
        // MEASURED with the reset missing: over 400 ticks one mob changed
        // stance 105 times and direction 33 times while never leaving a 32px
        // stretch of one foothold (reversals 0). Stance changes reset
        // MobAnimation's accumulator, so the walk cycle restarted ~4x a second
        // and never left frame 0 -- the same frozen legs RollEvery=1 caused,
        // just at a lower frequency, and harder to spot because the number in
        // the source said 60.
        //
        // Clearing it here rather than at the call site also means the counter
        // measures frames SINCE THE LAST ROLL, which is what the interval means,
        // and a caller that rolls for another reason cannot silently restart it.
        _sinceRoll = 0;

        // Mob.cs:354-375 verbatim in structure: one roll, and a heading chosen
        // from it.
        //
        // The reference's own code is a `switch` over `Random.Next(200)` with
        // four EXACT cases (50, 100, 150, 199) and no default, so 196 of the 200
        // outcomes leave the heading untouched. That is why a port of it looks
        // broken rather than faithful: the mobs stand still. Measured on a real
        // map, 30 mobs over 60 ticks produced dx = 0 on every one of them --
        // ~30% chance of that, since each tick independently keeps its heading
        // 196/200 of the time and a mob only ever travels while its heading is
        // non-zero.
        //
        // The client's own movement is not what made them move; it is that it
        // built a NEW Random per frame, so its 196 dead outcomes landed on
        // different ticks and read as "mostly keeps walking". A seeded Random
        // (see `_rng`, above) makes the dead outcomes repeat reproducibly, which
        // is what turns a 2% event into visible paralysis.
        //
        // So the RANGE is kept and the dead outcomes are given an outcome:
        // 0-49 left, 50-99 right, 100-149 stand, 150-199 jump-check. That is
        // the same four cases over the same 200-wide range, with the gaps the
        // client's switch left unfilled filled in as "carry on" the way a switch
        // with no default behaves -- except that "carry on" is what stops the
        // patrol, so it is made explicit as a heading rather than left to
        // chance.
        // Mob.cs:354 `switch (Random.Next(200))` with EXACTLY four cases and no
        // default: `case 50`, `case 100`, `case 150`, `case 199`. So 196 of 200
        // outcomes fall through and change nothing -- the mob keeps walking the way
        // it was walking.
        //
        // This port had read those as four RANGES (`roll < 50`, `roll < 100`,
        // `roll < 150`), which fires on 100% of rolls instead of 2%. Every 30 ticks
        // the mob therefore picked a brand-new heading, walked 30 ticks, picked
        // another, and never reached the end of anything -- it oscillated in place,
        // facing left, right, left, right, forever. That is the "mob flips wildly
        // left and right" report, and `reversals` stayed 0 the whole time because
        // Turn() was never involved: the roll was overwriting the direction outright.
        // MEASURED on f6/Map/Map0/000000002.img before the fix: 4 of 13 mobs flipped
        // facing more than once per ten frames, the worst at 65 flips across 120
        // samples while its x travelled 536px net.
        //
        // One roll now changes the heading at most once per 15 rolls on average.
        int roll = _rng.Next(RollRange);
        if (roll == RollLeft)
        {
            Facing = -1;
            FacingRight = false;
            Direction = -1;
            return;
        }
        if (roll == RollRight)
        {
            Facing = 1;
            FacingRight = true;
            Direction = 1;
            return;
        }
        if (roll == RollStand)
        {
            // Mob.cs:364-366 sets MoveDirection = None and deliberately does
            // NOT touch FlipX, so the mob keeps facing the way it was heading
            // while it stands. Modelling that is why Facing is left alone here
            // and only Direction is zeroed.
            Direction = 0;
            return;
        }
        switch (roll)
        {
            case RollJump:
                // Mob.cs:367-375 -- DoJump = true, and only for a Jump mob that
                // is not already in its hit animation. A patrol does not leap:
                // it would move the question "can a mob stand here" to a
                // different one. The roll is still consumed, exactly as the
                // client consumes it.
                break;
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

        // ---- the turn at the end of a foothold.
        //
        // Mob.cs:568-582 (left) and :647-660 (right), transcribed whole. Two details
        // in there are what stop a mob from flipping forever at a foothold edge:
        //
        //  1. The comparison is STRICT -- `X < FallEdge` going left, `X > FallEdge`
        //     going right. The foothold hand-off below uses `<=` and `>=`. With one
        //     operator for both, pinning X to the edge and then re-testing `X >= edge`
        //     is still true on the next tick, so Turn() fires again, and again, 60
        //     times a second: the sprite jitters in place and never travels. That is
        //     the "mob flips wildly left and right" report, and the cause is an
        //     operator, not a threshold.
        //
        //  2. The edge differs by cause. No next foothold at all gets `FH.X2 + 5` -- a
        //     5px overhang allowance, so a mob may stand slightly past the end -- while
        //     a wall gets exactly `FH.X2`. One edge for both made mobs turn at a
        //     coordinate the game walks past.
        //
        // The turn also sets an ABSOLUTE direction (`MoveDirection = Right`), not a
        // negation. Negation is only equivalent while Direction is exactly +/-1, and it
        // left Facing coupled to a value a stand-roll could already have zeroed.
        float fallEdge = Direction > 0 ? 999999f : -999999f;
        bool turnable = MoveType is not MobMoveType.Jump and not MobMoveType.Fly;
        if (Direction > 0)
        {
            FootholdPhysics.Foot? fwd = FindById(_foothold.Next);
            if (fwd is null) fallEdge = _foothold.X2 + 5f;
            else if (fwd is { IsWall: true }) fallEdge = _foothold.X2;
            if (turnable && _x > fallEdge)
            {
                _x = fallEdge;
                Direction = -1;
                Facing = -1;
                FacingRight = false;
                return;
            }
        }
        else
        {
            FootholdPhysics.Foot? back = FindById(_foothold.Prev);
            if (back is null) fallEdge = _foothold.X1;
            else if (back is { IsWall: true }) fallEdge = _foothold.X1;
            if (turnable && _x < fallEdge)
            {
                _x = fallEdge;
                Direction = 1;
                Facing = 1;
                FacingRight = true;
                return;
            }
        }

        // ---- the foothold hand-off: a SEPARATE test with its own guards.
        //
        // Mob.cs:621-630 and :698-706. Both require the current foothold not to be a
        // wall and the neighbour not to be a wall. A mob standing on a wall foothold,
        // or next to one, is not on a walkable chain and must not be teleported along
        // it -- and the neighbour needs an id at all, or FindById would resolve 0.
        FootholdPhysics.Foot? next = FindById(Direction > 0 ? _foothold.Next : _foothold.Prev);
        bool atEnd = Direction > 0 ? _x >= _foothold.X2 : _x <= _foothold.X1;
        if (next is not { IsWall: false } step || _foothold.IsWall || !atEnd)
            return;

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
        // Negating is only meaningful while Direction is +/-1. A stand-roll sets it
        // to 0 (Mob.cs:365), and `-0` is 0 -- so a turn taken from a standstill used
        // to clear Facing as well, and a mob with Facing 0 draws with no mirror and no
        // facing at all. Leaving here is the reference's behaviour at Mob.cs:566-577,
        // where a mob with nowhere to go simply stops.
        if (Direction == 0)
        {
            Reversals++;
            return;
        }

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

    private static int Angle256(int sx, int sy, int dx, int dy)
        {
            double raw = Math.Atan2(dx - sx, dy - sy) * PiConv256;

            // Same defect as CharacterPhysics.Angle256, same fix: a flat line's angle
            // is a whole number that `atan2(...) * 128/pi` lands a few ulps short of,
            // and the truncating cast turns 64 into 65 -- which makes Cos256
            // non-zero and sinks a mob walking on flat ground until the handover
            // snaps it back.
            double nearest = Math.Round(raw);
            if (Math.Abs(raw - nearest) < 1e-6) raw = nearest;

            return (int)raw + 128;
        }

    private static double Cos256(int i) => Math.Cos(i * Math.PI / 128.0);

    private static double Sin256(int i) => Cos256((i + 192) & 255);
}