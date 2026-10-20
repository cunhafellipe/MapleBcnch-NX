using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// A character that walks a map the way the client does: standing ON a foothold,
/// moving along its angle, and stepping to the next one at the end.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a rewrite, not a patch.</b> The previous version asked
/// "what ground is under this point?" every step, and that question has no good
/// answer on a slope: a 16px foothold dropping 3px changes its surface by less
/// than a pixel per step, so sampling it point by point quantises the slope into
/// a staircase and the character falls at every riser. Six attempts to fix that
/// with tolerances failed, and the reason is visible in Elem8100/MapleNecrocer
/// (MIT) <c>MapleCharacter.DoMove</c>, which never asks the question at all:
///
/// <code>
/// Direction = GetAngle256(X1, Y1, X2, Y2);
/// X += (float)(Sin256(Direction) * SpeedR);
/// Y -= (float)(Cos256(Direction) * SpeedR);
///
/// if ((X >= FH.X2) &amp;&amp; (FH.NextID != 0) &amp;&amp; (!FH.IsWall()) &amp;&amp; (!FH.Next.IsWall()))
/// {
///     FH = FH.Next;
///     X = FH.X1;
///     Y = FH.Y1;
/// }
/// </code>
///
/// <para>
/// The character carries a foothold, not a coordinate. It moves along that
/// foothold's direction and hands over at the endpoint. Slopes work because the
/// Y comes from the slope's own angle, never from a query.
/// </para>
///
/// <para>
/// Constants are the client's, from <c>Player.DoMove</c>:
/// <c>Speed += 1.5f</c> capped at 2.5, <c>Speed -= 0.25f</c> when released
/// (MapleCharacter.cs). <c>JumpSpeed 0.6 / JumpHeight 9.5 / MaxFallSpeed 8</c>
/// from MapleCharacter.cs:136-138. Fixed step 1/60 s, RenderFormDraw.cs:177.
/// </para>
/// </remarks>
public sealed class CharacterPhysics
{
    /// <summary>One frame at 60 Hz (RenderFormDraw.cs:177).</summary>
    public const float StepSeconds = 0.016666668f;

    /// <summary>Speed gained per step while a direction is held.</summary>
    public const float Acceleration = 1.5f;

    /// <summary>The cap the client puts on walking speed.</summary>
    public const float MaxWalkSpeed = 2.5f;

    /// <summary>Speed lost per step with no direction held.</summary>
    public const float Decceleration = 0.25f;

    /// <summary>Initial upward speed when jumping (MapleCharacter.cs:136).</summary>
    public const float JumpSpeed = 0.6f;

    /// <summary>Peak height a jump reaches (MapleCharacter.cs:137).</summary>
    public const float JumpHeight = 9.5f;

    /// <summary>Falling faster than this is capped (MapleCharacter.cs:138).</summary>
    public const float MaxFallSpeed = 8f;

    /// <summary>
    /// The cap the client raises to on landing (MapleCharacter.cs:879).
    /// </summary>
    /// <remarks>
    /// Landing writes <c>MaxFallSpeed = 10</c> into the same field it just been
    /// capped to. It is not reset until the character jumps again, so a jump's
    /// arc is longer on the way down than a fall from the same height.
    /// </remarks>
    public const float LandedFallSpeed = 10f;

    /// <summary>How far below the feet the fall probe reaches (MapleCharacter.cs:876).</summary>
    public const int FallProbeSlack = 6;

    /// <summary>
    /// How far past the map's bottom edge a fall may go before it is stopped.
    /// Must exceed one frame of fall travel, or a character whose y already
    /// equals <c>bounds.bottom</c> passes the test in a single step.
    /// </summary>
    public const float FallOutSlack = 60f;

    /// <summary>The band inside the map where walking stops (MapleCharacter.cs:777, :829).</summary>
    public const int EdgeMargin = 20;

    /// <summary>How far ahead and up the ground probe looks (MapleCharacter.cs:802).</summary>
    public const int GroundProbeAhead = 10;

    /// <summary>How far up that probe sits (MapleCharacter.cs:802, Y - 5).</summary>
    public const int GroundProbeUp = 5;

    /// <summary>How far ahead the wall probe looks (MapleCharacter.cs:803, X + 4).</summary>
    public const int WallProbeAhead = 4;

    /// <summary>How far up the wall probe sits (MapleCharacter.cs:803, Y - 4).</summary>
    public const int WallProbeUp = 4;

    /// <summary>Radians per angle unit, for the client's 256-step angle scale.</summary>
    private const float PiConv256 = -40.743665431f;

    /// <summary>Half the character's width, for wall checks (LadderRopes.cs:18-27).</summary>
    public const int BodyHalfWidth = 10;

    private readonly FootholdPhysics _map;
    private FootholdPhysics.Foot? _foothold;

    /// <summary>
    /// The map rectangle, or null when the map has no walkable extent.
    /// </summary>
    /// <remarks>
    /// Read from the map's <c>info</c> node, not derived from the footholds —
    /// MapleMap.cs:171-196 takes VRLeft/VRTop/VRRight/VRBottom when they are
    /// present, and those are usually wider than the geometry.
    /// </remarks>
    private (int Left, int Top, int Right, int Bottom)? _bounds;
    private float _x;
    private float _y;
    private float _speed;
    private float _velocityY;

    /// <summary>
    /// The terminal velocity in force right now: 8 normally, 10 after a landing.
    /// </summary>
    /// <remarks>
    /// The client keeps this in the same mutable field it caps against, so a
    /// landing writes 10 and it stays 10 until the next jump. Modelling it as a
    /// const would make a jump's descent slower than the game's.
    /// </remarks>
    private float _fallCap = MaxFallSpeed;

