using MapleBench.Services.MapEditor;

// NOTE ON CITATIONS. The `.cs`/`.js` citations below name MapleNecrocer
// (C:\MUWORK\GAME\_necrocer_src\MapleNecrocer) -- this port's own source,
// which has no counterpart in the authoritative reference: clean-client
// (C:\MUWORK\GAME\MPSH\clean-client\src) contains zero `.cs` files. Where a
// MapleNecrocer citation disagrees with clean-client, clean-client wins -- see
// pm/PARITY.md, "The C++ wins". These were not re-audited for content; some
// record a port's deliberate divergence.

namespace MapleBench.Services.Simulate;

/// <summary>
/// One mob's animation state for a live session: which action it is playing,
/// which frame of that action, and where its art lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is on the server.</b> There were two clocks for the same thing:
/// this file's intended one (the <see cref="MapSimMobFrame"/> remarks describe
/// a per-tick accumulator that resets to zero rather than to the overshoot) and
/// the one <c>mobsprite.js</c> already had. Both claimed authority and neither
/// knew about the other, so a mob's legs advanced at the browser's paint rate
/// while its position advanced at the session's tick rate, and nothing on the
/// wire said which was right.
/// </para>
/// <para>
/// <b>The server wins, because the tick is the thing that already exists.</b>
/// A session advances on <see cref="MapSimSessionStore.FixedStepMs"/> and is
/// replayable: the same inputs put the body and the mobs in the same places.
/// An animation clock in the browser is a function of how fast the tab happens
/// to paint, which makes "which frame is this mob on" a question about the
/// viewer rather than about the simulation — the exact thing the seeded,
/// reproducible design exists to prevent. <c>mobsprite.js</c> was therefore
/// reduced to a renderer: it picks the action's frames and blits the one the
/// server named.
/// </para>
/// <para>
/// <b>The timings are measured, not guessed.</b> A frame's own <c>delay</c>
/// node decides how long it holds (Animation.cpp:283-333, and the comment at
/// mobsprite.js:27-29). Where no archive is open there are no delay nodes, so
/// the fallbacks below are the reference's own default rather than a number
/// invented to look like a measurement.
/// </para>
/// </remarks>
internal sealed class MobAnimation
{
    /// <summary>
    /// The reference's default when a frame carries no <c>delay</c>
    /// node — the same constant mobsprite.js:50 used.
    /// </summary>
    private const int DefaultFrameMs = 100;

    /// <summary>
    /// How long a mob plays <c>hit1</c> when the archive will not say.
    /// </summary>
    /// <remarks>
    /// MEASURED on mob 9409000 (<c>GET /api/simulate/mob-sprite</c>): its
    /// <c>hit1</c> is ONE frame with <c>delay = 600</c>. This constant is only
    /// the answer for a mob whose art cannot be read; a mob whose art CAN be
    /// read gets its real per-frame delays instead.
    /// </remarks>
    private const int DefaultHitMs = 600;

    /// <summary>
    /// How long a mob plays <c>die1</c> when the archive will not say.
    /// </summary>
    /// <remarks>
    /// MEASURED on mob 9409000: <c>die1</c> is 4 frames with delays
    /// 150/150/150/300 — 750 ms in total, which is why a corpse holds on the
    /// last frame rather than being freed the tick it stops moving.
    /// </remarks>
    private const int DefaultDieMs = 750;

    /// <summary>The WZ action name, or "" when no archive holds this mob.</summary>
    public string Stance { get; private set; } = "";

    /// <summary>Which frame of <see cref="Stance"/> is showing.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>How many frames <see cref="Stance"/> carries.</summary>
    public int FrameCount { get; private set; }

    /// <summary>Session path of the mob's image, for the sprite cache to key on.</summary>
    public string SpritePath { get; private set; } = "";

    /// <summary>Milliseconds held on the current frame so far.</summary>
    private double _heldMs;

    /// <summary>
    /// The frame delays of the action currently playing, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Empty means "no archive": the caller then falls back to the constants
    /// above, which is why a mob with no art still gets a real stance name and
    /// a real frame count rather than zeros.
    /// </remarks>
    private int[] _delays = [];

    /// <summary>
    /// Whether the action loops. <c>die1</c> does not: it plays once and holds
    /// (mobsprite.js:66-68, and Mob.cs:443-476 frees the sprite afterwards).
    /// </summary>
    private bool _loops = true;

