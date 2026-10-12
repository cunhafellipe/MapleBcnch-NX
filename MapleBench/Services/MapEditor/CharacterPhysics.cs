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
            _speed += Acceleration * moveLeft;
            _speed = Math.Clamp(_speed, -MaxWalkSpeed, MaxWalkSpeed);
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
            LastAction = "fall";
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

    /// <summary>Advance a fall and look for the ground, as the client does.</summary>
    private void Fall()
    {
        _velocityY = Math.Min(_velocityY + JumpSpeed, MaxFallSpeed);

        // Sub-stepped: at terminal velocity the character moves 8px per step,
        // and a single point sample would step straight over a foothold.
        float remaining = _velocityY;
        while (remaining > 0f)
        {
            float slice = Math.Min(remaining, 1f);
            _y += slice;
            remaining -= slice;

            // FindBelow with falling:true accepts a surface at exactly Y, so
            // once the feet are level with one it lands there — which is right.
            // What is NOT right is letting a fall re-land on the ledge it just
            // left, every frame, so the character hovers instead of falling and
            // no fall ever lasts long enough to be measured. Measured: on
            // 610030510 the character fell for 18,000 frames with its position
            // frozen, and the airborne counter stayed at zero.
            //
            // The rule that makes it a fall: once airborne, only a foothold the
            // character was not already standing on can catch it.
            FootholdPhysics.Foot? ground = _map.FindBelow(
                (int)Math.Round(_x), (int)Math.Floor(_y), falling: true);

            if (ground is { IsWall: false } g
                && _y >= (g.SurfaceAt((int)Math.Round(_x)) ?? int.MaxValue) - 3
                && g.Id != _lastGroundId)
            {
                _foothold = g;
                _y = g.SurfaceAt((int)Math.Round(_x)) ?? _y;
                _velocityY = 0;
                OnGround = true;
                _lastGroundId = g.Id;
                LeftGroundBecause = null;
                return;
            }
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
        _velocityY = -JumpSpeed;
        OnGround = false;
        return true;
    }

    /// <summary>
    /// The ladder to climb, using a wider box than the client's.
    /// </summary>
    /// <remarks>
    /// The client reaches ±10 in X (LadderRopes.cs:18-27), which is right for a
    /// player steering with a key. A walk is a script that arrives wherever the
    /// chain ended, and chains routinely stop short of the column they serve:
    /// measured, a ladder at x=1274 with the chain stopping at x=1256. Keeping
    /// the client's box would mean the climb is unreachable on exactly the maps
    /// where a ladder is the way onward — which is every map that has one.
    /// </remarks>
    private FootholdPhysics.Ladder? FindLadderForClimb()
    {
        FootholdPhysics.Ladder? near = _map.FindLadder(
            (int)Math.Round(X), (int)Math.Round(Y));
        if (near is not null)
            return near;

        // Nearest ladder by horizontal distance, then vertical overlap.
        FootholdPhysics.Ladder? best = null;
        int bestDx = ClimbReachX;
        foreach (FootholdPhysics.Ladder l in _map.Ladders)
        {
            int dx = Math.Abs(l.X - (int)Math.Round(X));
            if (dx >= bestDx)
                continue;
            if ((int)Math.Round(Y) < l.Top - ClimbReachY || (int)Math.Round(Y) > l.Bottom + ClimbReachY)
                continue;
            bestDx = dx;
            best = l;
        }
        return best;
    }

    /// <summary>Horizontal reach for a scripted climb, wider than the client's ±10.</summary>
    private const int ClimbReachX = 40;

    /// <summary>Vertical padding for that reach; the client's is ±12.</summary>
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
        if (OnGround)
            LeaveGround("dropped through on purpose");
    }

    private void LeaveGround(string why)
    {
        OnGround = false;
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