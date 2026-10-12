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

    /// <summary>Radians per angle unit, for the client's 256-step angle scale.</summary>
    private const float PiConv256 = -40.743665431f;

    /// <summary>Half the character's width, for wall checks (LadderRopes.cs:18-27).</summary>
    public const int BodyHalfWidth = 10;

    private readonly FootholdPhysics _map;
    private FootholdPhysics.Foot? _foothold;
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
    public void Step(int moveLeft, int climb = 0)
    {
        Ticks++;

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

        LastAction = "walk";

        // Climbing takes over whenever it is asked for and a ladder is in reach.
        // The client does the same: the Up/Down branches run before the walk
        // branches and set InLadder, and the walk is skipped (MapleCharacter.cs:541).
        //
        // The reach test is deliberately WIDER than the client's ±10. A ladder
        // spans a column and the character approaches it along a foothold that
        // may stop short of it — measured, a ladder at x=1274 and a foothold
        // ending at x=1256 left the character 18px away, outside the client's
        // box, so the climb could never begin and the walk ran to the end of
        // the tick budget instead. A ladder you can see is a ladder you climb.
        if (climb != 0 && FindLadderForClimb() is { } ladderHere)
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

        int direction = Angle256(fh.X1, fh.Y1, fh.X2, fh.Y2);

        // The client's own expression: Sin/Cos of the foothold's direction, not a
        // horizontal move followed by a ground query. This is why slopes work.
        _x += (float)(Sin256(direction) * _speed);
        _y -= (float)(Cos256(direction) * _speed);

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
        FootholdPhysics.Foot? ground = _map.FindBelow(
            (int)Math.Round(_x), (int)Math.Round(_y), falling: false);

        // Probed a few pixels AHEAD, not at the body: the wall the character is
        // about to meet is not yet under it.
        int ahead = (int)Math.Round(_x) + (moveLeft > 0 ? 4 : -4);
        FootholdPhysics.Foot? wall = moveLeft > 0
            ? _map.FindWall(ahead, (int)Math.Round(_y), movingRight: true)
            : moveLeft < 0
                ? _map.FindWall(ahead, (int)Math.Round(_y), movingRight: false)
                : null;

        if (wall is { } w2 && ground is { } g2 && w2.Z == g2.Z)
        {
            if (moveLeft > 0 && _x >= w2.X1)
            {
                _x = w2.X1 - 1;
                _speed = 0;
            }
            else if (moveLeft < 0 && _x <= w2.X1)
            {
                _x = w2.X1 + 1;
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
        const float EdgeSlack = 10f;

        long leadId = moveLeft >= 0 ? fh.Next : fh.Prev;
        float fallEdge = moveLeft >= 0 ? 999999f : -999999f;
        if (leadId == 0)
            fallEdge = moveLeft >= 0 ? fh.X2 + EdgeSlack : fh.X1 - EdgeSlack;

        // A wall dropping away on the far side is the real edge.
        FootholdPhysics.Foot? lead = leadId == 0 ? null : FindById(leadId);
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
            GroundBelow = ground?.Id,
        };

        if (!reachedEnd)
            return;

        // Past the end of this foothold. Take the next one if it exists and is
        // walkable, otherwise the character has run out of ground.
        long nextId = moveLeft >= 0 ? fh.Next : fh.Prev;
        FootholdPhysics.Foot? next = nextId == 0 ? null : FindLinked(nextId, fh);

        if (next is not { IsWall: false } step)
        {
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

        _x += _speed * moveLeft;
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
        }
    }

    /// <summary>Where X stood on entry to the last climb, before the snap.</summary>
    public float ClimbEnterX { get; private set; }
    /// <summary>Where X stood immediately after the snap onto the ladder column.</summary>
    public float ClimbSnappedX { get; private set; }
    /// <summary>Where X stood at the climb's top/bottom test.</summary>
    public float ClimbExitX { get; private set; }

    /// <summary>Which branch ran on the last step. For diagnosis, not for logic.</summary>
    public string LastAction { get; private set; } = "";

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
    private FootholdPhysics.Ladder? FindLadderForClimb()
    {
        return _map.FindLadder((int)Math.Round(X), (int)Math.Round(Y));
    }

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
        ClimbOn(moveLeft, climb, FindLadderForClimb());

    /// <summary>Climb a ladder already chosen by the caller.</summary>
    public void ClimbOn(int moveLeft, int climb, FootholdPhysics.Ladder? ladder)
    {
        Ticks++;
        VelocityYValue = climb * ClimbSpeedPerStep;

        if (climb == 0)
        {
            // Not climbing and not walking: fall.
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
            if (l.UpperFoothold == 0)
            {
                _y = l.Top;               // rope: stop at the top of the rope
                _velocityY = 0;
            }
            else
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
                    _x = a.X1;
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

        // Reaching the bottom: step off onto whatever is below.
        if (_y >= l.Bottom && climb > 0)
        {
            FootholdPhysics.Foot? below = _map.FindBelow(l.X, (int)_y, falling: true);
            if (below is { } b)
            {
                _foothold = b;
                _x = b.X1;
                _y = b.Y1;
            }
            _velocityY = 0;
            _ladder = null;
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
    private FootholdPhysics.Foot? FindById(long id)
    {
        foreach (FootholdPhysics.Foot f in _map.Footholds)
        {
            if (long.TryParse(f.Id, out long fid) && fid == id)
                return f;
        }
        return null;
    }

    private FootholdPhysics.Foot? FindLinked(long id, FootholdPhysics.Foot from)
    {
        FootholdPhysics.Foot? byId = null;
        foreach (FootholdPhysics.Foot f in _map.Footholds)
        {
            if (!long.TryParse(f.Id, out long fid) || fid != id)
                continue;

            byId ??= f;
            if (Meets(from, f))
                return f;
        }
        return byId;
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
    private static int Angle256(int srcX, int srcY, int destX, int destY) =>
        (int)(Math.Atan2(destX - srcX, destY - srcY) * PiConv256) + 128;

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