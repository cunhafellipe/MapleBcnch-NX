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
    public void Step(int moveLeft)
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

        if (!OnGround)
        {
            Fall();
            return;
        }

        Walk(moveLeft);
    }

    /// <summary>
    /// Move along the current foothold and step to the next at its end.
    /// </summary>
    private void Walk(int moveLeft)
    {
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

        bool reachedEnd = moveLeft >= 0 ? _x >= fh.X2 : _x <= fh.X1;
        if (!reachedEnd)
            return;

        // Past the end of this foothold. Take the next one if it exists and is
        // walkable, otherwise the character has run out of ground.
        long nextId = moveLeft >= 0 ? fh.Next : fh.Prev;
        FootholdPhysics.Foot? next = nextId == 0 ? null : FindLinked(nextId, fh);

        if (next is not { IsWall: false } step)
        {
            LeaveGround(
                moveLeft >= 0
                    ? $"foothold {fh.Id} ends at x={fh.X2} with no walkable foothold after it"
                    : $"foothold {fh.Id} ends at x={fh.X1} with no walkable foothold before it");
            return;
        }

        _foothold = step;
        _x = moveLeft >= 0 ? step.X1 : step.X2;
        _y = moveLeft >= 0 ? step.Y1 : step.Y2;
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

            FootholdPhysics.Foot? ground = _map.FindBelow(
                (int)Math.Round(_x), (int)Math.Floor(_y), falling: true);

            if (ground is { IsWall: false } g
                && _y >= (g.SurfaceAt((int)Math.Round(_x)) ?? int.MaxValue) - 3)
            {
                _foothold = g;
                _x = g.X1;
                _y = g.Y1;
                _velocityY = 0;
                OnGround = true;
                LeftGroundBecause = null;
                return;
            }
        }
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