    public CharacterPhysics(FootholdPhysics map, FootholdPhysics.Foot start, int direction = 1)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _bounds = map.Bounds;
        _foothold = start;
        // Start at the end the character walks FROM, which is the opposite of the
        // end it walks toward: X1 going right, X2 going left. That is what
        // MapleNecrocer does — its rightward branch traverses X1->X2 and hands
        // over at X >= FH.X2, its leftward branch traverses X2->X1 and hands
        // over at X <= FH.X1 (MapleCharacter.cs). Starting anywhere else makes
        // the very first step count as "already reached the end".
        _x = direction >= 0 ? start.X1 : start.X2;
        _y = direction >= 0 ? start.Y1 : start.Y2;
        _lastGroundId = start.Id;
        OnGround = true;
    }

    /// <summary>Position in map coordinates.</summary>
    public float X => _x;

    public float Y => _y;

    /// <summary>Horizontal speed, px per step.</summary>
    public float Speed => _speed;

    /// <summary>Vertical speed, px per step. Positive is downward.</summary>
    public float VelocityY => _velocityY;

    /// <summary>Whether the character is standing on a foothold rather than falling.</summary>
    public bool OnGround { get; private set; }

    /// <summary>The foothold underfoot, or null while falling.</summary>
    public FootholdPhysics.Foot? StandingOn => _foothold;

    /// <summary>Steps taken since construction.</summary>
    public long Ticks { get; private set; }

    /// <summary>Why the character left the ground, or null while it is on it.</summary>
    public string? LeftGroundBecause { get; private set; }

    /// <summary>
    /// One fixed step.
    /// </summary>
    /// <param name="moveLeft">-1, 0 or +1.</param>
        /// <param name="climb">-1 up a ladder, +1 down one, 0 not climbing. Up is the
    /// NEGATIVE value because the client's Up key does <c>Y -= 1.5f</c>
    /// (MapleCharacter.cs:550) and smaller y is higher on a map.</param>
        /// <param name="prone">DOWN is held, which is prone when there is no ladder.</param>
        public void Step(int moveLeft, int climb = 0, bool prone = false)
            {
                StepCore(moveLeft, climb, prone);
                ClampToBounds();
            }

        /// <summary>
        /// Put the body on a foothold as if it had just spawned there.
        /// </summary>
        /// <remarks>
        /// <c>Player::respawn</c> (<c>Player.cpp:210-251</c>). The comment there is
        /// why this clears rather than moves: <i>"Nothing about the old spot may
        /// carry over to the new one. A leftover fall speed makes the character
        /// overshoot the platform it spawns on and drop straight through it, and a
        /// stale foothold makes the first physics step collide against the wrong
        /// platform. Clear the whole movement state and let FootholdTree::update_fh
        /// re-derive the foothold from the new position."</i>
        /// </remarks>
        public void Respawn(FootholdPhysics.Foot foot, int x, int y)
        {
            _foothold = foot;
            _x = x;
            _y = y;
            _speed = 0f;
            _velocityY = 0f;
            _fallCap = 0f;
            Rising = false;
            OnGround = true;
            AttackPoseTicks = 0;
            AttackStance = "";
            LastAction = "";
        }

        /// <summary>
        /// Let go of the swing pose without moving the body.
        /// </summary>
        /// <remarks>
        /// Used when the character DIES mid-swing. The pose lock holds the body for
        /// the whole animation (`Player.h:59-61`, `Player.cpp:1214-1224`), so a
        /// character killed three frames in keeps it for the other thirty-four and
        /// the wire reports a swing pose for a body that can no longer swing.
        /// </remarks>
        public void ReleasePoses()
        {
            AttackPoseTicks = 0;
            AttackStance = "";
            LastAction = "";
        }

        /// <summary>
        /// Put the body back inside the map rectangle, and stop it moving outward.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One authoritative clamp, at the one place every tick passes through.
        /// Clamping inside <see cref="Walk"/> was not enough and could never be:
        /// <c>DriftWhileFalling</c> (:599, a falling body's steering), the wall
        /// push-out (:439/:444) and the ladder entry (:838/:887/:899) each assign
        /// <c>_x</c> without walking through it, and <see cref="Step"/> returns early
        /// on the climb branch — so a clamp at the end of the method body would be
        /// skipped exactly where it was needed most.
        /// </para>
        /// <para>
        /// Measured over 331 sessions: 13 frames ended at x=2028 against a legal
        /// ceiling of 2026 — a body drifting past the right edge of the map by 2px
        /// and staying there.
        /// </para>
        /// </remarks>
        private void ClampToBounds()
        {
            if (_bounds is not { } bd)
                return;

            float lo = bd.Left + EdgeMargin;
            float hi = bd.Right - EdgeMargin;
            if (_x < lo) { _x = lo; _speed = 0; }
            else if (_x > hi) { _x = hi; _speed = 0; }
        }

        /// <summary>The tick's body, without the bounds invariant applied.</summary>
                private void StepCore(int moveLeft, int climb = 0, bool prone = false)
                                {
                            Ticks++;

                        // ---- the swing lock: while a swing is owed, NOTHING the caller asks for
                        // happens. -----------------------------------------------------------
                        //
                        // Not "movement is damped during a swing" — nothing moves, nothing turns,
                        // no state changes. The reference has three separate locks and this one
                        // line is the shape they add up to:
                        //
                        //   * every state machine's update() returns immediately while attacking
                        //     (PlayerStates.cpp:112-113 standing, :174-175 walking, :219-220
                        //     airborne, :364-365 flying), so the direction keys are never even
                        //     READ — PlayerWalkState::update reads `hasrightinput` only after
                        //     that guard (:177-181), so bypassing the setters would not help;
                        //   * set_direction() ignores its argument while attacking
                        //     (Player.cpp:1214-1217);
                        //   * set_state() ignores its argument while attacking (Player.cpp:1220-1224).
                        //
                        // Player.h:59-61 states the intent outright: "Set flipped ignore if
                        // attacking" / "Set state ignore if attacking".
                        //
                        // Placed FIRST, before the speed accumulation, so the tick is refused
                        // before anything has been written — the alternative, guarding each
                        // movement branch, is the failure this project has already paid for once
                        // (`ClampToBounds`: three write sites bypassed a clamp placed too late).
                        //
                        // The counter still ticks, because the reference clears `attacking` when
                        // the ANIMATION ends, not when the swing does damage:
                        // `Player.cpp:868-870` on `aniend`, and `aniend` is `stframe == 0` from
                        // CharLook.cpp:273-274 — i.e. the frame clock coming home. So the pose is
                        // released by the pose running out, which is what makes the release point
                        // land on the last frame instead of an arbitrary tick count.
                        if (AttackPoseTicks > 0)
                        {
                            AttackPoseTicks--;
                            if (AttackPoseTicks > 0)
                            {
                                LastAction = "attack";
                                return;
                            }

                            // Last tick of the animation: fall through so the body settles into
                            // the pose the next input asks for, in the same tick, rather than
                            // freezing for one extra frame.
                        }

                        if (moveLeft != 0)
        {
            // The client guards acceleration on the fall state
            // (MapleCharacter.cs:718, :735):
            //
            //     if (JumpState != JumpState.jsFalling)
            //     {
            //         SpeedL += 1.5f;
            //         if (SpeedL > 2.5f) SpeedL = 2.5f;
            //     }
            //
            // A falling character still steers, but it does not build walking
            // speed — it is moving under gravity, not under its own power.
            // Rising is exempt: the client guards only jsFalling.
            if (OnGround || Rising)
            {
                _speed += Acceleration * moveLeft;
                _speed = Math.Clamp(_speed, -MaxWalkSpeed, MaxWalkSpeed);
            }
        }
        else
        {
            // The client decelerates even mid-air, so momentum does not carry.
            if (_speed > 0) _speed = Math.Max(0, _speed - Decceleration);
            else if (_speed < 0) _speed = Math.Min(0, _speed + Decceleration);
        }

        // The action word has to say STAND when the character is standing, not
        // WALK unconditionally. It was one line -- `LastAction = "walk"` -- and
        // that is why the measurement said "identical action state under every
        // input": a body with no key held reported `walk`, a body falling off a
        // ledge reported `walk`, and a body mid-jump reported `walk`. The action
        // is what the avatar's stance is chosen FROM (avatar.js actionFor), so
        // a constant here means a constant pose on screen regardless of input.
        //
        // The transition the client actually makes is PlayerWalkState::
        // update_state (PlayerStates.cpp:196-209): a released key does not
        // yield STAND at once -- the body keeps walk1 until its residual speed
        // has actually bled off -- so the test is on the speed, not on the key.
        // `Math.Abs(_speed) > 0` is that residual-speed test, not a key test.
        LastAction = Math.Abs(_speed) > 0.01f ? "walk" : "stand";

        // Climbing takes over whenever it is asked for and a ladder is in reach.
        // The client does the same: the Up/Down branches run before the walk
        // branches and set InLadder, and the walk is skipped (MapleCharacter.cs:541).
        //
        // The reach test is the client's own ±10 box (LadderRopes.cs:19) — see
        // FindLadderForClimb. It used to be widened to 40 to catch ladders the
        // character stopped 18px short of, but that shortfall was this file
        // missing DriftWhileFalling, not a property of the data.
        if (climb != 0 && FindLadderForClimb(climb) is { } ladderHere)
        {
            LastAction = "climb";
            // Pass the ladder in. Climb used to look it up again from its own
            // position, and by then Step had not moved anything — but the two
            // lookups disagreed, so the character fell instead of climbing and
            // landed back on the foothold it started from. One lookup, one
            // ladder, one answer.
            ClimbOn(moveLeft, climb, ladderHere);
            return;
        }

        // STANDING ON A LADDER WITH NO CLIMB KEY HELD: hang, do not fall.
        //
        // MapleCharacter.cs:541-585 touches Y only inside the two key branches:
        //
        //     if (Keyboard.KeyDown(Input.Up))   { OffY = -3; if (OnLadder) { ... Y -= 1.5f; } ... }
        //     if (Keyboard.KeyDown(Input.Down) ...) { OffY = 0; if (OnLadder) { ... Y += 1.5f; } ... }
        //
        // With neither held, neither branch runs, `InLadder` stays true and Y is
        // untouched -- the character hangs. The ONLY ways out are the two exits
        // above (the top's uf, and Y > Y2 at the bottom) and jumping off.
        //
        // MEASURED before this: climbing to y=-325 and releasing every key put the
        // body into `fall` on the very next frame and walked it back down to the
        // platform at y=-265 -- so there was no way to climb and then wait, and
        // the top exit could only be reached by holding Up the whole way.
        if (_ladder is not null && !OnGround && climb == 0)
        {
            LastAction = "climb";
            VelocityYValue = 0;
            _velocityY = 0;
            return;
        }

        // Standing on a ladder, having reached the end of it, is a resting
        // state and not a request to keep climbing. Without this the top check
        // inside Climb fires again every tick, the character is teleported back
        // to the ladder's Y each time, and the walk spends its whole tick budget
        // oscillating. Measured: 18,000 steps ending where it started.
        if (_ladder is not null && climb != 0)
                {
                    LastAction = "climb-top";
                    _velocityY = 0;
                    return;
                }

                // Prone: DOWN on the ground, with no ladder in reach.
                //
                // The ordering is the whole of it. DOWN is ALSO climb-down
                // (MapSimSessionStore.Advance folds input.Down into climb = +1), and the
                // two meanings are told apart by which branch above already claimed the
                // tick: the climb branch returns at :281 when a ladder was found, and
                // the ladder-top branch returns at :292 when one is already held. So by
                // the time control reaches here, a ladder has definitively refused the
                // DOWN — which is exactly the condition that makes it prone.
                //
                // Without this the action word stayed `stand` forever: `climb` was
                // non-zero but FindLadderForClimb returned null, the climb branch was
                // skipped, and LastAction kept the stand/walk value assigned above.
                // Measured on Map.nx 000000002.img: DOWN held for 240 frames produced
                // action=stand on 240 of 240.
                //
                // Airborne is excluded so a body in the air still reports rise/fall —
                // the action word is the stance, and a jumping body drawn prone would be
                // a lie about where it is.
                //
                // `prone` is a self-named input rather than read off the physics: the
                // session store passes `input.Down`, and this is the one place the two
                // meanings of DOWN stop being the same integer.
                if (prone && OnGround && _ladder is null)
                        {
                            LastAction = "prone";
                            // Bleed off residual speed and settle onto the surface rather than
                            // returning: the fall-through below is what snaps the body down to
                            // the foothold it is standing over, and an early return here would
                            // leave a body that walked into a slope hovering above it.
                            if (moveLeft == 0)
                            {
                                _speed = 0;
                            }
                        }

        if (!OnGround)
        {
            // SpriteEngine.cs:1583-1598 splits the airborne arc in two:
            //
            //     case JumpState.jsJumping:
            //         Y += VelocityY;
            //         VelocityY += JumpSpeed;
            //         if (VelocityY > 0) jumpState = jsFalling;
            //         break;                       // <- no ground query
            //
            //     case JumpState.jsFalling:
            //         Y = Y + VelocityY;
            //         VelocityY = VelocityY + JumpSpeed;
            //         if (VelocityY > MaxFallSpeed) VelocityY = MaxFallSpeed;
            //         break;
            //
            // The ascent checks nothing. The descent is where MapleCharacter.cs
            // :875 looks for ground. Treating both as one "airborne" step made a
            // jump test the ground on the way UP, which is how a jump that should
            // clear 75px ended as a hop.
            LastAction = Rising ? "rise" : "fall";

            if (Rising)
            {
                // ---- HORIZONTAL MOTION WHILE RISING. MapleCharacter.cs:838-843:
                //
                //     Direction = GetAngle256(X1, Y1, X2, Y2);
                //     if (!FH.IsWall())
                //     {
                //         X += (float)(Sin256(Direction) * SpeedR);
                //         Y -= (float)(Cos256(Direction) * SpeedR);
                //     }
                //
                // There is NO JumpState guard on that movement. The two guards the
                // client does put on this block are both further down and both are
                // `jsNone`-only:
                //
                //     :852  if ((JumpState == jsNone) && (X > FallEdge)) JumpState = jsFalling;
                //     :864  if (JumpState == jsNone) { FH = FH.Next; X = FH.X1; ... }
                //
                // so while `jsJumping` the body keeps walking and keeps accelerating
                // (:718 / :735 exempt jsJumping from the "no speed while falling"
                // rule) but does not hand over to the next foothold and does not
                // start falling at the edge.
                //
                // MEASURED before this: this branch returned after the vertical
                // integration alone, so `_x` was never touched for the whole ascent.
                // A jump moved straight up and landed back where it started, and a
                // jump taken with a direction key held was indistinguishable from a
                // jump with no key held -- "cannot jump forward, only straight up".
                //
                // Y is not advanced by the slope term the client adds
                // (`Y -= Cos256 * SpeedR`). That omission is this file's existing,
                // measured one (see `Walk`, :489-513): the quantised angle drifts
                // 4px per flat foothold. Gravity supplies the vertical motion while
                // airborne, and stacking a second, quantised vertical on top of it
                // would compound that drift rather than follow a slope.
                if (_foothold is { } rfh && !rfh.IsWall)
                {
                    int rdir = Angle256(rfh.X1, rfh.Y1, rfh.X2, rfh.Y2);
                    _x += (float)(Sin256(rdir) * _speed);
                }

                _y += _velocityY;
                _velocityY += JumpSpeed;
                if (_velocityY > 0)
                    Rising = false;
                return;
            }

            // Falling does NOT stop horizontal motion. MapleCharacter.cs:824
            // gates the walk block on `FaceDir == Right && SpeedL == 0 && !InLadder`
            // and :839 on `!FH.IsWall()` -- neither mentions JumpState -- so a
            // character walking off a ledge keeps drifting sideways the whole way
            // down, decelerating at Decceleration the entire time.
            //
            // This method was purely vertical. The gap was invisible because it
            // only shows past the edge of a chain, where the character froze
            // in mid-air at the moment it stepped off. That is also why
            // ClimbReachX had been widened to 40 to catch ladders 18px away:
            // the character stopped at the foothold's end because it was never
            // moving horizontally, not because the ladder box was too narrow.
            DriftWhileFalling(moveLeft);

            Fall();
            return;
        }

        // Walking is what may END this step by running out of chain. When it
        // does, this tick has already been spent; stepping again would resume
        // from the new foothold at full speed and skip whatever the character
        // is standing on. The client has the same shape — the walk-left and
        // walk-right blocks each `return` when they hand over.
        bool leftGround = false;
        Walk(moveLeft, out leftGround);
        if (leftGround)
            return;

        // Standing in a portal. The client triggers on Up (MapleCharacter.cs:589)
        // but the geometry is what matters here: a character that reaches the
        // mouth of a door has arrived, and reporting which door tells the caller
        // whether the rest of the map is reachable.
        StandingPortal = _map.FindPortal((int)Math.Round(_x), (int)Math.Round(_y));
    }

    /// <summary>The portal the character is standing in, or null.</summary>
    public FootholdPhysics.Portal? StandingPortal { get; private set; }

    /// <summary>
    /// Move along the current foothold and step to the next at its end.
    /// </summary>
    private void Walk(int moveLeft, out bool leftGround)
    {
        leftGround = false;
        FootholdPhysics.Foot fh = _foothold!.Value;
        if (fh.IsWall)
        {
            LeaveGround("standing on a wall, which cannot be walked along");
            return;
        }

        // MapleCharacter.cs:777 (left) and :829 (right):
        //
        //     if ((X < Map.Left + 20) || (Attack) && (JumpState == jsNone))
        //         SpeedL = 0;
        //     if ((X > Map.Right - 20) || (Attack) && (JumpState == jsNone))
        //         SpeedR = 0;
        //
        // Twenty pixels of margin INSIDE the map rectangle, and the walk simply
        // stops there — it does not fall off and it does not wrap. The client has
        // no vertical counterpart: a character that misses every foothold keeps
        // falling forever, because FindBelow returns (99999, 99999) and the
        // landing test can never be satisfied. This port had no boundary at all,
        // which is why walks ran to the tick budget off the end of a map.
        if (_bounds is { } bd)
        {
            // The reference tests POSITION, not intent: `X < Map.Left + 20` zeroes
            // SpeedL whatever the keyboard is doing, because the test sits inside
            // `if (FaceDir == Left && SpeedR == 0 && !InLadder)` rather than inside
            // "the left key is down". Requiring `moveLeft < 0` here meant a character
            // that ended up outside the margin with no key held was never corrected,
            // and nothing else pulls it back -- the reference has the same hole, and
            // hides it by clamping the CAMERA (MapleCharacter.cs:688-691, :757-760).
            if (_x < bd.Left + EdgeMargin) _speed = Math.Max(_speed, 0);
            if (_x > bd.Right - EdgeMargin) _speed = Math.Min(_speed, 0);
        }

        int direction = Angle256(fh.X1, fh.Y1, fh.X2, fh.Y2);

        // The client's own expression for X: Sin of the foothold's direction, not a
                // horizontal move followed by a ground query. This is why slopes work.
                _x += (float)(Sin256(direction) * _speed);

                // Y does NOT come from the same expression. `-Cos256(direction) * speed`
                // is only the CLIENT's arithmetic, and it depends on the client's angle
                // convention (Sprites/Angle256: 0 is straight DOWN) lining up with its
                // axis convention. It does not line up here.
                //
                // What that costs, measured: the character walking right along
                // (-2918,180)->(-2750,180) -- a dead-flat line, dy=0, the case where the
                // answer is exactly "y does not change" -- drifted 4px per foothold and
                // the handover's `Y = step.Y1` snapped it back. 4px of fake climb, once
                // per ~1.3s, on level ground, in both directions (mirrored, because
                // _speed is signed). Zeroing this line removes the drift entirely
                // (control run: amplitude 4px -> 0px), which is what identified it.
                //
                // So Y is read off the line the character is actually on instead of
                // inferred from a quantised angle. `SurfaceAt` interpolates the real
                // segment, so flat is flat and a slope is followed exactly:
                //   - it removes the angle quantisation from the vertical axis
                //     altogether, which is where the 4px came from;
                //   - it cannot drift, because the value is a function of x alone.
                int surfaceX = (int)Math.Round(_x);
                _y = fh.SurfaceAt(surfaceX) ?? _y;

        // ...and then clamp POSITION, which the reference does not need to do for
        // the same reason the camera clamp covers the vertical fall: in the real
        // client the out-of-bounds state is never drawn. It is drawn here. This is the
        // same argument already made for the vertical floor clamp above, applied to
        // the horizontal edge -- the character must not end up somewhere the viewer
        // cannot scroll to. Without it, anything that moves the character without a
        // key held (a fall with drift, a portal landing, a scripted start) leaves it
        // permanently outside, which is the "character sometimes falls outside the map"
        // report.
        if (_bounds is { } bd2)
        {
            float lo = bd2.Left + EdgeMargin;
            float hi = bd2.Right - EdgeMargin;
            if (_x < lo) { _x = lo; _speed = 0; }
            else if (_x > hi) { _x = hi; _speed = 0; }
        }

        // A wall in the way stops horizontal motion -- but only a wall on the
        // character's OWN layer. MapleNecrocer compares Z (MapleCharacter.cs:857):
        //
        //     WallFH = FootholdTree.Instance.FindWallL(new Vector2(X - 4, Y - 4));
        //     if ((WallFH != null) && (X >= WallFH.X1) && (BelowFH.Z == WallFH.Z))
        //         X = WallFH.X1 - 1;
        //
        // A map stacks several layers, and a wall two floors up is scenery, not
        // an obstacle. Without the Z test the character is stopped by walls it
        // cannot even see -- and on a map with 400+ footholds there are always
        // some above it.
        // MapleCharacter.cs:801-807 (right) and :853-859 (left), together:
        //
        //     Below = FootholdTree.Instance.FindBelow(new Vector2(X + 10, Y - 5), ref BelowFH);
        //     WallFH = FootholdTree.Instance.FindWallR(new Vector2(X + 4, Y - 4));
        //     if ((WallFH != null) && (X <= WallFH.X1) && (BelowFH.Z == WallFH.Z))
        //     {
        //         X = WallFH.X1 + 1;
        //         SpeedR = 0;
        //     }
        //
        // Three things this port had wrong, and all three changed the result:
        //
        //   the probe is AHEAD and 5px UP, not under the feet. The wall the
        //   character is about to meet is not yet beneath it.
        //
        //   the wall probe is 4px UP as well — Y - 4, not Y. On a slope that
        //   shifts when the wall starts blocking.
        //
        //   the Z compared is the Z of the ground ten pixels AHEAD, not of the
        //   ground underfoot. Those are different floors on a layered map, and
        //   the question the client asks is "is that wall on the floor I am
        //   about to walk onto" — not "is it on the floor I am standing on".
        int leadX = (int)Math.Round(_x) + (moveLeft > 0 ? 10 : -10);
        int leadY = (int)Math.Round(_y) - GroundProbeUp;
        FootholdPhysics.Foot? leadGround = _map.FindBelow(leadX, leadY, falling: true);

        int wallX = (int)Math.Round(_x) + (moveLeft > 0 ? 4 : -4);
        int wallY = (int)Math.Round(_y) - WallProbeUp;
        FootholdPhysics.Foot? wall = moveLeft > 0
            ? _map.FindWall(wallX, wallY, movingRight: true, z: leadGround?.Z)
            : moveLeft < 0
                ? _map.FindWall(wallX, wallY, movingRight: false, z: leadGround?.Z)
                : null;

        if (wall is { } w2 && leadGround is { } g2 && w2.Z == g2.Z)
        {
            if (moveLeft > 0 && _x <= w2.X1)
            {
                _x = w2.X1 + 1;
                _speed = 0;
            }
            else if (moveLeft < 0 && _x >= w2.X1)
            {
                _x = w2.X1 - 1;
                _speed = 0;
            }
        }

        // MapleNecrocer's FallEdge, in its own shape (MapleCharacter.cs:793-801
        // left, :845-852 right):
        //
        //     FallEdge = -999999;                 // never fires
        //     if (FH.Prev == null)                // at the chain's end
        //         FallEdge = FH.X1 - 10;
        //     if ((FH.Prev != null) && (FH.Prev.IsWall()) && (FH.Prev.Y1 > Y))
        //         FallEdge = FH.X1;
        //     if ((JumpState == jsNone) && (X < FallEdge))
        //         JumpState = jsFalling;
        //
        // The default is what my version got wrong: the character steps
        // TEN pixels past the chain's end before it counts as off it.
        // Treating arrival at the end as leaving put the character into a
        // fall on the final frame of every chain.
        // MapleCharacter.cs:845-847 right, :793-795 left. The slack is NOT
        // symmetric: right is X2 + 5, left is X1 - 10. Using one number for both
        // gave the rightward walk five pixels of tolerance it should not have,
        // which is long enough to step onto a foothold that is already falling
        // away underneath.
        const float RightSlack = 5f;
        const float LeftSlack = 10f;

        long leadId = moveLeft >= 0 ? fh.Next : fh.Prev;
        float fallEdge = moveLeft >= 0 ? 999999f : -999999f;
        if (leadId == 0)
            fallEdge = moveLeft >= 0 ? fh.X2 + RightSlack : fh.X1 - LeftSlack;

        // A wall dropping away on the far side is the real edge.
        FootholdPhysics.Foot? lead = leadId == 0 ? null : FindById(leadId, fh);
        if (lead is { IsWall: true } lw)
        {
            float wallTop = moveLeft >= 0 ? lw.Y2 : lw.Y1;
            if (wallTop > _y)
                fallEdge = moveLeft >= 0 ? fh.X2 : fh.X1;
        }

        bool reachedEnd = moveLeft >= 0 ? _x > fallEdge : _x < fallEdge;

        // What the step decided, for a caller that cannot otherwise tell a
        // character that stood still from one that walked and was reset.
        LastWalk = new WalkDecision
        {
            Foothold = fh.Id,
            MovedTo = (int)Math.Round(_x),
            FootholdEnd = moveLeft >= 0 ? fh.X2 : fh.X1,
            ReachedEnd = reachedEnd,
            BlockedByWall = wall is { } wb && _x <= wb.X1 + 1,
            WallAt = wall?.X1,
            GroundBelow = leadGround?.Id,
        };

        // HANDOVER — gated on POSITION, not on reaching the chain's end.
        //
        // MapleCharacter.cs:810 (left) and :862 (right):
        //
        //     if ((X <= FH.X1) && (FH.PrevID != 0) && (!FH.IsWall())
        //         && (!FH.Prev.IsWall()))
        //     {
        //         FH = FH.Prev;
        //         if (JumpState == JumpState.jsNone) { X = FH.X2; Y = FH.Y2; }
        //     }
        //
        // The test is whether the character has walked off the near end of the
        // foothold it is on. `reachedEnd` only asks whether it has passed the
        // chain's far boundary, so it is false for every foothold in the middle
        // of a chain — and gating the handover on it meant the character took one
        // foothold and then stood there for the rest of its budget. Measured on
        // f1/Map/Map6/610030510.img: 300 steps, one foothold, 177.
        bool atEnd = moveLeft >= 0 ? _x >= fh.X2 : _x <= fh.X1;

        if (!atEnd)
            return;

        // Past the end of this foothold. Take the next one if it exists and is
        // walkable, otherwise the character has run out of ground.
        long nextId = moveLeft >= 0 ? fh.Next : fh.Prev;
        FootholdPhysics.Foot? next = nextId == 0 ? null : FindLinked(nextId, fh);

        if (next is not { IsWall: false } step)
        {
            // No foothold to hand over to. Whether that is "fell off the world"
            // or "the chain ended" is decided by reachedEnd, which knows where
            // the chain's own boundary is.
            if (!reachedEnd)
            {
                // Past this foothold's end, mid-chain, with no neighbour to take.
                // Returning and keeping `fh` let X run on regardless: the character
                // kept moving along a foothold whose extent had already ended.
                // The client has no such state — it either takes FH.Next/Prev or,
                // past FallEdge, goes to jsFalling.
                leftGround = true;
                LeaveGround(moveLeft >= 0
                    ? $"foothold {fh.Id} ends at x={fh.X2} and no foothold at id {nextId} meets it"
                    : $"foothold {fh.Id} ends at x={fh.X1} and no foothold at id {fh.Prev} meets it");
                return;
            }

            leftGround = true;
            LeaveGround(
                moveLeft >= 0
                    ? $"foothold {fh.Id} ends at x={fh.X2} with no walkable foothold after it"
                    : $"foothold {fh.Id} ends at x={fh.X1} with no walkable foothold before it");
            return;
        }

        _foothold = step;
        _x = moveLeft >= 0 ? step.X1 : step.X2;
        _y = moveLeft >= 0 ? step.Y1 : step.Y2;
        _lastGroundId = step.Id;
        // MapleCharacter.cs:863-868 also writes Z there. It is a derived render
        // depth (`FH.Z * 100000 + 60000`), and this port keeps the layer index
        // on the foothold instead, so there is nothing to update.
    }

    /// <summary>
    /// Keep walking sideways while falling, and shed speed the way the client does.
    /// </summary>
    /// <remarks>
    /// MapleCharacter.cs:824-843 runs the whole walk block whenever a direction
    /// is held, and its only guards are <c>!InLadder</c> and <c>!FH.IsWall()</c>.
    /// There is no JumpState test anywhere in it, so a falling character is still
    /// being steered: it moves along the direction it last held and decelerates
    /// at the usual 0.25 per frame (the <c>else</c> branch at :727-730).
    ///
    /// Y is left alone here. The client moves Y through the gravity integrator in
    /// <c>JumperSprite.DoMove</c>, which is the vertical half and runs on the same
    /// tick; <see cref="Fall"/> is that half.
    /// </remarks>
    private void DriftWhileFalling(int moveLeft)
    {
        if (moveLeft == 0)
        {
            // MapleCharacter.cs:727-730 — released input bleeds speed off even
            // mid-air, so the drift dies out instead of carrying to the ground.
            if (_speed > 0) _speed = Math.Max(0, _speed - Decceleration);
            else if (_speed < 0) _speed = Math.Min(0, _speed + Decceleration);
            if (_speed == 0)
                return;
        }
        else
        {
            // :718 / :735 — a falling character does not build speed, only sheds it.
            if (_speed > 0) _speed = Math.Max(0, _speed - Decceleration);
            else if (_speed < 0) _speed = Math.Min(0, _speed + Decceleration);
            moveLeft = Math.Sign(_speed);
            if (_speed == 0)
                return;
        }

        // `_speed` ALREADY carries the sign, so `* moveLeft` applies it a second
        // time and the character drifts the opposite way.
        //
        // The reference splits this into two magnitudes: `SpeedL` and `SpeedR` are
        // each clamped at 0 (MapleCharacter.cs:727-729, :744-746) and the SIGN comes
        // from which branch is taken -- `X += Sin256(Direction) * SpeedL` for left
        // (:790), `* SpeedR` for right (:841). This port merged the pair into one
        // signed `_speed` (`_speed += Acceleration * moveLeft`, :283) and `Walk`
        // follows that model (:489). `moveLeft = Math.Sign(_speed)` just above makes
        // the product `_speed * sign(_speed)` == `|_speed|`, i.e. ALWAYS RIGHT.
        //
        // So a character that walked off a ledge while holding left drifted back
        // to the right -- the "randomly flies off" report.
        _x += _speed;
    }

    /// <summary>Advance a fall and look for the ground, as the client does.</summary>
    private void Fall()
    {
        // MapleCharacter.cs:875-884, verbatim in shape:
        //
        //     if ((JumpState == JumpState.jsFalling) && (FallFlag))
        //     {
        //         Below = FootholdTree.Instance.FindBelow(
        //             new Vector2(X, Y - VelocityY - 6), ref BelowFH);
        //         if (Y >= Below.Y - 3)
        //         {
        //             Y = Below.Y;
        //             MaxFallSpeed = 10;
        //             JumpState = JumpState.jsNone;
        //             FH = BelowFH;
        //         }
        //     }
        //
        // Two things I had wrong. The probe reaches back over the whole frame's
        // travel (VelocityY + 6), not to a single point — so a foothold the
        // character passed through mid-frame still catches it. And landing
        // RAISES the terminal velocity to 10, which is why a jump arc in the
        // game is longer on the way down than a plain fall from the same height.
        float travel = _velocityY;
        _y += travel;
        _velocityY = Math.Min(_velocityY + JumpSpeed, _fallCap);

        FootholdPhysics.Foot? ground = _map.FindBelow(
            (int)Math.Round(_x), (int)Math.Floor(_y - travel - FallProbeSlack), falling: true);

        if (ground is { IsWall: false } g && _y >= (g.SurfaceAt((int)Math.Round(_x)) ?? int.MaxValue) - 3)
        {
            _foothold = g;
            _y = g.SurfaceAt((int)Math.Round(_x)) ?? _y;
            // The client raises the cap on landing (MapleCharacter.cs:879).
            _fallCap = LandedFallSpeed;
            _velocityY = 0;
            OnGround = true;
            _lastGroundId = g.Id;
            LeftGroundBecause = null;
            FellOutOfMap = false;
            return;
        }

        // The fall found no foothold. Measured on f1/Map/Map0/000030000.img:
        // walking right off the ledge at x=153.7 the character enters jsFalling,
        // speed decays to zero, and Y climbs forever -- 240 steps took it to
        // y=503 with bounds.bottom=409. The reference has no vertical counterpart
        // to its `Map.Right - 20` margin (CharacterPhysics.cs:350-351 is the
        // horizontal one), so the character leaves the world in the real client
        // too; what the reference does do is keep the CAMERA on the map
        // (MapleCharacter.cs:761-764, Camera.Y clamped to Map.Bottom - Height),
        // so the fall is never seen.
        //
        // For a simulator the character must not become unreachable. Stop it at
        // the map's floor.
        //
        // The floor is `bounds.bottom + FallOutSlack`, not `bounds.bottom`.
        // `bounds.bottom` IS the lowest foothold's own Y — MapBounds.Resolve
        // widens the rectangle to cover the footholds — so a character standing
        // on that foothold already has y == bounds.bottom and passes a `>` test
        // in a single step. Measured on f1/Map/Map9/922230001.img: spawn
        // y=511 on foothold id=2 with bounds.bottom=511, and the clamp did not
        // fire for 16 frames.
        if (_bounds is { } b && _y > b.Bottom + FallOutSlack)
        {
            _y = b.Bottom + FallOutSlack;
            _velocityY = 0;
            _fallCap = LandedFallSpeed;
            OnGround = false;              // nothing underfoot; do not fake standing
            FellOutOfMap = true;
            LeftGroundBecause =
                $"fell past the bottom of the map (bounds.bottom={b.Bottom}) with no foothold below";
        }
    }

    /// <summary>
    /// The last fall ran off the bottom of the map with no foothold to catch it.
    /// The character has been stopped at <c>bounds.bottom</c>; the client would
    /// simply have continued forever. Diagnostic, and used to report the defect.
    /// </summary>
    public bool FellOutOfMap { get; private set; }

    /// <summary>Where X stood on entry to the last climb, before the snap.</summary>
    public float ClimbEnterX { get; private set; }
    /// <summary>Where X stood immediately after the snap onto the ladder column.</summary>
    public float ClimbSnappedX { get; private set; }
    /// <summary>Where X stood at the climb's top/bottom test.</summary>
    public float ClimbExitX { get; private set; }

    /// <summary>Which branch ran on the last step. For diagnosis, not for logic.</summary>
        public string LastAction { get; private set; } = "";

            /// <summary>
            /// The swing stance this body is owed, for <see cref="AttackPoseTicks"/>
            /// more ticks. Read by the client, which asks the archive for that stance's
            /// frames.
            /// </summary>
            public string AttackStance { get; private set; } = "";

        /// <summary>
        /// Ticks of swing pose still owed, decremented once per <see cref="Step"/>.
        /// </summary>
        /// <remarks>
        /// A swing has to outlast the single tick that requested it, or the
        /// avatar shows one frame of it and snaps back. The attack key is an
        /// EDGE (livewalk.js latches it so one press is one swing), so nothing
        /// downstream keeps the pose alive — without a counter the action word
        /// would be `attack` for exactly one frame and the animation would never
        /// be seen at all.
        /// </remarks>
        public int AttackPoseTicks { get; private set; }

        /// <summary>
        /// Put the body into its swing pose for <paramref name="ticks"/> ticks.
        /// </summary>
        /// <remarks>
        /// Called by <c>MapSimSessionStore.Swing</c> at the point where a swing has
        /// actually been resolved, i.e. AFTER its on-ladder, airborne and
        /// weapon-box guards — so a blocked swing does not put the body in a pose
        /// it did not perform.
        /// </remarks>
        public void SwingPose(string stance, int ticks)
                {
                    AttackStance = stance;
                    AttackPoseTicks = Math.Max(AttackPoseTicks, ticks);
                    LastAction = "attack";
                }

    /// <summary>What the last <c>Walk</c> decided. For diagnosis, not for logic.</summary>
    public WalkDecision? LastWalk { get; private set; }

    /// <summary>One tick's worth of walking decisions.</summary>
    public sealed class WalkDecision
    {
        public string Foothold { get; init; } = "";
        public int MovedTo { get; init; }
        public int FootholdEnd { get; init; }
        public bool ReachedEnd { get; init; }
        public bool BlockedByWall { get; init; }
        public int? WallAt { get; init; }
        public string? GroundBelow { get; init; }
    }

    /// <summary>Jump from the current foothold.</summary>
    public bool Jump()
    {
        if (!OnGround)
            return false;
        // SpriteEngine.cs:1576-1581:
        //
        //     case JumpState.jsNone:
        //         if (DoJump)
        //         {
        //             jumpState = JumpState.jsJumping;
        //             VelocityY = -JumpHeight;      // <- NOT -JumpSpeed
        //         }
        //
        // The launch velocity is JumpHeight (9.5), not JumpSpeed (0.6). With
        // 0.6 the character rose 0.3px against a 0.6 gravity — a jump in name
        // only. Gravity acts the whole way, so the arc is
        // Height = v²/2g = 9.5²/1.2 ≈ 75px.
        _fallCap = MaxFallSpeed;
        _velocityY = -JumpHeight;
        Rising = true;
        OnGround = false;
        return true;
    }

    /// <summary>
    /// True while the character is still going up (the client's jsJumping).
    /// </summary>
    /// <remarks>
    /// jsJumping and jsFalling are separate states, not one "airborne" flag. It
    /// matters in two places: acceleration is allowed while rising but not while
    /// falling (MapleCharacter.cs:718, :735), and the ground query runs only
    /// when falling (MapleCharacter.cs:875). Collapsing them into a boolean
    /// loses both.
    /// </remarks>
    public bool Rising { get; private set; }

    /// <summary>
    /// The ladder to climb, using the client's box.
    /// </summary>
    /// <remarks>
    /// LadderRopes.cs:19: <c>P.X &gt; L.X - 10 &amp;&amp; P.X &lt; L.X + 10 &amp;&amp; P.Y &lt; L.Y2 + 12
    /// &amp;&amp; P.Y &gt; L.Y1 - 12</c>.
    ///
    /// This used to widen X to 40 and take the nearest ladder by distance. The
    /// justification in the old comment was a measurement — a ladder at x=1274
    /// with the chain ending at x=1256, 18px short — but that measurement was
    /// evidence of a bug in this file, not a property of the data: the character
    /// stopped at the chain's end because <see cref="DriftWhileFalling"/> did not
    /// exist, so it never drifted the last 18px toward the ladder. The client
    /// keeps moving and catches the ladder on its own box.
    ///
    /// With the drift in place the client's box is enough, and a widened one would
    /// let a character climb a ladder it is visibly nowhere near.
    /// </remarks>
    private FootholdPhysics.Ladder? FindLadderForClimb(int climb)
    {
        // MapleCharacter.cs:538 probes at (X, Y + OffY), and OffY is set by the
        // key that is held: -3 going up (:543), 0 going down (:570). The client
        // re-probes every frame with the current key's offset, so the ladder box
        // it tests is three pixels higher while climbing up than while down.
        //
        // Measured on this port without it: a ladder whose top sits three pixels
        // above the character's standing height was out of reach going up and
        // within reach going down, which is backwards.
        int offY = climb < 0 ? ClimbProbeUp : 0;
        return _map.FindLadder((int)Math.Round(X), (int)Math.Round(Y) + offY);
    }

    /// <summary>
    /// How far above the feet the ladder probe sits while climbing up.
    /// </summary>
    /// <remarks>
    /// MapleCharacter.cs:543, <c>OffY = -3</c>. It is an instance field in the
    /// client and stays -3 until a Down key press zeroes it, so a character that
    /// stops climbing keeps the last offset — but a walk script decides per step,
    /// so deriving it from the direction is both equivalent and self-documenting.
    /// </remarks>
    public const int ClimbProbeUp = -3;

    /// <summary>Vertical padding for the climb box; the client's is ±12.</summary>
    private const int ClimbReachY = 12;

    /// <summary>
    /// Climb, which is a different move from walking: the character leaves the
    /// foothold chain and rides a ladder or rope.
    /// </summary>
    /// <param name="moveLeft">
    /// Horizontal intent, kept for the step after the climb ends.
    /// </param>
    /// <param name="climb">-1 for up, +1 for down, 0 for neither.</param>
    /// <remarks>
    /// The client's ladder motion is its own path (MapleCharacter.cs:541-570):
    /// it snaps X onto the ladder, moves 1.5px per step vertically, and on
    /// reaching the top either leaves at the ladder's Y1 (a rope ends there) or
    /// finds a foothold below and steps onto it (uf == 1). Reimplemented rather
    /// than quoted because the state machine around it is a WinForms keyboard
    /// read that does not survive the port.
    /// </remarks>
    public void Climb(int moveLeft, int climb) =>
        ClimbOn(moveLeft, climb, FindLadderForClimb(climb));

    /// <summary>Climb a ladder already chosen by the caller.</summary>
    public void ClimbOn(int moveLeft, int climb, FootholdPhysics.Ladder? ladder)
    {
        Ticks++;
        VelocityYValue = climb * ClimbSpeedPerStep;

        if (climb == 0)
        {
            // Not climbing and not walking. The reference hangs (see the branch in
            // Step): with no key held it leaves Y and InLadder alone. Falling is
            // only right when the character is NOT on a ladder -- a body that is
            // merely airborne falls, a body that is on a rope waits.
            if (_ladder is not null)
            {
                VelocityYValue = 0;
                _velocityY = 0;
                return;
            }
            if (OnGround)
                LeaveGround("walked off the ladder with no direction held");
            else
                Fall();
            return;
        }

        if (ladder is not { } l)
        {
            // Not within reach of one: fall.
            LeaveGround("walked off the ladder out of reach of it");
            Fall();
            return;
        }

        _ladder = l;
        ClimbEnterX = _x;
        _x = l.X;                       // snap to the ladder column
        ClimbSnappedX = _x;
        _y += climb * ClimbSpeedPerStep;
        OnGround = false;
        _velocityY = 0;                 // climbing is not falling

        ClimbExitX = _x;

        // Reaching the top: a rope ends there, a ladder hands you to a foothold.
        if (_y <= l.Top && climb < 0)
        {
            // MapleCharacter.cs:554-566 uses two INDEPENDENT ifs on uf:
            //
            //     if (LadderRope.uf == 0) Y = LadderRope.Y1;
            //     if (LadderRope.uf == 1)
            //     {
            //         Below = FindBelow(new Vector2(X, Y - 5), ref BelowFH);
            //         Y = Below.Y; FH = BelowFH; InLadder = false;
            //     }
            //
            // They are not an else. A uf that is neither 0 nor 1 stops at Y1 the
            // way uf == 0 does; this port collapsed the test into one branch and
            // gave every non-zero value the uf == 1 behaviour.
            if (l.UpperFoothold == 0)
            {
                _y = l.Top;               // rope: stop at the top of the rope
                _velocityY = 0;
            }
            if (l.UpperFoothold == 1)
            {
                // Look BELOW the ladder's top, not above it. The client asks for
                // FindBelow(X, Y - 5) — the ledge a climber steps OFF onto is
                // under the top of the ladder, not over it.
                //
                // And it must not be the foothold already underfoot: measured, a
                // ladder at x=1274 standing on a foothold spanning 1211..1445
                // with its surface at y=-42 found THAT foothold again, and
                // `_x = a.X1` dragged the character back to 1211 every tick —
                // 18,000 steps ending where the walk began. A foothold only
                // counts as the way off if it is a DIFFERENT one.
                // The client's own query: the ledge a climber steps OFF onto is
                // under the top of the ladder. MapleCharacter.cs:556 uses Y - 5.
                FootholdPhysics.Foot? belowTop =
                    _map.FindBelow(l.X, (int)l.Top - 5, falling: true);

                string? standing = _foothold?.Id;
                if (belowTop is { } a && a.Id != standing)
                {
                    _foothold = a;
                    // X is NOT moved. The client's top exit assigns only Y, FH, Z and
                    // InLadder (MapleCharacter.cs:557-564) and never touches X, which
                    // stays on the column :549 put it on (`X = LadderRope.X`).
                    //
                    // `_x = a.X1` was here and it broke climbing DOWN, not just
                    // cosmetics: a ladder at x=2505 tops out on a foothold spanning
                    // 2469..2520, so the exit threw the character 36px left of the
                    // ladder. That is outside the client's own +-10 reach box, so
                    // FindLadderForClimb returned null every tick afterwards, Step
                    // fell through to the `_ladder is not null` resting branch, and
                    // holding Down did nothing at all -- measured: y pinned at the
                    // top for 400/400 frames, action stuck on `climb-top`. Climbing
                    // up was never affected, which is why it read as "climbing works".
                    //
                    // Measured on f4/Map/Map1/100000004.img: with `_x = a.X1` the
                    // three ladders whose top foothold starts left of the column
                    // (x=2505, 2272, 2533) all descended 0px from the top; only
                    // x=1936, whose top foothold starts AT the column, could come back
                    // down (268.5px).
                    _y = a.Y1;
                    _velocityY = 0;
                    OnGround = true;
                    LeftGroundBecause = null;
                }
                else if (belowTop is { } same)
                {
                    // The ladder tops out on the ground already under the
                    // character. Step up onto it where it actually is, rather
                    // than snapping X back to its far end.
                    _foothold = same;
                    _x = l.X;
                    _y = same.SurfaceAt(l.X) ?? l.Top;
                    _velocityY = 0;
                    OnGround = true;
                }
                else
                {
                    _y = l.Top;           // nothing to step onto; hold the top
                    _velocityY = 0;
                }
            }
            // _ladder stays set: the character is standing at the end of this
            // ladder, which Step() treats as a resting state rather than a new
            // climb request. Clearing it made every tick re-enter the climb.
            return;
        }

        // Reaching the bottom: MapleCharacter.cs:579-583.
        //
        //     if ((Y > LadderRope.Y2) && (InLadder))
        //     {
        //         InLadder = false;
        //         JumpState = JumpState.jsFalling;
        //     }
        //
        // Nothing else. No FindBelow, no X change, no foothold adoption. The
        // character simply stops climbing and falls — and X stays where the
        // ladder put it, not snapped to the left end of whatever happens to be
        // below.
        //
        // This port looked for a foothold under the ladder and then assigned
        // `_x = b.X1`, which threw the character to the far end of a segment it
        // had not walked. Measured on the fleet it changed where 1,063 walks
        // ended. The client never does that: leaving the ladder at the bottom is
        // an exit, and the fall that follows is an ordinary fall.
        if (_y > l.Bottom && climb > 0)
        {
            _ladder = null;
            OnGround = false;
            Rising = false;
            _velocityY = 0;
            LeftGroundBecause = $"climbed down past the bottom of the ladder at y={l.Bottom}";
            return;
        }
    }

    /// <summary>
    /// The ladder the character is currently riding, or null.
    /// </summary>
    public FootholdPhysics.Ladder? OnLadder => _ladder;

    private FootholdPhysics.Ladder? _ladder;

    /// <summary>The foothold the feet last rested on, so a fall cannot re-land on it.</summary>
    private string? _lastGroundId;

    /// <summary>
    /// Vertical climb speed, per step.
    /// </summary>
    /// <remarks>
    /// The client moves <c>Y -= 1.5f</c> per frame on a held key
    /// (MapleCharacter.cs:556), and there is no acceleration term — a ladder is
    /// a fixed rate in both directions.
    /// </remarks>
    public const float ClimbSpeedPerStep = 1.5f;

    /// <summary>Vertical speed while climbing. Negative is up.</summary>
    public float VelocityYValue { get; private set; }

    /// <summary>Step off deliberately, as Alt+Down does in the client.</summary>
    public void DropThrough()
    {
        if (!OnGround)
            return;

        // MapleCharacter.cs:889-899 — the drop only happens through a HORIZONTAL
        // foothold:
        //
        //     Below = FootholdTree.Instance.FindBelow(new Vector2(X, Y + 4), ref BelowFH);
        //     if (BelowFH.X2 > BelowFH.X1)
        //     {
        //         if (Below.Y != 99999)
        //         {
        //             JumpState = JumpState.jsFalling;
        //         }
        //     }
        //
        // The probe looks 4px BELOW the feet, and a sloped foothold is refused.
        // Dropping through a slope in the client does nothing at all, so a walk
        // script that assumes it can will diverge as soon as the ground tilts.
        FootholdPhysics.Foot? below = _map.FindBelow(
            (int)Math.Round(_x), (int)Math.Round(_y) + DropProbeSlack, falling: false);

        if (below is { } b && b.X2 > b.X1)
            LeaveGround("dropped through on purpose");
    }

    /// <summary>How far below the feet the drop-through probe reaches (MapleCharacter.cs:892).</summary>
    public const int DropProbeSlack = 4;

    private void LeaveGround(string why)
    {
        OnGround = false;
        Rising = false;
        LeftGroundBecause = why;
        _velocityY = 0;
    }

    /// <summary>
    /// The foothold <paramref name="id"/> that this one links to in the
    /// direction of travel.
    /// </summary>
    /// <remarks>
    /// Ids are not unique — 69 maps ship a duplicate — so the adjacency test is
    /// what picks the right one: a real link meets its neighbour at an endpoint.
    /// </remarks>
    /// <summary>The foothold with this id, or null.</summary>
    private FootholdPhysics.Foot? FindById(long id, FootholdPhysics.Foot from)
    {
        foreach (FootholdPhysics.Foot f in _map.Footholds)
        {
            if (long.TryParse(f.Id, out long fid) && fid == id && Meets(from, f))
                return f;
        }
        return null;
    }

    private FootholdPhysics.Foot? FindLinked(long id, FootholdPhysics.Foot from)
    {
        // Ids are unique only WITHIN a layer/group, not across the map — the
        // flattened list repeats numbers. The client never looks an id up at all:
        // CreateFootholds resolves the link while building the chain and stores it
        // as an object reference, so a link that does not meet its neighbour never
        // becomes one.
        //
        // Returning `byId` when nothing meets meant the character could adopt a
        // foothold from another floor, and since nothing clamped X to the current
        // foothold's extent it would then "walk" along it for the rest of its
        // budget. Measured: reported standing on 14 (x=0..51) while X ran to 721.
        foreach (FootholdPhysics.Foot f in _map.Footholds)
        {
            if (long.TryParse(f.Id, out long fid) && fid == id && Meets(from, f))
                return f;
        }
        return null;
    }

    /// <summary>Whether two footholds share an endpoint, as linked ones do.</summary>
    private static bool Meets(FootholdPhysics.Foot a, FootholdPhysics.Foot b) =>
        (b.X1 == a.X2 && b.Y1 == a.Y2) || (b.X1 == a.X1 && b.Y1 == a.Y1)
        || (b.X2 == a.X2 && b.Y2 == a.Y2) || (b.X2 == a.X1 && b.Y2 == a.Y1);

    /// <summary>
    /// The client's angle scale: a 256-step circle where 0 is straight down.
    /// </summary>
    /// <remarks>
    /// Kept because it IS the client's, and a map authored against the game
    /// should behave the same way here. The cost is a quantised direction — a
    /// foothold at 40° snaps to one of 256 — which is at most 0.7° and under a
    /// pixel over a foothold's length.
    /// </remarks>
    private static int Angle256(int srcX, int srcY, int destX, int destY)
    {
        double raw = Math.Atan2(destX - srcX, destY - srcY) * PiConv256;

        // Every axis-aligned foothold has an angle that IS a whole number -- a
        // flat rightward line is 64, exactly -- but `atan2(...) * 128/pi` lands a
        // few ulps short of it. Measured for (-1575,180)->(-1407,180):
        //
        //     raw          = -63.999999999175     (true value: -64)
        //     (int) raw    = -63                  (the cast truncates toward zero)
        //     -63 + 128    = 65
        //
        // so a flat line came out 1 step -- 1.4 degrees -- off horizontal. 64
        // gives Cos256(64) == 0 and the walk leaves y alone; 65 gives
        // Cos256(65) == -0.0245, and Walk()'s `Y -= Cos256(dir) * speed` then
        // sinks the character 0.0245px per step. Over a 168px foothold that is
        // +4.1px, and the handover's `Y = step.Y1` snapped it straight back to
        // 180 -- a 1.3 second sawtooth on flat ground, which is the report
        // "the character drifts upward, then rights itself about two seconds
        // later". Walking left showed the same drift mirrored, because _speed
        // is signed and the wrong Cos256 applies either way.
        //
        // Only values already within 1e-6 of an integer are snapped, so genuine
        // angles keep the truncating behaviour they always had.
        double nearest = Math.Round(raw);
        if (Math.Abs(raw - nearest) < 1e-6) raw = nearest;

        return (int)raw + 128;
    }

    /// <summary>
    /// Sine on the client's 256-step circle (SpriteEngine.cs:778).
    /// </summary>
    /// <remarks>
    /// The client computes it as <c>CosTable256[(i + 192) &amp; 255]</c>, i.e. the
    /// cosine table offset by three quarters of a turn. Written out here so the
    /// table is not a second source of truth, but the masking is explicit:
    /// <c>(i + 192) &amp; 255</c> is the index, and only then is it scaled.
    /// </remarks>
    private static double Sin256(int i) => Cos256((i + 192) & 255);

    /// <summary>Cosine on the client's 256-step circle (SpriteEngine.cs:774).</summary>
    private static double Cos256(int i) => Math.Cos(i * Math.PI / 128.0);
}