    /// <summary>The session tick this clock was last advanced on.</summary>
    /// <remarks>
    /// The guard that makes this safe to READ from anywhere. Two callers ask
    /// for a mob's animation in the same tick — the frame response and the
    /// weapon-swing hit test — and a clock that advanced on every read would
    /// run at double speed for whichever mob both of them touched. Advancing
    /// once per <c>session.Frame</c> makes the call order irrelevant.
    /// </remarks>
    private long _advancedOnFrame = -1;

    /// <summary>
        /// The clock for the mob at <paramref name="index"/> of the session's list.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Keyed by POSITION, and passed one, rather than looked up by id. Both
        /// callers already walk the list in order, so the index is free — and
        /// looking the id up was wrong twice over. A map may spawn one id many
        /// times over (measured: 9409000 at indices 0, 4 and 6 on
        /// f1/Map/Map9/920010800.img), and a first-match search sends all three to
        /// ONE clock: they animate in lockstep, and a stun on one staggers the
        /// other two. And when the ids did not resolve in list order the search
        /// returned an index past the end of <c>Animations</c> and threw.
        /// </para>
        /// <para>
        /// Safe to call from anywhere: the clock advances at most once per session
        /// tick no matter how many callers ask.
        /// </para>
        /// </remarks>
        public static MobAnimation For(
            MapSimSession session, int index, string kind, MobPatrol patrol)
        {
            if (index < 0 || index >= session.Mobs.Count)
                return new MobAnimation();
            string mobId = session.Mobs[index].Id;

            while (session.Animations.Count <= index)
                        session.Animations.Add(new MobAnimation());
                    MobAnimation anim = session.Animations[index];

                    anim.SpritePath = session.SpritePathFor(index, mobId, kind);
                    anim.AdvanceOnce(session, mobId, kind, patrol);
                    return anim;
                }

    /// <summary>
        /// Advance this mob's animation, at most once per session tick.
        /// </summary>
        private void AdvanceOnce(MapSimSession session, string mobId, string kind, MobPatrol patrol)
        {
            if (_advancedOnFrame == session.Frame)
                return;
            _advancedOnFrame = session.Frame;
            Advance(session, mobId, kind, patrol);
        }

        /// <summary>
        /// Work out the action from the mob's state, and advance its frame.
        /// </summary>
    /// <remarks>
    /// The action is chosen the way <c>Mob.cpp:114-133</c> names them:
    /// <c>stand</c> when idle, <c>move</c> when travelling, <c>hit1</c> while
    /// staggered and <c>die1</c> once dead. That mapping belongs to the
    /// reference, so it is made here rather than guessed in the browser.
    /// </remarks>
    private void Advance(MapSimSession session, string mobId, string kind, MobPatrol patrol)
    {
        // `Dying`, not `Dead`. Those are two different instants and the old code
        // used the wrong one:
        //
        //   MobPatrol.cs:420   Dying => Hp <= 0 && !Dead     <- HP just hit zero
        //   MobPatrol.cs:450-456  if (Dying) { if (--DeathFramesLeft <= 0) Dead = true; return; }
        //
        // So `Dead` only becomes true once the death window has ALREADY elapsed --
        // and the death window is measured in die1 frames (MobPatrol.cs:592 sets
        // `DeathFramesLeft` from `_deathFrames`, which is die1's own length). Asking
        // for `die1` on `Dead` therefore asks for the animation exactly when there
        // is no time left to play it.
        //
        // MEASURED before this line, attacking a 350 HP mob on f4/Map/Map1/100000004.img
        // until it hit zero: the frame reported `hp=0 dying=True` while the stance
        // stayed `stand` and walked 0/2 -> 1/2, and the mob then simply vanished.
        // The corpse never played die1 at all.
        //
        // The reference has HP reaching zero start the death animation
        // (Mob.cpp:1861-1868, apply_death) and only afterwards free the sprite
        // (SpriteEngine.cs:486-492) -- which is `Dying` and then `Dead`, in that order.
        // A chosen attack is its OWN action in the archive -- attack1..attack8, the same
        // names Mob.cpp:142-148 walks. It sits ABOVE stand/move/hit1/die1 because
        // while it is in flight the mob is standing still (MobAttack.cpp:93-94
        // zeroes inputx and hspeed), and reporting `stand` for the whole swing
        // would hide the very thing the archive animates.
        string attack = patrol.AttackStance;
        string wanted = attack.Length > 0 ? attack
            : patrol.Dying ? "die1"
            : patrol.Stun > 0 ? "hit1"
            : patrol.Direction != 0 ? "move"
            : "stand";

        // Mob.cpp:115-124 -- A FLYING MOB STANDS ON ITS `fly` ANIMATION:
        //
        //   if (canfly) { animations[Stance::STAND] = anisrc["fly"];
        //                 animations[Stance::MOVE]  = anisrc["fly"]; }
        //   else        { animations[Stance::STAND] = anisrc["stand"];
        //                 animations[Stance::MOVE]  = anisrc["move"];  }
        //
        // with `canfly = anisrc["fly"].size() > 0` (Mob.cpp:82). Asking such a mob
        // for `stand` asks for art that does not exist: MEASURED,
        // `f5/9300104.img` carries attack1, die1, fly and hit1 and NO stand at all,
        // so the simulation drew nothing and `_combat.MobFrame(mid, "stand", ...)`
        // returned a zero collision box -- a mob nothing could ever be hit by,
        // while its `fly/0` carries a perfectly good `lt`/`rb`.
        if ((wanted == "stand" || wanted == "move") && session.MobCanFly(mobId, kind))
            wanted = "fly";

        if (wanted != Stance)
        {
            Stance = wanted;
            FrameIndex = 0;
            _heldMs = 0;
            _loops = wanted != "die1";
            _delays = session.MobFrameDelays(mobId, kind, wanted);
        }

        // No art: one synthetic frame, so the count is a real 1 rather than a
        // zero that a caller would have to special-case.
        if (_delays.Length == 0)
        {
            FrameCount = 1;
            FrameIndex = 0;
            return;
        }

        FrameCount = _delays.Length;

        // The accumulator: elapsed ms are summed against the CURRENT frame's own
        // delay and the counter resets to ZERO rather than to the overshoot --
        // Animation.cpp:283-333, and the rule mobsprite.js:236-240 already used.
        // While-loop, not if, because a slow frame can outlast several delays.
        _heldMs += MapSimSessionStore.FixedStepMs;
        while (_heldMs > _delays[FrameIndex])
        {
            _heldMs = 0;
            int next = FrameIndex + 1;
            if (next >= FrameCount)
            {
                if (!_loops)
                {
                    // A death holds on its last frame. The mob is still on the
                    // map (there is no respawn) but the animation is over.
                    FrameIndex = FrameCount - 1;
                    _heldMs = 0;
                    return;
                }
                next = 0;
            }
            FrameIndex = next;
        }
    }

    /// <summary>
    /// Frame delays for one action of one mob, read from the open archive.
    /// </summary>
    /// <remarks>
    /// <paramref name="fallbackMs"/> is the measured total for this action
    /// (600 for <c>hit1</c>, 750 for <c>die1</c>), divided across the one
    /// synthetic frame that is reported when the archive says nothing — so the
    /// number on the wire is the real duration even with no art loaded.
    /// </remarks>
    internal static int[] DelaysOrFallback(
        string mobId, string kind, string action, int fallbackMs)
    {
        MobSpriteService sprites = MobAnimation.Sprites;
        if (sprites is null)
            return [fallbackMs];

        MobSpriteDto dto = sprites.Describe(null, mobId, kind);
        foreach (MobActionDto a in dto.Actions)
        {
            if (!string.Equals(a.Name, action, StringComparison.OrdinalIgnoreCase))
                continue;

            List<int> delays = new();
            foreach (AnimationFrameDto f in a.Frames)
                // A frame with no pixels is not a frame a viewer can show.
                if (f.Width > 0 && f.Height > 0)
                    delays.Add(f.Delay > 0 ? f.Delay : DefaultFrameMs);
            if (delays.Count > 0)
                return delays.ToArray();
        }

        // The action is absent or undrawable. Report the duration we measured
        // for it as a single frame rather than nothing.
        return action switch
        {
            "hit1" => [fallbackMs > 0 ? fallbackMs : DefaultHitMs],
            "die1" => [fallbackMs > 0 ? fallbackMs : DefaultDieMs],
            _ => [DefaultFrameMs],
        };
    }

    /// <summary>
    /// The sprite reader, set once by the store's constructor.
    /// </summary>
    /// <remarks>
    /// A static because the animation needs to read the archive from a session
    /// DTO method that has no service handle. It is the same singleton the
    /// <c>/mob-sprite</c> endpoint already resolves, so there is still one
    /// implementation of "what art does this mob have".
    /// </remarks>
    internal static MobSpriteService? Sprites { get; set; }
}