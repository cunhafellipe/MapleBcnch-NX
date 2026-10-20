using MapleBench.Services.Look;
using MapleBench.Services.MapEditor;
using MapleBench.Services.MapModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// NOTE ON CITATIONS. The `.cs`/`.js` citations below name MapleNecrocer
// (C:\MUWORK\GAME\_necrocer_src\MapleNecrocer) -- this port's own source,
// which has no counterpart in the authoritative reference: clean-client
// (C:\MUWORK\GAME\MPSH\clean-client\src) contains zero `.cs` files. Where a
// MapleNecrocer citation disagrees with clean-client, clean-client wins -- see
// pm/PARITY.md, "The C++ wins". These were not re-audited for content; some
// record a port's deliberate divergence.

namespace MapleBench.Services.Simulate;

/// <summary>
/// Which weapon image the character should be holding from now on.
/// </summary>
/// <remarks>
/// A missing/blank <c>equippedWeapon</c> DISARMS rather than defaulting to the
/// weapon a session happens to have started with, because unarmed is a real state
/// with its own reference behaviour (CharLook.cpp:594: no weapon means no swing
/// pose) and defaulting would make it impossible to reach.
/// </remarks>
public sealed class MapSimWeaponRequest
{
	public string? EquippedWeapon { get; set; }
}

/// <summary>The whole equipped set, as one replacement.</summary>
/// <remarks>
/// A REPLACEMENT, not a patch. A client that posted only what changed would leave
/// this server unable to tell "removed" from "never equipped", and the sheet it
/// builds would keep stat contributions from items that are off the character —
/// the sheet is <c>base + sum over the inventory</c> (Player.cpp:309-313), so a
/// stale entry is a wrong number, not a stale picture.
///
/// Slot names are the client's own (<c>Cap-1</c>, <c>Coat</c>, <c>Weapon-1</c>),
/// not the reference's <c>EquipSlot::Id</c> ordinals: those are a wire protocol
/// (<c>EquipSlot.h:27-60</c>), and this route is not that wire.
/// </remarks>
public sealed class MapSimEquipRequest
{
	public Dictionary<string, string>? Items { get; set; }
}

/// <summary>N physics steps for the price of one round trip.</summary>
/// <remarks>
/// <see cref="First"/> is the input for the first step and <see cref="Then"/> for the
/// rest, so the browser sends its held keys ONCE. An OMITTED <c>then</c> and an EMPTY
/// <c>then</c> mean the same thing to a caller and must mean the same thing here: the
/// level check is <c>MapSimInput.AnyHeld()</c>, never <c>?? First</c>, because an empty
/// JSON object deserializes to an all-false input that is NOT null.
/// </remarks>
public sealed class MapSimBatchRequest
{
	public int? Count { get; set; }

	/// <summary>
	/// W-089. Claim this session to drive it frame by frame: the server's own
	/// clock stops advancing it.
	/// </summary>
	/// <remarks>
	/// For a caller that MEASURES. A 250 ms lease cannot express "I will send
	/// the next frame when I have decided what to send" — MEASURED 2026-10-18, a
	/// 200 ms gap between two requests is enough for the background clock to
	/// inject ten frames, with the request latency unchanged at 2.1 ms. The
	/// browser does not set this, so the browser's behaviour is unchanged.
	/// </remarks>
	public bool Manual { get; set; }

	public MapSimInput? First { get; set; }

	public MapSimInput? Then { get; set; }
}

/// <summary>A portal the frame the body just entered belongs to.</summary>
public sealed record PortalDto(string Name, string ToName, long ToMap, long Type);

/// <summary>Which map a portal sends the body to, and where it lands.</summary>
public sealed record MapTransferDto(string From, string To, long MapId, string PortalName, int X, int Y);

public sealed class MapSimInput
{
	public bool Left { get; set; }

	public bool Right { get; set; }

	public bool Up { get; set; }

	public bool Down { get; set; }

	public bool Jump { get; set; }

	public bool Attack { get; set; }

	/// <summary>
	/// The skill to cast this frame, or null.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A skill id rather than a key, because the key already did its work: the
	/// browser polls its own bindings and resolves the letter to an id before it
	/// sends anything, which is exactly where the reference does it
	/// (<c>Skill.cs:321-329</c> maps a pressed key through <c>Skill.HotKeyList</c>
	/// and calls <c>Skill.Create</c> with an ID).
	/// </para>
	/// <para>
	/// Null is not "no skill" by accident ??it is the ordinary state of every frame
	/// of a walk. <see cref="AnyHeld"/> deliberately does NOT count it, because that
	/// method exists to answer "is a direction key down" for the /frames hold logic
	/// (see its own use at SimulatorEndpoints.cs), and a cast is not a direction.
	/// </para>
	/// </remarks>
	public string? SkillId { get; set; }

	public bool AnyHeld()
	{
		if (!Left && !Right && !Up)
		{
			return Down;
		}
		return true;
	}
}

public sealed class MapSimDamageNumber
{
	public string MobId { get; set; } = "";

	public int Value { get; set; }

	public float X { get; set; }

	public float Y { get; set; }

	public bool Miss { get; set; }
}

    /// <summary>One attack entry as the frame publishes it. W-070.</summary>
    public sealed class MapSimAttackEntry
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double ElapsedMs { get; set; }
        public double DelayMs { get; set; }
        public double LifetimeMs { get; set; }
        public int Type { get; set; }
        public string BallAction { get; set; } = "";
        public int BallFrameCount { get; set; }
        public int BallFrameIndex { get; set; }
        public bool Flipped { get; set; }
    }

    /// <summary>
    /// The effect a cast draws ON this mob, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Mob::show_effect</c> (<c>Mob.cpp:1536-1559</c>) adds one animation to the
    /// MOB's own effect layer, so it is drawn at the mob's position and never at
    /// the caster's. It is published here per mob for exactly that reason: the
    /// mob id is implied by the <see cref="MapSimMobFrame"/> this list hangs off,
    /// and the mob's own position is already on that same entry, so the client
    /// anchors the blit where the reference anchors it.
    /// </para>
    /// <para>
    /// <c>Flipped</c> is the ATTACKER's, not the mob's. <c>SkillHitEffect.h:47</c>
    /// takes the flip as a parameter and every caller passes <c>user.flip</c>
    /// (<c>SkillHitEffect.cpp:28</c>, <c>SkillHitEffect.h:97</c>,
    /// <c>Attack.h:172</c>) — the caster's own facing, so a mob that turns to face
    /// its attacker does not turn the hit art with it.
    /// </para>
    /// </remarks>
    public sealed class MapSimMobHitEffect
    {
        /// <summary>
        /// The <c>pos</c> node: where on the mob the effect hangs.
        /// <c>0</c> is the mob's HEAD, not "no shift" (<c>Mob.cpp:1544-1547</c>).
        /// </summary>
        public int Pos { get; set; }

        /// <summary>
        /// The <c>z</c> node: negative draws BEHIND the mob, 0 and up in front
        /// (<c>EffectLayer.cpp:22-42</c>).
        /// </summary>
        public int Z { get; set; }

        /// <summary>How many frames the animation holds.</summary>
        public int Frames { get; set; }

        /// <summary>
        /// The anchor to shift by, already in map coordinates — the mob's own head
        /// position when <see cref="Pos"/> is 0 and (0,0) otherwise.
        /// </summary>
        /// <remarks>
        /// <c>Mob.cpp:1546</c> takes the shift from <c>get_head_position</c>, which
        /// this server already computes for the damage number a few lines above
        /// (<c>geometry.HeadAt</c>). Sending the shift rather than a "use the head"
        /// flag keeps one resolution of "where is this mob's head" in the code that
        /// already has the answer, instead of adding a second one in the browser.
        /// </remarks>
        public double ShiftX { get; set; }

        /// <summary>See <see cref="ShiftX"/>.</summary>
        public double ShiftY { get; set; }

        /// <summary>The attacker's facing: true draws right, false draws left.</summary>
        public bool Flipped { get; set; }

        /// <summary>
        /// Which cast this effect belongs to, so a new cast restarts the animation
        /// even when it is the same skill cast twice.
        /// </summary>
        /// <remarks>
        /// The same reason <see cref="MapSimFrame.SkillCastSeq"/> exists, and the
        /// same bug it fixed: keying the sprite clock on the skill id leaves the
        /// second cast of a skill animating from the first cast's stale clock
        /// (livewalk.js, W-075/W-083).
        /// </remarks>
public int CastSeq { get; set; }

		/// <summary>
		/// The clock factor this effect runs at. 1.0 for a mob's hit flash, and
		/// 1.0 for 3111003's ground decal as well.
		/// </summary>
		/// <remarks>
		/// Both go through the three-argument <c>EffectLayer::add</c>
		/// (<c>EffectLayer.cpp:68-71</c>), whose speed is <c>1.0f</c>. It is on
		/// the wire because 3111003's decal is a DIFFERENT effect that happens to
		/// ride this list (see <c>AddTargetTiles</c>), and a caster-side tile in
		/// the same animation set runs at <c>get_real_attackspeed()</c> — so a
		/// client reading the rate off the caster for everything would play a
		/// mob-side decal at the wrong speed.
		/// </remarks>
		public float Speed { get; set; } = 1f;

		/// <summary>One image of the effect, as the plan read it from the archive.</summary>
        public SkillHitSprite Sprite { get; set; } = new(0, 0, "", "", 0, Array.Empty<PointDto>(), Array.Empty<int>(), 0, 0);
    }

    /// <summary>
    /// One ground decal, the caster-side or the victim-side half of a skill's
    /// <c>tile</c> node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TileFootHoldUseEffect</c> spawns these two ways and the two are
    /// different calls, not one call with a flag:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>apply(Char&amp;)</c> → <c>Char::show_attack_effect_at(anim, 4, rel_pos)</c>
    /// (<c>SkillUseEffect.cpp:257</c>), which is
    /// <c>effects.add(anim, DrawArgument(position, facing_right), z, get_real_attackspeed())</c>
    /// (<c>Char.cpp:653-658</c>). <b>z is 4</b> — in front, on its own key — and
    /// the clock runs at the CASTER's attack speed.
    /// </item>
    /// <item>
    /// <c>apply_at_target(Mob&amp;)</c> → <c>Mob::show_effect(anim, 1, 4, false)</c>
    /// (<c>SkillUseEffect.cpp:211</c>). <c>pos == 1</c> leaves <c>shift</c> at
    /// <c>(0,0)</c> (<c>Mob.cpp:1546-1548</c>), i.e. the mob's own feet, the flip
    /// argument is <c>false</c>, and <c>EffectLayer::add</c>'s three-argument
    /// overload (<c>EffectLayer.cpp:68-71</c>) uses speed <b>1.0</b> — real time,
    /// not the caster's attack speed.
    /// </item>
    /// </list>
    /// <para>
    /// That is why <see cref="Speed"/> travels on the wire: the two halves of one
    /// skill's art play at different rates, and a client that read the rate off
    /// the caster for both would play a victim-side decal at the wrong speed.
    /// </para>
    /// </remarks>
    public sealed class MapSimTileEffect
    {
        /// <summary>
        /// The decal's offset from its anchor, in map units, already in the
        /// reference's own terms.
        /// </summary>
        /// <remarks>
        /// <para>
        /// On the caster side it is <c>rel_pos</c> — <c>(x - user_pos.x(),
        /// ground_y + jitter_y - user_pos.y)</c> from
        /// <c>SkillUseEffect.cpp:256</c>, or <c>(0,0)</c> for the fallback at
        /// <c>:262</c>. On the mob side it is always <c>(0,0)</c>, because
        /// <c>pos == 1</c> means "no shift" (<c>Mob.cpp:1546</c>).
        /// </para>
        /// <para>
        /// RELATIVE, not world, because the reference's <c>DrawArgument</c> takes
        /// the position as a child of the character's own <c>absp</c> and adds it
        /// there (<c>Sprite.cpp:27-31</c>, <c>absargs = stateargs + parentpos</c>).
        /// Sending world coordinates would freeze a decal on the map while the
        /// character walks away from it.
        /// </para>
        /// </remarks>
        public double ShiftX { get; set; }

        /// <summary>See <see cref="ShiftX"/>.</summary>
        public double ShiftY { get; set; }

        /// <summary>
        /// The caster's facing decides the mirror: <c>DrawArgument(position,
        /// facing_right)</c> (<c>Char.cpp:657</c>).
        /// </summary>
        /// <remarks>
        /// <b>True = mirrored</b>, which is what the client's rectangle maths
        /// wants, and it is the same convention <see cref="MapSimMobHitEffect.Flipped"/>
        /// already travels under.
        /// </remarks>
        public bool Flipped { get; set; }

        /// <summary>The <c>z</c> the effect layer was given.</summary>
        /// <remarks>
        /// Always <b>4</b> on both paths — a literal in the reference
        /// (<c>SkillUseEffect.cpp:211,257</c>), not a node read from the archive.
        /// It is on the wire rather than assumed by the client for the same
        /// reason it is on <see cref="MapSimMobHitEffect"/>: it is a real
        /// layer index, and a client that invented one would silently draw the
        /// decals in the wrong half of the effect pass.
        /// </remarks>
        public int Z { get; set; }

        /// <summary>Which cast this decal belongs to, so a re-cast restarts it.</summary>
        public int CastSeq { get; set; }

        /// <summary>
        /// The clock factor: the caster's real attack speed for a decal on the
        /// caster, <c>1.0</c> for one on a mob.
        /// </summary>
        public float Speed { get; set; } = 1f;

        /// <summary>One image of the decal, as the plan read it from the archive.</summary>
        public SkillTileSprite Sprite { get; set; } =
            new(0, "", "", 0, Array.Empty<PointDto>(), Array.Empty<int>());
    }

public sealed class MapSimMobFrame
{
	public string MobId { get; set; } = "";

	public float X { get; set; }

	public float Y { get; set; }

	public string Foothold { get; set; } = "";

	public int Facing { get; set; }

	public int Direction { get; set; }

	public bool OnGround { get; set; }

	public int Reversals { get; set; }

	public string Stance { get; set; } = "";

	public int FrameIndex { get; set; }

	public int FrameCount { get; set; }

	public int Stun { get; set; }

	public bool Dead { get; set; }

	public int Hp { get; set; }

	public int MaxHp { get; set; }

	public bool Dying { get; set; }

	public string SpritePath { get; set; } = "";

	public string Kind { get; set; } = "m";

    /// <summary>The mob's level, from <c>info/level</c>. 0 when the archive is silent.</summary>
    /// <remarks>
    /// Read once per spawn and cached here rather than per frame, for the same
    /// reason <see cref="NpcLabels"/> is: a frame that asked the archive for a
    /// level sixty times a second is a different program from the one being ported.
    /// </remarks>
    public int Level { get; set; }

    /// <summary>
    /// The mob's name, from the string pool's <c>String/Mob.img/&lt;id&gt;/name</c>.
    /// </summary>
    /// <remarks>
    /// NOT from the mob image: MEASURED, <c>f5/2220000.img/info</c> has 27 children
    /// and <c>name</c> is not one of them -- that node holds combat stats only. The
    /// name lives in String.nx (<c>f3/Mob.img/2220000/name</c>), which is
    /// what <c>StringPoolService.GetMobName</c> already scans.
    /// </remarks>
    public string MobName { get; set; } = "";

	/// <summary>
	/// What this NPC is saying right now, or empty when it is not.
	/// </summary>
	/// <remarks>
	/// Carried on the NPC's own frame entry so the caller does not have to know
	/// which mobs are NPCs — the same reason <see cref="NpcName"/> lives here.
	/// The text is the archive's, resolved once at spawn; only WHEN it is shown
	/// is decided per frame, by the reference's own pool
	/// (<c>Npc.cpp:478-484</c>).
	/// </remarks>
	public string Speech { get; set; } = "";

	/// <summary>
	/// Frames left on the balloon. 0 means nothing is being said.
	/// </summary>
	/// <remarks>
	/// COUNTED IN FRAMES, never wall-clock: the reference's
	/// <c>CHAT_DURATION = 3000</c> is milliseconds against a 16.67ms step
	 /// (<c>Npc.h:136</c>), and a replay that used the wall clock would differ by
	/// the client's frame rate. 3000ms is 180 frames at 60Hz.
	/// </remarks>
	public int SpeechFrames { get; set; }

	/// <summary>
	/// The hit knockback applied on this frame, in pixels; 0 on every other frame.
	/// </summary>
	/// <remarks>
	/// A field rather than something a caller infers. A struck mob also walks
	/// toward its attacker (Mob.cs:434-460), so by the time a caller reads its
	/// position the walk has contributed thousands of pixels and the 2.17px recoil
	/// is invisible inside it — MEASURED: a probe reading the mob's x saw
	/// -2164.567px on the first frame that moved at all, and could not tell the
	/// recoil from the walk. Mob.cpp:595-598, KBSPEED 130.0 (Mob.h:467).
	/// </remarks>
	public double KnockbackPx { get; set; }

	/// <summary>
	/// The facing the knockback was computed from — 1 right, -1 left, 0 none.
	/// </summary>
	/// <remarks>
	/// Reported because the struck mob is redirected toward its attacker on the
	/// same hit (Mob.cs:434-460), so the facing on a LATER frame is not the one
	/// <c>Mob.cpp:597</c> used. Without this a caller checking the sign is
	/// checking it against a value the code never read.
	/// </remarks>
	public int KnockbackFacing { get; set; }

    /// <summary>
    /// The attack entries this mob has in flight right now, and the FIRST one's
    /// position and clock.
    /// </summary>
    /// <remarks>
    /// Reported so a projectile can be SEEN rather than inferred from a damage
    /// number. A type-2 entry sits at its launch point for the whole <c>delay</c>
    /// and then travels to <c>finish</c> over <c>lifetimeMs</c>
    /// (<c>MobAttack.cpp:199-210</c>), so its position alone distinguishes a
    /// travelling attack from an instant cone. Every other source of damage to the
    /// character — contact (<c>Stage.cpp:809</c>), another mob — is invisible here,
    /// which is the point: those are not the projectile.
    /// </remarks>
    public int AttackEntryCount { get; set; }

    public double AttackEntryX { get; set; }

    public double AttackEntryY { get; set; }

    public double AttackEntryElapsedMs { get; set; }

    public double AttackEntryDelayMs { get; set; }

    public double AttackEntryLifetimeMs { get; set; }

    public int AttackEntryType { get; set; }

    /// <summary>
    /// The sprite-cache action to draw for the in-flight projectile, or "".
    /// <c>Mob.cpp:163-165</c> / <c>Mob.cpp:1121-1125</c>.
    /// </summary>
    /// <remarks>
    /// Empty while the entry is still inside its <c>delay</c>, because the
    /// reference draws the ball only once <c>entry.elapsed &gt;= entry.delay</c> —
    /// there is nothing in the air until then.
    /// </remarks>
    public string AttackEntryBallAction { get; set; } = "";

    public int AttackEntryBallFrameCount { get; set; }

    public int AttackEntryBallFrameIndex { get; set; }

    /// <summary>
    /// Every attack entry in flight, not just the first. W-070.
    /// </summary>
    /// <remarks>
    /// The scalars above describe <c>_entries[0]</c>, which is not always the one
    /// carrying art: <c>4220000</c> has a flying <c>attack1</c> and a static
    /// <c>attack2</c>, and while it used attack2 the frame reported an entry with no
    /// ball for the whole fight. The reference draws one ball per entry past its
    /// delay (<c>Mob.cpp:1118-1126</c>), so this list is the faithful shape.
    /// </remarks>
    public List<MapSimAttackEntry> AttackEntries { get; set; } = new();

    /// <summary>The launch facing, which mirrors the ball art. 1 right, 0 left.</summary>
    public bool AttackEntryFlipped { get; set; }

    /// <summary>
    /// The effects a cast drew ON this mob, emptied every frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A "now", exactly like <see cref="MapSimFrame.Damage"/>: the reference adds
    /// the effect to the mob's layer at the moment of the hit
    /// (<c>Mob.cpp:1558</c>) and the mob's own effect layer advances and retires it
    /// on its own. This is published for the frame the hit lands on and cleared when
    /// the frame is built, so the client starts the animation when the hit is
    /// published and lets it end on its own delays.
    /// </para>
    /// <para>
    /// It was empty for the life of this port: <c>SkillCastPlan.HitEffects</c> was
    /// built and read by nothing, so a cast drew its effect on the CHARACTER and
    /// the mob that was struck flashed nothing.
    /// </para>
    /// </remarks>
    public List<MapSimMobHitEffect> HitEffects { get; set; } = new();

	public string FootholdLayer { get; set; } = "";

	/// <summary>
	/// The label an NPC shows above itself. Empty for a mob, and empty for an NPC
	/// the string pool has no name for.
	/// </summary>
	/// <remarks>
	/// <c>MapleNecrocer/Client/Npc.cs:171-186</c> resolves all three once, at
	/// spawn, and never again — so these are resolved at spawn here too, not per
	/// frame. <c>NpcFunc</c> empty is how the reference says "no function line"
	/// (<c>Npc.cs:174-175</c> tests the string, not the node).
	/// </remarks>
	public string NpcName { get; set; } = "";

	/// <summary>The NPC's function line, or empty when it has none.</summary>
	public string NpcFunc { get; set; } = "";

	/// <summary>
	/// Whether the NPC's own archive forbids showing its name
	/// (<c>Npc/&lt;id&gt;.img/info/hideName</c>).
	/// </summary>
	/// <remarks>
	/// The id line is NOT gated on this: <c>NpcText.DoDraw</c> draws the id under
	/// <c>Map.ShowID</c> regardless, and only the NAME returns early
	/// (<c>Npc.cs:279-280</c>). An NPC that hides its name still shows its id.
	/// </remarks>
	public bool NpcHideName { get; set; }
}

public sealed class MapSimBounds
{
	public float Left { get; set; }

	public float Top { get; set; }

	public float Right { get; set; }

	public float Bottom { get; set; }

	public int ViewportHeight { get; set; }
}

public sealed class MapSimFrame
{
	public string SessionId { get; set; } = "";

	public int Frame { get; set; }

	public float X { get; set; }

	public float Y { get; set; }

	public float Speed { get; set; }

	public float VelocityY { get; set; }

	public int Facing { get; set; }

	public bool OnGround { get; set; }

	public bool OnLadder { get; set; }

	public string Foothold { get; set; } = "";

	/// <summary>How many ticks the caller asked /session/{id}/frames for.</summary>
	/// <remarks>
	/// <para>
	/// That route clamps a batch to 60 (SimulatorEndpoints.cs), which is the
	/// browser's catch-up window and is deliberate. Clamping SILENTLY is a
	/// different thing: a caller that asks for 600 gets 60 with nothing saying so.
	/// </para>
	/// <para>
	/// MEASURED the cost of that silence. A check that asked for 600 ticks to see
	/// whether mobs patrol read "0 of 50 mobs moved in 10 seconds" and sent the
	/// search after a bug that did not exist -- the mobs had been given 60 ticks,
	/// a tenth of what was asked. Split into batches of 60 they moved normally.
	/// A cap you cannot detect is indistinguishable from a stall.
	/// </para>
	/// </remarks>
	public int TicksRequested { get; set; }

	/// <summary>Ticks actually stepped. Equals the request unless the 60 cap cut it.</summary>
	public int TicksRun { get; set; }

	/// <summary>True when the 60-tick cap cut the request short.</summary>
	public bool Clamped { get; set; }

	public string Action { get; set; } = "";

	public string AttackStance { get; set; } = "";

	/// <summary>
	/// The pose a running skill owes the body, or empty.
	/// </summary>
	/// <remarks>
	/// A NAME, never an index, and never chosen in the browser:
	/// <c>MapleCharacter.cs:1591-1600</c> reads the skill's action off
	/// <c>Equip.DataS[Skill.ID + "/action"]</c> ??a string the server already
	/// resolved. Deriving it client-side would mean a second implementation of the
	/// archive lookup that can disagree with the one the server used.
	/// </remarks>
	public string SkillStance { get; set; } = "";

	/// <summary>The skill being cast, or the one last cast, or empty.</summary>
	public string SkillId { get; set; } = "";

	/// <summary>
	/// How many casts this session has started. W-083.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The client cannot tell one cast of a skill from the next using
	/// <see cref="SkillId"/>, which is why casting the SAME skill twice left the
	/// effect animation frozen on the first cast's clock: <c>livewalk.js</c> reset
	/// <c>skillFxBorn</c> when the id CHANGED, and a second cast of the same id
	/// changed nothing. MEASURED 2026-10-17 as "the first cast animates, the
	/// second does not, the pose still plays".
	/// </para>
	/// <para>
	/// A counter is the cast's identity on the wire. It is monotonic within a
	/// session and never resets, so no sequence of casts can collide.
	/// </para>
	/// </remarks>
	public int SkillCastSeq { get; set; }

	/// <summary>
	/// The cast's own sprites, so the client can draw what casting produced.
	/// </summary>
	/// <remarks>
	/// Empty when no cast is running. These are the plan's own
	/// <see cref="SkillCastSprite"/>s, not a re-derivation: SkillCastService already
	/// resolved which archive each one lives in, and a second lookup here would be
	/// a second answer that could disagree with the timeline they came from.
	/// </remarks>
	public List<SkillCastSprite> SkillEffects { get; set; } = new List<SkillCastSprite>();

	/// <summary>
	/// The ground decals this cast laid down, on the CASTER.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Not <see cref="SkillEffects"/> and not merged into it. A tile is a
	/// <em>positioned</em> animation — it hangs at an offset from its anchor and
	/// is not drawn where the character stands — while a cast sprite is drawn at
	/// the character itself. Merging them would mean the client reading a
	/// position off a shape that has nowhere to put one, and the tile would
	/// either sit on the character's feet or nowhere.
	/// </para>
	/// <para>
	/// Populated for the whole animation rather than one frame, for the reason
	/// <see cref="MapSimMobFrame.HitEffects"/> is: the client polls every ~16 ms
	/// while the server advances up to 8 frames per poll, so a decal published on
	/// the single frame it was spawned would be consumed inside one poll and
	/// never drawn. See <c>MapSimSession.TileEffectUntil</c>.
	/// </para>
	/// </remarks>
	public List<MapSimTileEffect> TileEffects { get; set; } = new List<MapSimTileEffect>();

	public bool BlockedByWall { get; set; }

	public bool FootholdChanged { get; set; }

	public string? FellOut { get; set; }

	public PlayerStatus? Status { get; set; }

	public long ExpGained { get; set; }

	public int LevelsGained { get; set; }

	public int PlayerDamage { get; set; }

	public bool PlayerDead { get; set; }

	public PortalDto? Portal { get; set; }

	public MapTransferDto? Transfer { get; set; }

	public List<MapSimMobFrame> Mobs { get; set; } = new List<MapSimMobFrame>();

	public List<MapSimDamageNumber> Damage { get; set; } = new List<MapSimDamageNumber>();

	public MapSimBounds? Bounds { get; set; }

	public string? Error { get; set; }
}

internal sealed class MapSimSession
{
	public readonly object InputLock = new object();

	public const int SwingPoseTicks = 48;

	private readonly Dictionary<string, int[]> _mobDelays = new Dictionary<string, int[]>(StringComparer.Ordinal);

	public Random CombatRandom { get; init; } = new Random(0);

	public string? Weapon { get; init; }

    /// <summary>
    /// The character cannot be hurt. For watching a mob's attack animation.
    /// </summary>
    /// <remarks>
    /// Set from the session request so it needs no code change to toggle. See
    /// <see cref="PlayerStatus.Invulnerable"/> for why it exists and for what it
    /// deliberately does NOT suppress.
    /// </remarks>
    public bool Invulnerable { get; set; }

	/// <summary>
	/// The WZ weapon IMAGE the character has equipped, e.g. <c>01302000</c>.
	/// Distinct from <see cref="Weapon"/>, which names an <c>Afterimage</c> image
	/// for the hit box and falls back to <c>barehands.img</c> when unset.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Two namespaces, and conflating them gives the wrong answer. <see cref="Weapon"/>
	/// answers "which hit box"; this one answers "which SWING STANCES exist", which the
	/// reference decides from the weapon image's own <c>info/attack</c> integer
	/// (WeaponData.cpp:35) indexing a static table (CharLook.cpp:648, table :682-696).
	/// </para>
	/// <para>
	/// Settable rather than init-only because a player changes weapon mid-play and the
	/// stances change with it; see <see cref="SetEquippedWeapon"/>.
	/// </para>
	/// <para>
	/// Null is NOT attack 0. It is CharLook.cpp:594's early return, where the stance is
	/// left unchanged: an unarmed character has no swing pose at all. That is reference
	/// behaviour, not a gap, so nothing defaults it.
	/// </para>
	/// </remarks>
	public string? EquippedWeapon { get; set; }

	/// <summary>
	/// The whole equipped set, slot -> item image.
	/// </summary>
	/// <remarks>
	/// The sheet is built from this, not from <see cref="EquippedWeapon"/> alone. The
	/// reference's sheet is the sum over the inventory (Player.cpp:309-313), and 326
	/// of the 840 items sampled carry <c>incPDD</c>, 134 carry <c>incDEX</c> and 123
	/// carry <c>incSTR</c> — numbers that move accuracy, max HP and max damage, not
	/// just what the character looks like.
	/// </remarks>
	public Dictionary<string, string> EquippedItems { get; } =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The skill the character casts for each bound key, exactly the reference's
	/// <c>Skill.HotKeyList</c>: a key character to a skill id.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Built by <c>SkillForm.cs:168-174</c>, which CLEARS it and rebuilds the whole
	/// dictionary from the grid on every change rather than editing one entry. The
	/// two mutual exclusions are what that rebuild encodes
	/// (<c>SkillForm.cs:156-162</c>): a row whose skill id already appears is
	/// removed, and a row whose key already appears is removed, so one skill maps to
	/// at most one key and one key to at most one skill. Rebuilding rather than
	/// patching is also why the LAST row wins a collision rather than the first.
	/// </para>
	/// <para>
	/// Keyed case-insensitively because the only keys the reference can produce are
	/// the upper-case letters "A".."Z" (<c>SkillForm.Designer.cs:50</c>), so "d" and
	/// "D" naming different skills would be a caller-side accident, not a feature.
	/// </para>
	/// </remarks>
	public Dictionary<string, string> SkillHotKeys { get; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The character pose a running skill owes, or empty when none.
	/// </summary>
	/// <remarks>
	/// <c>MapleCharacter.cs:1591-1600</c>: while a cast is running the character
	/// swaps <c>State</c> to the skill's own action pose. It travels as a NAME and
	/// never as an index, because the reference stores it as a name too
	/// (<c>Skill.cs:47</c> writes <c>Equip.DataS[ID + "/action"]</c> and
	/// <c>MapleCharacter.cs:1593,1595,1600</c> reads it back as a string).
	/// </remarks>
	public string SkillStance { get; set; } = "";

	/// <summary>
	/// The cast's own sprites, so the client can draw what casting produced.
	/// </summary>
	/// <remarks>
	/// Empty when no cast is running. These are the plan's own
	/// <see cref="SkillCastSprite"/>s, not a re-derivation: SkillCastService already
	/// resolved which archive each one lives in, and a second lookup here would be
	/// a second answer that could disagree with the timeline they came from.
	/// </remarks>
	public List<SkillCastSprite> SkillEffects { get; set; } = new List<SkillCastSprite>();

	/// <summary>Ticks of skill pose still owed, the same counter idea as the swing pose.</summary>
	public int SkillPoseTicks { get; set; }

	/// <summary>The pose length in ms, so the source of the lock is reportable.</summary>
	public double SkillPoseMs { get; set; }

	/// <summary>The skill whose cast is running or was last cast, or empty.</summary>
	public string SkillId { get; set; } = "";

	/// <summary>Which cast this frame belongs to. See <see cref="SkillCastSeq"/>.</summary>
	public int SkillCastSeq { get; set; }

	/// <summary>
	/// NPC labels, resolved once per spawn and keyed by npc id.
	/// </summary>
	/// <remarks>
	/// Cached rather than re-read per frame because the reference resolves them at
	/// spawn (<c>Npc.cs:171-186</c>) and a frame that asked the archive for a name
	/// sixty times a second would be a different program from the one being ported.
	/// </remarks>
	internal Dictionary<string, (string Name, string Func, bool HideName)> NpcLabels { get; } =
		new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal);

	/// <summary>
	/// What each NPC says and when it is due to say it.
	/// </summary>
	/// <remarks>
	/// Keyed by mob id, beside <see cref="NpcLabels"/> and for the same reason the
	/// reference gives every <c>Npc</c> its own <c>chat_delay</c> and its own
	/// balloon (Npc.h:94-95, Npc.h:127): two NPCs of the same id on one map
	/// chatter independently rather than in lockstep.
	/// </remarks>
	internal Dictionary<string, NpcChatState> NpcChats { get; } =
		new Dictionary<string, NpcChatState>(StringComparer.Ordinal);

	/// <summary>
	/// One-frame mob moves larger than <see cref="SimAuditService.MaxMobStepPx"/>,
	/// recorded as they happen.
	/// </summary>
	/// <remarks>
	/// The standing form of "the coordinates shifted when it was hit". The logical
	/// position is sampled here, every frame, for every mob, and anything that
	/// moved further than a walk in one step is kept. A mob flinching does NOT
	/// appear here — measured 0.00px across a six-swing run — which is what makes
	/// a real jump worth reporting rather than noise.
	/// </remarks>
	public List<string> LastMobJumps { get; } = new();

	/// <summary>
	/// Frames where a mob that was PLAYING AN ATTACK also moved.
	/// </summary>
	/// <remarks>
	/// A stricter check than <see cref="LastMobJumps"/>, because the threshold is
	/// zero rather than generous: the reference stops a mob dead the instant an
	/// attack begins.
	/// <para>
	/// <c>MobAttack.cpp:93-94</c> — <c>phobj.inputx = phobj.inputy = 0;
	/// phobj.hspeed = phobj.vx = 0;</c> — and <c>:48</c> returns while
	/// <c>active_attack &gt;= 0</c>, so the move decision at <c>:119-126</c> is
	/// unreachable for the length of a swing.
	/// </para>
	/// <para>
	/// MEASURED before the fix, 2026-10-16: <c>9500302</c> played <c>attack1</c> for
	/// 285 frames and moved on 22 of them, 0.6px per frame, because the chase block
	/// excluded a swinging mob by making its condition FALSE, which fell through
	/// into the patrol. After the fix, 0 of 276.
	/// </para>
	/// </remarks>
	public List<string> LastMobMovesWhileAttacking { get; } = new();

	/// <summary>Frames this session has advanced. A divisor for per-frame rates.</summary>
	public long FramesAdvanced { get; set; }

	/// <summary>
	/// Consecutive clock passes that ended in an exception for this session.
	/// </summary>
	/// <remarks>
	/// Reset to zero by the first clean pass, so this counts an episode rather
	/// than a lifetime. It exists because the step is not idempotent: a frame that
	/// throws part-way has already decremented the NPC chat counters and already
	/// moved the body, and retrying it 240 times a second compounds that damage
	/// instead of recovering from it.
	/// </remarks>
	public int TickFailureStreak { get; set; }

	/// <summary>Why the last failing pass failed, or null when it did not fail.</summary>
	public string? TickFailure { get; set; }

	/// <summary>
	/// True once this session has failed its budget and been stood down.
	/// </summary>
	/// <remarks>
	/// It stays stepped-out until the session is closed and opened again, which is
	/// the honest outcome: the frame that throws is not known to be safe to run
	/// again, and the alternative — retrying forever — is both the log flood and
	/// the compounding above. Every other session keeps running either way.
	/// </remarks>
	public bool TickQuarantined { get; set; }

	/// <summary>The previous frame's mob positions, by slot index.</summary>
	private readonly Dictionary<int, (double X, double Y)> _lastMobPositions = new();

	/// <summary>
	/// Sample every mob's position and record anything that moved too far.
	/// </summary>
	/// <remarks>
	/// Called once per <see cref="Advance"/>. The threshold is the audit's and is
	/// deliberately generous: a grounded mob walks at about 2.5px/frame, so 8px
	/// survives a slope and a knockback and still catches a teleport-sized jump.
	/// </remarks>
	internal void SampleMobPositions()
	{
		FramesAdvanced++;
		for (int i = 0; i < Mobs.Count; i++)
		{
			(MobPatrol patrol, string mobId, _, _) = Mobs[i];
			if (!_lastMobPositions.TryGetValue(i, out (double X, double Y) previous))
			{
				_lastMobPositions[i] = (patrol.X, patrol.Y);
				continue;
			}

			double dx = Math.Abs(patrol.X - previous.X);
			double dy = Math.Abs(patrol.Y - previous.Y);

			// Zero tolerance while an attack is playing, EXCEPT for the sanctioned
			// recoil. Mob.cpp:595-598 gives a struck mob exactly KBSPEED (130 px/s)
			// on the frame it enters the HIT pose, so 2.17px of movement there is
			// the reference's own motion and not a walk. MEASURED: with the
			// knockback in place this finding fired on 9 frames, every one of them
			// exactly 2.17px — the check was flagging the recoil it had just been
			// taught to expect. The previous frame's impulse is subtracted rather
			// than ignored, so a REAL walk of 2.17px during an attack is still
			// caught.
			double knock = Math.Abs(patrol.LastKnockbackPx);
			if (patrol.AttackStance.Length > 0 && (dx - knock > 0.01 || dy > 0.01)
				&& LastMobMovesWhileAttacking.Count < 64)
			{
				LastMobMovesWhileAttacking.Add(
					$"{mobId}@{patrol.X:F1},{patrol.Y:F1} moved {dx:F2},{dy:F2} "
					+ $"while playing {patrol.AttackStance} (recoil {knock:F2} excluded)");
			}

			if (Math.Max(dx, dy) > SimAuditService.MaxMobStepPx && LastMobJumps.Count < 64)
			{
				LastMobJumps.Add(
					$"{mobId}@{patrol.X:F0},{patrol.Y:F0} moved {dx:F2},{dy:F2} in one frame");
			}
			_lastMobPositions[i] = (patrol.X, patrol.Y);
		}
	}

	/// <summary>One NPC's speech state.</summary>
	internal sealed class NpcChatState
	{
		/// <summary>The lines to draw from, in archive order.</summary>
		public List<string> Lines { get; set; } = new();

		/// <summary>
		/// How many animation states share the random pool (Npc.cpp:478). An NPC
		/// with four animations speaks half as often as one with none — that is
		/// the reference's behaviour, not a tuning knob.
		/// </summary>
		public int States { get; set; }

		/// <summary>Frames until the next line, <c>chat_delay</c>.</summary>
		public int DelayFrames { get; set; }

		/// <summary>Frames left on the balloon, 0 when nothing is said.</summary>
		public int ShowingFrames { get; set; }

		/// <summary>What is on the balloon right now.</summary>
		public string Showing { get; set; } = "";
	}

	/// <summary>
	/// Each mob's level, resolved once at spawn.
	/// </summary>
	/// <remarks>
	/// The same reason as <see cref="NpcLabels"/>: <c>CombatService.MobLevel</c>
	/// walks the open Mob archives per call, and a frame that did that per mob per
	/// tick would be measuring the archive, not the fight.
	/// </remarks>
	internal Dictionary<string, int> MobLevels { get; } =
		new Dictionary<string, int>(StringComparer.Ordinal);

	/// <summary>
	/// Each mob's name, from the string pool.
	/// </summary>
	/// <remarks>
	/// NOT on the mob image. MEASURED: <c>f5/2220000.img/info</c> carries 27 children
	/// and no <c>name</c> among them; the name is in String.nx at
	/// <c>f3/Mob.img/2220000/name</c>.
	/// </remarks>
	internal Dictionary<string, string> MobNames { get; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>
	/// Each mob's attacks, read once at spawn.
	/// </summary>
	/// <remarks>
	/// The reference builds its <c>attacks</c> map once, in the mob's constructor
	/// (<c>Mob.cpp:142-177</c>), and reads it from there for the rest of the
	/// mob's life. Asking the archive per frame would be a different program from
	/// the one being ported.
	/// </remarks>
	internal Dictionary<string, IReadOnlyList<CombatService.MobAttackSpec>> MobAttacks { get; } =
		new Dictionary<string, IReadOnlyList<CombatService.MobAttackSpec>>(StringComparer.Ordinal);

	public string Archive { get; init; } = "f1";

	public FootholdPhysics.Portal? CurrentPortal { get; set; }

	/// <summary>
	/// The character sheet. Settable because equipping a weapon rebuilds it —
	/// Player::recalc_stats (Player.cpp:299-360) re-derives the whole sheet on an
	/// equip change, and a sheet that could only be set once could not follow.
	/// </summary>
	public PlayerStatus Status { get; set; } = new PlayerStatus();

	public int PendingPlayerDamage { get; set; }

	/// <summary>
	/// Ticks left before a scheduled swing's hit resolves. 0 and
	/// <see cref="SwingHitPending"/> false means nothing is waiting.
	/// </summary>
	/// <remarks>
	/// The hit used to be applied inside the <c>Swing</c> call that the attack key
	/// entered, so it landed on the same frame with no delay at all. The delay is
	/// now <c>get_attackdelay(0)</c> (Char.cpp:615-622) on the weapon's clock --
	/// see <see cref="CombatService.SwingHitDelayTicks"/>.
	/// </remarks>
	public int PendingSwingHitTicks { get; set; }

	/// <summary>True while a swing's hit is waiting on <see cref="PendingSwingHitTicks"/>.</summary>
	public bool SwingHitPending { get; set; }

	/// <summary>The attack box captured when the key went down, tested when the hit resolves.</summary>
	public Box? PendingSwingBox { get; set; }

	/// <summary>
	/// The pose length the last swing was given, in milliseconds.
	/// </summary>
	/// <remarks>
	/// Reported rather than inferred: "the swing looks the same length for every
	/// weapon" and "the pose is right but the hit is early" are different defects,
	/// and the frame cannot tell them apart.
	/// </remarks>
	public double LastSwingPoseMs { get; set; }

	/// <summary>The attack box the last swing's hit was tested against.</summary>
	public Box? LastSwingBox { get; set; }

	/// <summary>Why the last swing's hit missed, or "" when it did not.</summary>
	public string LastSwingMiss { get; set; } = "";

	public HashSet<MobPatrol> ExpPaidFor { get; } = new HashSet<MobPatrol>();

	public long PendingExp { get; set; }

	public int PendingLevels { get; set; }

	public List<string> MobSwingLog { get; } = new List<string>();

	/// <summary>
	/// The character's position on the previous frame, for the SWEPT touch box.
	/// </summary>
	/// <remarks>
	/// <c>MapMobs.cpp:245-253</c> ??<c>find_colliding</c> builds the rectangle from
	/// the character's <c>get_last_x()/get_last_y()</c> through its current
	/// position, not from the current position alone:
	///
	/// <code>
	/// Range&lt;int16_t&gt; horizontal = Range(moveobj.get_last_x(), moveobj.get_x());
	/// Rectangle&lt;int16_t&gt; player_rect = { horizontal.smaller() + bounds.left(),
	///                                       horizontal.greater() + bounds.right(),
	///                                       vertical.smaller()   + bounds.top(),
	///                                       vertical.greater()   + bounds.bottom() };
	/// </code>
	///
	/// At 60Hz a walking character moves ~2.5px per frame, so a box built from
	/// the current position alone can miss a mob the character is standing on top
	/// of. This port needs the previous position for exactly that reason and had
	/// nowhere to keep it.
	/// </remarks>
	public float PrevX { get; set; }

	/// <summary>See <see cref="PrevX"/>.</summary>
	public float PrevY { get; set; }

	public MapTransferDto? PendingTransfer { get; set; }

	public List<MapSimDamageNumber> PendingDamage { get; } = new List<MapSimDamageNumber>();

    /// <summary>
    /// Hit effects waiting to be published, keyed by mob index so two casts on one
    /// frame each land on their own mob.
    /// </summary>
    /// <remarks>
    /// Keyed by INDEX, not by mob id, for the reason <c>session.Mobs</c> is: a map's
    /// own <c>life</c> entries can name the same id -- twelve mobs sharing one id is
    /// ordinary -- so an id-keyed dictionary would put every struck mob's effect on
    /// the last of them.
    /// </remarks>
    public Dictionary<int, List<MapSimMobHitEffect>> PendingHitEffects { get; } = new();

    /// <summary>
    /// The frame each mob's hit effect is retired on. W-084.
    /// </summary>
    /// <remarks>
    /// The effect must be published on EVERY frame it is alive, not only the frame
    /// the hit landed on: this client polls the server every ~16 ms while the
    /// server advances several frames per poll, so a one-frame publication is
    /// never seen. The length is the sprite's own delays, in 1/60 s frames.
    /// </remarks>
    public Dictionary<int, int> HitEffectUntil { get; } = new();

    /// <summary>
    /// The caster-side ground decals waiting to be published.
    /// </summary>
    /// <remarks>
    /// One list rather than one per mob: <c>TileFootHoldUseEffect::apply(Char&amp;)</c>
    /// lays the whole grid on the CASTER (<c>SkillUseEffect.cpp:245-258</c>), so
    /// every decal of a cast belongs to the same character and the same list. The
    /// mob-side decals are a different call entirely and hang off
    /// <see cref="PendingHitEffects"/> with the mob they landed on.
    /// </remarks>
    public List<MapSimTileEffect> PendingTileEffects { get; } = new();

    /// <summary>
    /// The frame the caster-side ground decals are retired on. The caster-side
    /// counterpart of <see cref="HitEffectUntil"/>, with the same reason and the
    /// same measured floor.
    /// </summary>
    /// <remarks>
    /// MEASURED 2026-10-17 on the hit-effect path: at the bare animation length
    /// the client NEVER drew the effect — it polls every ~16 ms and the server
    /// advances up to 8 frames per poll, so an 11-frame animation was consumed
    /// inside one poll. Hence <c>Math.Max(30, …)</c>. The tile path measures the
    /// same way and uses the same floor rather than a second, different one.
    /// </remarks>
    public int TileEffectUntil { get; set; }

	public required string Id { get; init; }

	public required string Path { get; set; }

	public required FootholdPhysics Map { get; set; }

	public required CharacterPhysics Body { get; set; }

	public int Frame { get; set; }

	public MapSimInput Held { get; } = new MapSimInput();

	public long ManualUntilMs { get; set; }

	/// <summary>
	/// W-089. True once a caller has claimed this session to drive frame by
	/// frame; the background clock stops advancing it.
	/// </summary>
	/// <remarks>
	/// <see cref="ManualUntilMs"/> is a TIME lease and therefore a guess about how
	/// quickly the caller returns; this is the caller saying so outright. The
	/// idle reaper takes the session away, so the flag cannot outlive its owner.
	/// </remarks>
	public bool ManualMode { get; set; }

	public long LastWakeMs { get; set; }

	public double AccumulatorMs { get; set; }

	public string LastFoothold { get; set; } = "";

	/// <summary>
	/// Whether this character's swing pose has already been let go of since it died.
	/// </summary>
	/// <remarks>
	/// A latch, not a flag to read: it is set the one frame the character dies and
	/// cleared by <c>Revive</c>, so the clear happens once rather than every frame
	/// the corpse is on screen.
	/// </remarks>
	public bool BodyDeadPoseCleared { get; set; }

	/// <summary>
	/// What the CLIENT reported about its own art loading.
	/// </summary>
	/// <remarks>
	/// The server side of "the map did not load" measures healthy: 60 of 60 sampled
	/// maps answered <c>/simulate/geometry</c> with footholds and no error, and 927
	/// art slots across 40 maps had 8 failures (0.9%, all ladder ropes). What was
	/// left unmeasured was the BROWSER's decode path — <c>loadArt</c> /
	/// <c>loadVisible</c> set <c>slot.missing</c> on an <c>onerror</c> and nothing
	/// could read it back.
	///
	/// <para>
	/// This is where that count lands. It is a report FROM the client and is kept
	/// separate from <c>doc.art[].missing</c> (which means "the node would not
	/// resolve") so the two cannot be confused.
	/// </para>
	/// </remarks>
	public ArtStats ClientArt { get; } = new ArtStats();

	/// <summary>What the browser managed and failed to decode.</summary>
	public sealed class ArtStats
	{
		/// <summary>How many image slots the client attempted.</summary>
		public int Attempted { get; set; }

		/// <summary>How many came back as something the browser could not draw.</summary>
		public int Missing { get; set; }

		/// <summary>Up to twenty failing slots, so a failure is nameable.</summary>
		public List<string> MissingSample { get; } = new List<string>();

		/// <summary>When the client last reported, for staleness.</summary>
		public long ReportedAtMs { get; set; }
	}

	public float LastX { get; set; }

	public int Facing { get; set; }

	public List<(MobPatrol Patrol, string Id, string Kind, string Layer)> Mobs { get; } = new List<(MobPatrol, string, string, string)>();

	public List<MobAnimation> Animations { get; } = new List<MobAnimation>();

	/// <summary>
	/// The indices in <see cref="Mobs"/> that a summon placed, so they can be
	/// removed again without touching the map's own spawns.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The benchmark tracks summons by id: <c>Mob.SummonedList</c> is a
	/// <c>List&lt;string&gt;</c> of mob ids (<c>Mob.cs:75</c>) and its Remove button
	/// kills every mob whose <c>LocalID</c> matches (<c>MobForm.cs:128-140</c>). That
	/// cannot be copied here, because a map's own <c>life</c> entries can name the
	/// same id ??twelve mobs sharing one id is ordinary ??so removing "every mob
	/// with this id" would take the map's spawns with the summons.
	/// </para>
	/// <para>
	/// Indices are the stable identity: <c>Mobs</c> only ever grows (a dead mob keeps
	/// its slot and reports <c>Alive == false</c>, see the swing loop's
	/// <c>if (!mobPatrol.Alive) continue;</c>), so an index recorded at spawn time
	/// still names the same animal at despawn time.
	/// </para>
	/// </remarks>
	public HashSet<int> SummonedIndexes { get; } = new HashSet<int>();

	public Dictionary<int, string> MobSpritePaths { get; } = new Dictionary<int, string>();

	public long LastInputMs { get; set; }

	public string SpritePathFor(int index, string mobId, string kind)
	{
		if (MobSpritePaths.TryGetValue(index, out string value))
		{
			return value;
		}
		MobSpriteService sprites = MobAnimation.Sprites;
		string text = ((sprites != null) ? sprites.Describe(null, mobId, kind).ImagePath : "");
		MobSpritePaths[index] = text;
		return text;
	}

	public int[] MobFrameDelays(string mobId, string kind, string action)
	{
		int num = ((action == "hit1") ? 600 : ((action == "die1") ? 750 : 0));
		int fallbackMs = num;
		string key = $"{mobId}|{kind}|{action}";
		if (_mobDelays.TryGetValue(key, out int[] value))
		{
			return value;
		}
		int[] array = MobAnimation.DelaysOrFallback(mobId, kind, action, fallbackMs);
		_mobDelays[key] = array;
		return array;
	}

	/// <summary>
	/// Whether this mob can fly — <c>Mob.cpp:82</c>,
	/// <c>canfly = anisrc["fly"].size() &gt; 0</c>.
	/// </summary>
	/// <remarks>
	/// Read once per mob and remembered, for the same reason the attack list and
	/// the labels are: a frame that asked the archive sixty times a second is a
	/// different program from the one being ported.
	/// <para>
	/// It decides the STAND animation, which is why it lives here rather than in
	/// the patrol: <c>Mob.cpp:115-124</c> says a flying mob's STAND and MOVE are
	/// BOTH its <c>fly</c> animation, not its <c>stand</c>.
	/// </para>
	/// </remarks>
	public bool MobCanFly(string mobId, string kind)
	{
		string key = $"{mobId}|{kind}";
		if (_mobFly.Contains(key))
		{
			return true;
		}
		MobSpriteService? sprites = MobAnimation.Sprites;
		if (sprites is null)
		{
			return false;
		}
		bool fly = sprites.Describe(null, mobId, kind).Actions
			.Any(a => a.Name == "fly" && a.Frames.Count > 0);
		if (fly)
		{
			_mobFly.Add(key);
		}
		return fly;
	}

	private readonly HashSet<string> _mobFly = new(StringComparer.Ordinal);
}

public sealed class MapSimSessionStore
{
	public const double FixedStepMs = 16.66666753590107;

	private const long IdleTimeoutMs = 120000L;

	private const long ManualHoldMs = 250L;

	public static readonly TimeSpan LoopInterval = TimeSpan.FromMilliseconds(4.166666883975267);

	private readonly ConcurrentDictionary<string, MapSimSession> _sessions = new ConcurrentDictionary<string, MapSimSession>(StringComparer.Ordinal);

	/// <summary>
	/// Where a per-session step failure is reported.
	/// </summary>
	/// <remarks>
	/// Optional in the constructor so nothing that builds this by hand has to
	/// change with it; absent, the failures are recorded on the session
	/// (<see cref="MapSimSession.TickFailure"/>) and simply not logged.
	/// </remarks>
	private readonly ILogger<MapSimSessionStore> _log;

	private readonly MapEditorService _maps;

	private readonly CombatService _combat;

	/// <summary>
	/// Equipment totals, so an equipped weapon can move the sheet. Null only when
	/// the store is constructed without DI (tests); a null leaves the sheet at the
	/// reference's level-1 base, which is what an unequipped character is anyway.
	/// </summary>
	private readonly EquipStatsService? _equips;

	/// <summary>
	/// What NPCs say, read once at spawn. Null only when the store is constructed
	/// without DI; every NPC is then silent, which is the truth when no Npc and
	/// String archive is open rather than an invented line.
	/// </summary>
	private readonly NpcChatService? _chats;

	private readonly SkillCastService _casts;

	/// <summary>
	/// Where a mob's NAME comes from -- String.nx, not the mob image.
	/// </summary>
	/// <remarks>
	/// Optional: a server built without the string pool still runs, and every mob
	/// label then falls back to its id alone. MEASURED: <c>info/name</c> does not
	/// exist on the mob image at all, so there is no second place to try.
	/// </remarks>
	private readonly StringPoolService? _strings;

	private const int SimulatedPlayerLevel = 1;

	private const int OffsetYReferenceHeight = 600;

	public int LiveCount => _sessions.Count;

	/// <summary>
	/// Each mob's CURRENT animation box, for the assertion surface.
	/// </summary>
	/// <remarks>
	/// A mob's box is a function of its pose AND its frame — measured on 2220000,
	/// a <c>move</c> frame reaches 82px while the same mob standing 19px away does
	/// not overlap the player at all. So a box captured when a swing starts is
	/// stale by the time its hit resolves, and "why did it miss" cannot be answered
	/// from a number taken once.
	/// </remarks>
	internal List<object> MobFrames(MapSimSession live)
	{
		List<object> rows = new();
		for (int i = 0; i < live.Mobs.Count && i < 40; i++)
		{
			var (patrol, mobId, kind, layer) = live.Mobs[i];
			if (kind == "n") continue;
			MobAnimation anim = MobAnimation.For(live, i, kind, patrol);
			MobFrameGeometry? geom = _combat.MobFrame(mobId, anim.Stance, anim.FrameIndex, kind);
			Box box = geom is null ? Box.Empty : geom.At(patrol.X, patrol.Y);
			rows.Add(new
			{
				index = i,
				mobId,
				x = patrol.X,
				y = patrol.Y,
				layer,
				stance = anim.Stance,
				frame = anim.FrameIndex,
				alive = patrol.Alive,
				aggro = patrol.Aggro,
				box = new { box.Left, box.Top, box.Right, box.Bottom },
			});
		}
		return rows;
	}

	/// <summary>
	/// The nearest ladder to a character, with the distances that decide it.
	/// </summary>
	/// <remarks>
	/// The climb box is <c>L.X ± 10</c> and the rope's whole height ± 12
	/// (<c>LadderRopes.cs:19</c>). Measured on f4/Map/Map1/100000004.img, every
	/// ladder hangs in the air (y −533..−271, −1343..−1082) while the ground is
	/// y −25, so standing on the ground puts the character hundreds of pixels
	/// below every rope and <c>FindLadder</c> correctly refuses. That is why "it
	/// climbs things that are not ropes" is unanswerable without knowing where the
	/// user was standing.
	/// </remarks>
	internal object NearestLadder(MapSimSession live)
	{
		// Ladder is a readonly record STRUCT, so `Ladder?` is Nullable<Ladder> and
		// `is null` does not narrow it -- hence a plain value plus a flag.
		FootholdPhysics.Ladder best = default;
		bool found = false;
		int bestDx = int.MaxValue;
		int px = (int)Math.Round(live.Body.X);
		foreach (FootholdPhysics.Ladder l in live.Map.Ladders)
		{
			int dx = Math.Abs(l.X - px);
			if (dx >= bestDx) continue;
			bestDx = dx;
			best = l;
			found = true;
		}
		if (!found)
			return new { found = false, x = 0, top = 0, bottom = 0, dx = -1, dyFeet = 0, inReach = false };
		int feet = (int)Math.Round(live.Body.Y);
		return new
		{
			found = true,
			x = best.X,
			top = best.Top,
			bottom = best.Bottom,
			dx = bestDx,
			dyFeet = feet - best.Bottom,
			inReach = bestDx <= 10 && feet >= best.Top - 12 && feet <= best.Bottom + 12,
		};
	}

	public object Gate => _maps.EditorGate;

	public string? LastPortalFailure { get; set; }

	public MapSimSessionStore(MapEditorService maps, CombatService combat, MobSpriteService sprites,
		SkillCastService casts, StringPoolService? strings = null, EquipStatsService? equips = null,
		NpcChatService? chats = null, ILogger<MapSimSessionStore>? log = null)
	{
		_maps = maps;
		_combat = combat;
		_casts = casts;
		_strings = strings;
		_equips = equips;
		_chats = chats;
		_log = log ?? NullLogger<MapSimSessionStore>.Instance;
		MobAnimation.Sprites = sprites;
	}

	/// <summary>
	/// How many consecutive failed passes a session gets before it is stood down.
	/// </summary>
	/// <remarks>
	/// Small on purpose. A step is not idempotent — the NPC chat counters have
	/// already been decremented and the body already moved by the time it can
	/// throw — so each retry after the first makes that session less correct, not
	/// more. Five is enough to ride out one bad pass and short enough that the
	/// damage from a persistently broken session stays bounded to five frames.
	/// </remarks>
	internal const int MaxConsecutiveTickFailures = 5;

	public void Tick(CancellationToken cancel)
	{
		long tickCount = Environment.TickCount64;
		Sweep(tickCount);
		foreach (KeyValuePair<string, MapSimSession> entry in _sessions)
		{
			if (cancel.IsCancellationRequested)
			{
				break;
			}

			MapSimSession value = entry.Value;

			// Already stood down. Skipping here rather than catching twice is what
			// keeps a broken session from filling the log for the life of the process.
			if (value.TickQuarantined)
			{
				continue;
			}

			try
			{
				TickSession(value, tickCount);
				value.TickFailureStreak = 0;
				value.TickFailure = null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// One session, one failure. The loop used to call TickSession bare, so
				// the first session to throw ended the whole pass, and the pass ran
				// under a try that wrapped the loop -- so it ended the clock too.
				//
				// Bounded logging is the reason this is not a bare catch-and-log: the
				// step runs on a 16.67 ms accumulator, so a session that always throws
				// reaches this branch about sixty times a second. An unconditional
				// log line there is a worse defect than the one being fixed.
				value.TickFailureStreak++;
				value.TickFailure = ex.Message;

				if (value.TickFailureStreak < MaxConsecutiveTickFailures)
				{
					_log.LogWarning(ex,
						"simulation session {Id} failed to step ({Streak}/{Budget}); it will be stood down if it keeps failing",
						entry.Key, value.TickFailureStreak, MaxConsecutiveTickFailures);
					continue;
				}

				value.TickQuarantined = true;
				_log.LogError(ex,
					"simulation session {Id} failed {Streak} passes in a row and has been stood down; every other session keeps running",
					entry.Key, value.TickFailureStreak);
			}
		}
	}

	private void TickSession(MapSimSession session, long now)
	{
		// W-089. A session that was claimed by a caller driving it frame by frame
		// is not the server's to advance. The lease below is a guess about how
		// quickly the caller comes back; this flag is the caller saying it will
		// come back when it comes back.
		if (session.ManualMode)
		{
			session.AccumulatorMs = 0.0;
			session.LastWakeMs = now;
			return;
		}
		if (now < session.ManualUntilMs)
		{
			session.AccumulatorMs = 0.0;
			session.LastWakeMs = now;
			return;
		}
		double num = now - session.LastWakeMs;
		session.LastWakeMs = now;
		if (!(num <= 0.0))
		{
			if (num > 16.66666753590107)
			{
				num = 16.66666753590107;
			}
			session.AccumulatorMs += num;
			while (session.AccumulatorMs >= 16.66666753590107)
			{
				session.AccumulatorMs -= 16.66666753590107;
				Advance(session, HeldInput(session));
			}
		}
	}

	private static MapSimInput HeldInput(MapSimSession session)
	{
		lock (session.InputLock)
		{
			return new MapSimInput
			{
				Left = session.Held.Left,
				Right = session.Held.Right,
				Up = session.Held.Up,
				Down = session.Held.Down
			};
		}
	}

	/// <summary>
	/// One frame of NPC chatter.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>Npc.cpp:276-284</c>:
	/// </para>
	/// <code>
	/// if (chat_delay > 0)          chat_delay -= TIMESTEP;
	/// else if (!balloon.is_active() &amp;&amp; quest_icon_type &lt; 0)
	/// {   chat_delay = CHAT_MIN_DELAY + rand(CHAT_DELAY_SPREAD);
	///     speak(); }
	/// </code>
	/// <para>
	/// and <c>speak()</c> (Npc.cpp:457-484) draws one index out of
	/// <c>states + lines</c> TOGETHER and only speaks when the index lands on a
	/// line — so an NPC with things to do says less, and that is the behaviour
	/// rather than a tuning knob.
	/// </para>
	/// <para>
	/// The quest-marker condition has no counterpart here: this simulator has no
	/// quest log, so <c>quest_icon_type</c> is always -1 and the test is true.
	/// That is stated rather than quietly dropped.
	/// </para>
	/// <para>
	/// FRAME-COUNTED throughout. The reference's numbers are milliseconds
	/// (3000 / 6000 / 3000) against a 16.67ms step; a wall-clock port would make a
	/// replay differ by the client's frame rate, which this project forbids.
	/// </para>
	/// </remarks>
	private void AdvanceNpcChat(MapSimSession session)
	{
		if (session.NpcChats.Count == 0) return;

		foreach (MapSimSession.NpcChatState chat in session.NpcChats.Values)
		{
			if (chat.ShowingFrames > 0)
			{
				chat.ShowingFrames--;
				if (chat.ShowingFrames == 0) chat.Showing = "";
			}

			if (chat.DelayFrames > 0)
			{
				chat.DelayFrames--;
				continue;
			}
			// A line only goes out when nothing is already on screen.
			if (chat.ShowingFrames > 0) continue;

			chat.DelayFrames = NpcChatService.ChatMinDelayMs / MsPerFrame +
				session.CombatRandom.Next(NpcChatService.ChatDelaySpreadMs / MsPerFrame + 1);

			if (chat.Lines.Count == 0) continue;

			int pool = chat.States + chat.Lines.Count;
			int pick = session.CombatRandom.Next(pool);
			if (pick < chat.States) continue;    // it did something instead

			chat.Showing = chat.Lines[pick - chat.States];
			chat.ShowingFrames = NpcChatService.ChatDurationMs / MsPerFrame;
		}
	}

	/// <summary>The fixed step, 1000/60 ms. Every duration here divides by it.</summary>
	private const int MsPerFrame = 1000 / 60;

	private void Advance(MapSimSession session, MapSimInput input)
	{
		try
		{
			AdvanceOnce(session, input);
		}
		finally
		{
			// The frame counter is a clock, not a tally of completed frames, and
			// everything that expires an effect reads it: HitEffectUntil at
			// Describe(), PendingTileEffects at TileEffectUntil. While this was the
			// last statement of the body instead, a frame that threw anywhere in
			// ~190 lines left Frame frozen -- so those two containers never met
			// their retire condition and grew for the life of the session, and every
			// flashed mob kept its flash drawn forever.
			//
			// A frozen counter is a worse outcome than a failed frame: it looks
			// healthy. The counter moves, the half-applied frame does not, and the
			// caller sees both because the exception still propagates.
			session.Frame++;
		}
	}

	private void AdvanceOnce(MapSimSession session, MapSimInput input)
	{
		AdvanceNpcChat(session);
		session.SampleMobPositions();
		CharacterPhysics body = session.Body;
		int moveLeft = (input.Left ? (-1) : (input.Right ? 1 : 0));

		// W-082b. FACING FOLLOWS THE KEY, NOT THE MOVEMENT.
		//
		//   PlayerStates.cpp:52-63   if (is_key_down(LEFT))  player.set_direction(false);
		//                            else if (is_key_down(RIGHT)) player.set_direction(true);
		//   Player.cpp:1214-1218    void Player::set_direction(bool f)
		//                            { if (!attacking) Char::set_direction(f); }
		//
		// Two things follow from those, and this port had neither.
		//
		// ONE: the direction is read off the KEY. This derived it from the body's
		// actual movement, in Describe(), which runs AFTER the frame's combat. So a
		// character that was blocked, or one standing still under a cast (moveLeft
		// is zeroed while SkillPoseTicks > 0), never turned -- and the skill's
		// collision box is built from this same field, so the cast went out on the
		// side the character happened to be facing from last time it walked. MEASURED
		// 2026-10-17: pressing Left and casting on the same frame still aimed right.
		//
		// TWO: it does not change while attacking, or casting. That is the whole
		// body of Player::set_direction, and it is why a cast cannot be re-aimed by
		// leaning on the arrow key through the animation.
		if (session.SkillPoseTicks <= 0 && body.AttackPoseTicks <= 0)
		{
			if (input.Left) session.Facing = -1;
			else if (input.Right) session.Facing = 1;
		}

		// W-082c. A CORPSE DOES NOT WALK.
		//
		// The swing and the cast already refuse on `Status.Dead` (:1431 and
		// CastSkill :1492) -- that guard was added by the S1-S5 sweep. Walking was
		// never guarded, and MEASURED on 2026-10-17 a character reported
		// `playerDead: true, status.hp: 0` while still travelling: 1710 -> 1614
		// over 40 frames, `action: "walk"`. A dead body walking is the same class
		// of defect as a corpse swinging: the frame says one thing and the
		// simulation does another.
		bool jump = input.Jump;
		if (session.Status.Dead)
		{
			moveLeft = 0;
			jump = false;
		}

		if (jump)
		{
			body.Jump();
		}

		// W-076. A CAST STOPS THE BODY, for the whole cast.
		//
		//   Char.cpp:911       Char::attack(action) sets `attacking = true`
		//   PlayerStates.cpp:96,112,150,174,219,364
		//                       every state handler opens with
		//                       `if (is_attacking()) return;`
		//   Player.cpp:869-871 the lock ends when the BODY animation ends
		//
		// We already had the lock for re-casting (`SkillPoseTicks > 0`,
		// MapSimSessionStore.cs:1475) but only as a CAST gate -- the physics body
		// still walked, so a character walked through its own cast. That is a
		// mechanism defect (ADR-0005 criterion 2), not a fidelity nicety: a test
		// tool that lets the body move while the pose says otherwise shows the
		// wrong thing.
		//
		// Up and down are NOT suppressed: the reference's ladder/rope handlers are
		// the ones that refuse, and `CastSkill` already refuses to START on a
		// ladder (:1476), so a cast in progress cannot become a climb.
		if (session.SkillPoseTicks > 0) moveLeft = 0;
		// -1 is UP and +1 is DOWN. The physics says so in seven places, and the
		// reference says so with arithmetic:
		//
		//   MapleCharacter.cs:541  if (Keyboard.KeyDown(Input.Up))
		//   MapleCharacter.cs:543      OffY = -3;
		//   MapleCharacter.cs:550      Y -= 1.5f;          <-- Up DECREASES y
		//   MapleCharacter.cs:568  if (Keyboard.KeyDown(Input.Down) ...)
		//   MapleCharacter.cs:577      Y += 1.5f;          <-- Down INCREASES y
		//
		// and smaller y is higher on a map (bounds top=-1345, bottom=180).
		// CharacterPhysics applies `_y += climb * ClimbSpeedPerStep`
		// (CharacterPhysics.cs:1029), so Up has to arrive as climb = -1 or the
		// ladder is ridden the wrong way.
		//
		// This line had it backwards (`Up ? 1`), which is also what a stale XML
		// comment above CharacterPhysics.Step claimed -- "+1 up a ladder, -1 down
		// one" -- contradicting the body of the same file seven lines away from it.
		// Measured on f4/Map/Map1/100000004.img, standing on foothold 114 at
		// (1936,-265) with a ladder at x=1936 spanning y -533..-271:
		//
		//   holding UP gave a 5-frame loop -- climb -265->-263.5->-262->-260.5->-259
		//   (descending), then fall back to -265 -- and onLadder was true on 0 of
		//   120 frames.
		//
		// The reference's own convention is stated in MapEditorService.SimulateWalk.cs
		// ("pass climb=-1 to go up it", :98) and MapWalkRequest.Climb ("-1 is up", :953).
		int climb = (input.Up ? (-1) : (input.Down ? 1 : 0));
		bool down = input.Down;
		lock (Gate)
		{
			body.Step(moveLeft, climb, down);
			for (int i = 0; i < session.Mobs.Count; i++)
			{
				var (mobPatrol, kind, _, _) = session.Mobs[i];
				mobPatrol.Step((int)body.X, (int)body.Y);
				MobAnimation.For(session, i, kind, mobPatrol);
			}
			session.CurrentPortal = session.Map.FindPortal((int)body.X, (int)body.Y);
			session.PendingTransfer = null;
			if (input.Up)
			{
				FootholdPhysics.Portal? currentPortal = session.CurrentPortal;
				if (currentPortal.HasValue)
				{
					FootholdPhysics.Portal valueOrDefault = currentPortal.GetValueOrDefault();
					if (valueOrDefault.ToMap != 999999999)
					{
						session.PendingTransfer = TakePortal(session, valueOrDefault);
					}
				}
			}
			int num = 0;
			session.MobSwingLog.Clear();

			// The SWEPT body rect, MapMobs.cpp:245-253. Captured before anything
			// else moves the body, so it spans the frame the character actually
			// travelled rather than only where it ended up.
			float lastX = session.PrevX;
			float lastY = session.PrevY;
			// Char.cpp:449-452 -- `if (!morph_visible) return { -22, 22, -65, 0 };`
			Box touchRect = new(
				(int)MathF.Min(lastX, body.X) - MobPatrol.TouchHalfWidth,
				(int)MathF.Min(lastY, body.Y) + MobPatrol.TouchTop,
				(int)MathF.Max(lastX, body.X) + MobPatrol.TouchHalfWidth,
				(int)MathF.Max(lastY, body.Y) + MobPatrol.TouchBottom);
			session.PrevX = body.X;
			session.PrevY = body.Y;

			if (!session.Status.Dead)
			{
				// THE PURSUIT half -- MobAttack.cpp:50-127. Acquisition, forgetting,
				// and (once the attack list is ported) swinging. It deals NO damage:
				// in the reference this function ends at `set_stance`, and the hit
				// comes from the attack entry's own geometry.
				foreach (var (mobPatrol2, _, text, _) in session.Mobs)
				{
					if (!(text == "n"))
					{
						mobPatrol2.Engage((int)body.X, (int)body.Y);
					}
				}

				// THE ATTACK LIST, resolving. MobAttack.cpp:193-217 -- an attack entry
				// is tested only once its `attackAfter` has elapsed, and
				// `MobAttack.cpp:226` retires it once it hits or its lifetime expires.
				//
				// This is a DIFFERENT stream from the touch block below and the C++
				// keeps them apart: a mob swings at range and, separately, hurts on
				// contact. Merging them would let a mob at 200px hit a character
				// standing on it and one character would take two hits a tick.
				foreach (var (mobPatrol2, _, text, _) in session.Mobs)
				{
					if (text == "n") continue;
					int swingDamage = mobPatrol2.ConsumeAttackDamage();
					if (swingDamage <= 0) continue;
					num += session.Status.Hurt(swingDamage);
				}

				// THE TOUCH half -- Stage.cpp:809-818, which is where the damage
				// actually comes from, and where it always came from:
				//
				//     if (int32_t oid_id = mobs.find_colliding(player.get_phobj(),
				//                                                   player.get_touch_bounds()))
				//         if (MobAttack attack = mobs.create_attack(oid_id); attack && ...)
				//             player.damage(attack);
				//
				// `find_colliding` is a `std::find_if` over the mob list
				// (MapMobs.cpp:255-268): the FIRST alive mob the character overlaps,
				// not the nearest and not every one of them. `is_in_range`
				// (Mob.cpp:1875-1884) tests the mob's CURRENT animation bounds
				// against that rect -- the same per-frame box the player's own swing
				// uses. `create_attack` -> `create_touch_attack` (Mob.cpp:1848-1858)
				// returns nothing at all unless `info/bodyAttack` is set.
				//
				// What this replaces: a per-mob aggro/cooldown/facing reach test.
				// That made an ORDINARY mob -- one the character never struck and
				// that has no firstAttack -- harmless on contact, which is not the
				// game: standing next to a slime hurts in every frame of the
				// original.
				MobPatrol? toucher = null;
				for (int i = 0; i < session.Mobs.Count && toucher is null; i++)
				{
					var (patrol, mobId, kind, _) = session.Mobs[i];
					if (kind == "n" || !patrol.Alive)
					{
						continue;
					}
					MobAnimation anim = MobAnimation.For(session, i, kind, patrol);
					MobFrameGeometry geom = _combat.MobFrame(mobId, anim.Stance, anim.FrameIndex, kind);
					if (geom is null)
					{
						continue;
					}
					Box mobBox = geom.At(patrol.X, patrol.Y);
					if (!mobBox.IsEmpty && mobBox.Overlaps(touchRect))
					{
						toucher = patrol;
					}
				}

				if (toucher is not null)
				{
					session.MobSwingLog.Add($"{toucher.X:F0},{toucher.Y:F0} alive={toucher.Alive} aggro={toucher.Aggro} cd={toucher.AttackCooldownLeft} dx={(int)body.X - (int)toucher.X} TOUCH");
					num = toucher.ContactDamage();
					if (num > 0)
					{
						session.Status.Hurt(num);
					}
				}
			}
			session.PendingPlayerDamage = num;

		// A corpse is not frozen mid-swing. The pose locks outlive the character:
		// `AttackPoseTicks` holds the body for the whole animation, and a character
		// killed 3 frames into its swing kept that lock for the other 34, so the
		// frame reported `attackStance` for a body that could not swing at all --
		// and MEASURED on `f4/Map/Map0/000000000.img`, `revive-verify.py` read
		// `attackStance='stabO1'` off a DEAD character and called it a missing
		// guard. The guard is there; what was left was the pose.
		//
		// Declared own-design: the reference plays a death animation this port does
		// not have (`Char.cpp`/`CharLook.cpp` name it, and nothing here reads it), so
		// "standing still, not swinging" is the closest honest state for a corpse.
		if (session.Status.Dead && !session.BodyDeadPoseCleared)
		{
			session.BodyDeadPoseCleared = true;
			session.PendingSwingHitTicks = 0;
			session.SwingHitPending = false;
			session.PendingSwingBox = null;
			session.SkillPoseTicks = 0;
			body.ReleasePoses();
		}

		// The player's own attack and cast stay OUTSIDE the block above, and that
		// is deliberate: `Advance` keeps running when the character is dead so the
		// body settles and the camera keeps following. What was missing is that
		// nothing here re-checked `Dead`, so a corpse kept swinging, kept killing
		// mobs (`mobPatrol.TakeHit`) and kept being paid for them (`GainExp`) on
		// the same frame it reported `playerDead: true`.
		//
		// Found by the S1-S5 defect sweep, and it is the SAME SHAPE as the
		// `MobPatrol.cs` fall-through fixed earlier today: a condition that reads
		// like a guard and guards something else.
		//
		// The guard is HERE rather than inside the block above so the brace
		// structure of a 300-line method is not restructured to carry it — a
		// misplaced brace in here cost more than the fix was worth.
		if (input.Attack)
		{
			Swing(session, body, input);
		}
		else
		{
			// A swing in flight still resolves on its own schedule; only a NEW
			// swing re-captures the box. This is why the block is not simply
			// inside `if (input.Attack)`.
			ResolveSwingHit(session, body);
		}
		if (input.SkillId is { Length: > 0 } cast)
		{
			CastSkill(session, body, cast);
		}

			// The cast lock runs down every tick, exactly like the swing pose, and
			// while it holds the character reports `skill` so the body wears the
			// skill's action pose instead of walking. MapleCharacter.cs:1472,1482,
			// 1509,1513,1522,1642 gate walking, jumping, prone and the normal attack
			// on `!Skill.PlayEnded` for the same reason: a cast IS the animation.
			if (session.SkillPoseTicks > 0)
			{
				session.SkillPoseTicks--;
			}
			else if (session.SkillStance.Length > 0)
			{
				// The pose is over but the name lingers. Cleared rather than left, so
				// the next idle frame is plainly `stand` and not a stale `skill`.
				session.SkillStance = "";
				// The effect sprites go with the pose. They are the cast's own frames
				// (Skill.cs:241-279 spawns them at t0), so holding them past the end of
				// the pose would leave a finished animation on screen.
				session.SkillEffects.Clear();
			}
		}
	}

	/// <summary>Ticks a cast is locked for before another one may start.</summary>
	/// <remarks>
	/// The reference's only anti-spam gate is <c>Skill.PlayEnded</c>, and that flag
	/// is set from the effect sprite's own animation end (<c>Skill.cs:410</c>
	/// <c>Skill.PlayEnded = AnimEnd;</c>) -- NOT from <c>TotalTime</c>, which
	/// (<c>Skill.cs:187</c>) is how long the collision objects live. Measured over
	/// twelve castable skills those two differ by as much as sixteen times, so
	/// locking on <c>TotalTime</c> held the character still far past the point the
	/// reference releases it. The lock is <see cref="SkillCastPlan.EffectMs"/>
	/// converted at the client's 60Hz.
	/// <para>
	/// The floor of one tick is deliberate: a skill carrying no effect sprite has no
	/// animation to wait for, and a zero-length lock would make the gate
	/// unobservable. <c>Skill.cs:321</c> is a per-tick poll, so a cast whose sprite
	/// ends inside one tick still occupies that tick.
	/// </para>
	/// </remarks>
	private void CastSkill(MapSimSession session, CharacterPhysics body, string skillId)
	{
		// A DEAD CHARACTER DOES NOT CAST. Same reason as Swing(): the cast sits
		// outside the `!session.Status.Dead` block that guards the mob-side combat,
		// and `CastSkill` checks only the pose lock and the ladder.
		if (session.Status.Dead) return;
		// Skill.cs:321 -- two gates, and both are refusals:
		//     if (Skill.PlayEnded && !Game.Player.InLadder)
		if (session.SkillPoseTicks > 0) return;      // the previous cast has not ended
		if (body.OnLadder.HasValue) return;           // InLadder is true while riding

		SkillCastPlan? plan = _casts.Plan(skillId, session.Status.Level,
            // W-076: the sprite clock runs at the character's real attack speed,
            // so the effect plays at the rate the reference plays it. Read from the
            // ONE place that already reads it for a swing (CombatService:1066).
            CombatService.RealAttackSpeed(_combat.WeaponAttackSpeed(session.EquippedWeapon)));
		if (plan is null) return;

		// MapleCharacter.cs:1591-1600 -- while the cast runs, the character wears the
		// skill's own action pose. The reference stores the pose NAME and compares
		// before assigning (:1595), so an absent action leaves whatever the body was
		// already wearing; SkillCastService has already applied the reference's
		// fallback to "alert" (Skill.cs:46-47), which is the same shape of answer.
		if (plan.Actions.Count > 0)
		{
			// The reference picks among them BY WEAPON, not at random and not always
			// the first:
			//
			//   SkillAction.cpp:58-70
			//     TwoHandedAction::TwoHandedAction(src) { actions[false] = src["action"]["0"];
			//                                              actions[true]  = src["action"]["1"]; }
			//     void TwoHandedAction::apply(Char& target, Attack::Type) const {
			//         bool twohanded = target.is_twohanded();
			//         std::string action = actions[twohanded];
			//         target.attack(action); }
			//
			// so `action/0` is the ONE-handed pose and `action/1` the TWO-handed
			// one, and the weapon decides. Reading index 0 unconditionally left the
			// second pose unreachable: MEASURED 2026-10-18, 1121008 cast three
			// times and `brandish2` never appeared on screen.
			//
			// A skill with ONE action has no such choice, and the reference's own
			// resolution for a single `action` node is that one.
			int actionIndex = 0;
			if (plan.Actions.Count > 1 && CombatService.IsTwoHanded(session.EquippedWeapon))
				actionIndex = 1;
			session.SkillStance = plan.Actions[actionIndex];
			// The lock is the POSE's length, not the cast sprite's.
			//
			// This used to be `plan.EffectMs`, which is how long the SPRITE lasts,
			// and it was wrong for 44 of the 55 castable skills that declare a pose:
			// shorter than the pose for 17 (the animation is cut off mid-play) and
			// longer for 27 (the character freezes in a finished pose and refuses the
			// next cast). MapleCharacter.cs:392-397/:1121/:1240-1242 advances the
			// character on the body image's own per-frame delays, so that is the clock.
			//
			// Falls back to EffectMs only when the body image carries no such pose,
			// which is the same shape as the "alert" fallback above: an archive that
			// cannot answer should not silently lock the character for zero frames.
			int poseMs = _casts.PoseMs(session.SkillStance);
			session.SkillPoseMs = poseMs > 0 ? poseMs : plan.EffectMs;
			session.SkillPoseTicks = Math.Max(1,
				(int)Math.Round(session.SkillPoseMs / (1000.0 / 60.0)));
		}

		// The cast's own sprites (Skill.cs:241-279), attached to the SAME window as
		// the pose. Every one of the eleven node names the reference checks is
		// already resolved into `plan.Effects`; this only hands them to the client,
		// which otherwise has no way to learn that casting drew anything.
		session.SkillId = skillId;

		// W-094. The published copy carries the cast it belongs to, so the client
		// can key its effect clock on the sprite it is drawing instead of on a
		// counter that arrives a frame late.
		//
		// The increment comes FIRST. Reading `session.SkillCastSeq` before it put
		// the PREVIOUS cast's number on these sprites, which is the same one-frame
		// lag the fix was meant to remove -- and the symptom was identical:
		// MEASURED 2026-10-18, effect frames [8, 0, 0, 0, ...] on a nine-frame
		// animation, i.e. the tail of the previous effect on the first frame of the
		// new one.
		session.SkillCastSeq++;
		int castSeq = session.SkillCastSeq;
		session.SkillEffects = new List<SkillCastSprite>(
			plan.Effects.Select(e => e with { CastSeq = castSeq }));

		ApplySkillHits(session, body, plan);
		SpawnSkillTiles(session, body, plan);
	}

	/// <summary>
	/// The caster-side grid a cast lays down on the ground under the character.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>Skill.cpp:219-226</c>:
	/// </para>
	/// <code>if (tileeffect &amp;&amp; skillid != 3111003) tileeffect-&gt;apply(user);</code>
	/// <para>
	/// The <b>other</b> half — 3111003's decal on each struck mob — is NOT here.
	/// The reference calls it from <c>Skill::apply_hiteffects</c>
	/// (<c>Skill.cpp:524-525</c>), once per struck mob, and so does
	/// <see cref="ApplySkillHits"/>: it is spawned from inside the same per-mob
	/// loop that already picks the struck mobs, which is the only place that
	/// loop exists. Spawning it from here would either run the hit loop twice
	/// (dealing double damage) or re-derive the struck-mob list (a second
	/// answer, which is the bug the remarks there exist to prevent).
	/// </para>
	/// </remarks>
	private void SpawnSkillTiles(MapSimSession session, CharacterPhysics body, SkillCastPlan plan)
	{
		// Skill.cpp:94-98 — `if (src["tile"] && skillid != 3121006 && skillid != 3221005)`.
		// The constructor is not built for those two, so `tileeffect` is null and
		// neither apply can run. They are summons (Phoenix / Frostprey), which
		// this simulator does not spawn, but the guard is the reference's.
		if (plan.SkillId is "3121006" or "3221005")
			return;

		// Skill.cpp:223 — the caster-side half is exactly "every skill with a tile
		// node EXCEPT 3111003".
		if (plan.TileAtTarget)
			return;

		ApplyCasterTiles(session, body, plan,
			CombatService.RealAttackSpeed(_combat.WeaponAttackSpeed(session.EquippedWeapon)));
	}

	/// <summary>
	/// The caster-side grid: <c>TileFootHoldUseEffect::apply(Char&amp;)</c>,
	/// <c>SkillUseEffect.cpp:214-267</c>, transcribed whole.
	/// </summary>
	private void ApplyCasterTiles(MapSimSession session, CharacterPhysics body, SkillCastPlan plan,
		float realAttackSpeed)
	{
		// `if (tile_animations.empty()) return;`
		if (plan.Tiles.Count == 0)
			return;

		// A NEW cast replaces the previous one's decals rather than adding to them.
		//
		// `EffectLayer::update` (EffectLayer.cpp:44-61) removes an effect when its
		// animation ends, so a cast whose decals are still alive would be drawn on
		// top of the next cast's — and `MapSimFrame.TileEffects` is a list, not a
		// per-cast slot, so two live sets would both be on the wire. The pose lock
		// (`SkillPoseTicks`, Skill.cs:321) means a cast cannot normally start while
		// the previous one plays, and the half-second hold is longer than that lock
		// in practice, so this is the tail rather than the rule.
		session.PendingTileEffects.Clear();

		// :217  Point&lt;int16_t&gt; user_pos = target.get_position();
		int userX = (int)Math.Round(body.X);
		int userY = (int)Math.Round(body.Y);

		(int left, int right, int top, int bottom) area = plan.ResolvedTileArea;

		// :222-232 — 5121001 alone re-reads its own box at the CASTER's level for
		// 5121001 and mirrors it when they face right, because the box is authored
		// for a LEFT-facing cast.
		if (plan.TileAreaFollowsFacing)
		{
			// `if (level["lt"] && level["rb"]) area = Rectangle(level["lt"], level["rb"]);`
			// — and when the re-read finds nothing the cached area STAYS, which is
			// what a null return means here.
			if (_casts.TileAreaAtLevel(plan.SkillId, plan.SkillLevel ?? 1) is { } at)
				area = at;

			// `if (target.is_facing_right()) area = Rectangle(-area.right(), -area.left(), area.top(), area.bottom());`
			// The SAME shape MobAttackGeometry.h:45-49 uses for a mob's attack box:
			// negate the two X corners and swap them, so the box lands on the same
			// side of the character the way they are facing.
			if (session.Facing >= 0)
				area = (-area.right, -area.left, area.top, area.bottom);
		}

		// :233-243 — the world box, then each pair swapped if it came out inverted.
		int wLeft = userX + area.left;
		int wRight = userX + area.right;
		int wTop = userY + area.top;
		int wBottom = userY + area.bottom;
		if (wLeft > wRight) (wLeft, wRight) = (wRight, wLeft);
		if (wTop > wBottom) (wTop, wBottom) = (wBottom, wTop);

		// :243  int16_t step = (effect_distance > 0) ? effect_distance : 180;
		int step = plan.TileStep > 0 ? plan.TileStep : 180;

		// `int16_t x` in the reference's for-loop, so the column count is bounded by
		// the same int16 the client used. A box wider than the map would otherwise
		// iterate forever; the reference stops when x wraps, and this stops there.
		int spawned = 0;
		for (int x = wLeft; x <= wRight && x <= short.MaxValue; x += step)
		{
			// :250  std::vector<int16_t> y_list = fht.get_foothold_range(x, w_top, w_bottom);
			//
			// The map's OWN footholds, through the reader this project already has
			// for the character (FootholdPhysics.GetFootholdRange, a port of the
			// reference's own get_foothold_range). Not a second foothold reader:
			// two of them would answer two different heights for the same slope.
			List<int> yList = session.Map.GetFootholdRange(x, wTop, wBottom);

			foreach (int groundY in yList)
			{
				// :252-255
				//     int16_t jitter_y  = (rand() % 5) - 2;
				//     size_t anim_idx   = rand() % tile_animations.size();
				//     Point<int16_t> rel_pos(x - user_pos.x(), (ground_y + jitter_y) - user_pos.y());
				int jitterY = session.CombatRandom.Next(5) - 2;
				SkillTileSprite sprite = plan.Tiles[session.CombatRandom.Next(plan.Tiles.Count)];

				session.PendingTileEffects.Add(new MapSimTileEffect
				{
					ShiftX = x - userX,
					ShiftY = groundY + jitterY - userY,
					// `DrawArgument(position, facing_right)` (Char.cpp:657). True means
					// the client's `xscale` is -1, i.e. mirrored -- the same wire
					// convention MapSimMobHitEffect.Flipped already uses.
					//
					// W-093. `flip` is TRUE when the caster faces RIGHT: the
					// reference passes `facing_right` itself, and
					// `DrawArgument.h:34-40` turns a true flip into xscale = -1.
					// `session.Facing < 0` was the INVERSE, so the decal was
					// mirrored exactly when it should not have been -- the same
					// inversion as the cast sprite, and the reason the ground
					// effect and the character pointed in opposite directions.
					Flipped = session.Facing >= 0,
					Z = 4,
					CastSeq = session.SkillCastSeq,
					// `effects.add(anim, DrawArgument(position, facing_right), z, get_real_attackspeed())`
					// (Char.cpp:653-658) — the CASTER's attack speed, not 1.0.
					Speed = realAttackSpeed,
					Sprite = sprite,
				});
				spawned++;
			}
		}

		// :260-263 — the fallback. A map with no foothold inside the box (a floating
		// platform map, or an area whose floor is outside the Y band) would otherwise
		// spawn nothing at all and the skill would be silently invisible.
		if (spawned == 0)
		{
			session.PendingTileEffects.Add(new MapSimTileEffect
			{
				ShiftX = 0,
				ShiftY = 0,
				// W-093: `flip` is TRUE facing RIGHT -- see the grid tile above.
				Flipped = session.Facing >= 0,
				Z = 4,
				CastSeq = session.SkillCastSeq,
				Speed = realAttackSpeed,
				Sprite = plan.Tiles[0],
			});
			spawned = 1;
		}

		// The same MEASURED hold as the hit effect: this client polls every ~16 ms
		// and the server advances up to 8 frames per poll, so a decal published on
		// the one frame it was spawned is consumed inside a single poll and the
		// canvas draws none of it. Held for the LONGEST variant's own delays with
		// the same half-second floor, computed once rather than per decal because
		// they are all spawned on the same frame and the longest is the only
		// reading under which one still on screen keeps the window open.
		int holdFrames = 30;
		foreach (MapSimTileEffect fx in session.PendingTileEffects)
		{
			int frames = (int)Math.Ceiling(
				fx.Sprite.Delays.Sum(d => (double)(d > 0 ? d : 100)) / (1000.0 / 60.0));
			holdFrames = Math.Max(holdFrames, Math.Max(30, frames));
		}
		session.TileEffectUntil = session.Frame + holdFrames;
	}

	/// <summary>
	/// Damage the mobs a cast covers, or none at all.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the reference's own behaviour, including the part that looks wrong,
	/// because the whole of it is reachable and only one branch of it lands.
	/// </para>
	/// <para>
	/// <c>Skill.cs:168-178</c> sets <c>MultiStrike = false</c> for exactly three ids
	/// (<c>1121008</c>, <c>1001005</c>, <c>1311012</c>) and true for every other
	/// skill. From there the two halves of the reference's damage system are
	/// arranged so that only the <c>false</c> branch connects to anything:
	/// </para>
	/// <list type="bullet">
	/// <item>
	/// <b>false</b> — six <c>SkillCollision</c> sprites (<c>Skill.cs:206-209</c>,
	/// <c>Count = 6</c>). Each fires <c>Collision()</c> once its StartTime passes
	/// (<c>:467-473</c>), and its own <c>OnCollision</c> (<c>:478-512</c>) does the
	/// work: <c>50000 + Random.Next(700000)</c> at <c>:491</c>, then the mob stops
	/// being collidable (<c>:486</c>) and the collision sprite dies (<c>:509-510</c>).
	/// </item>
	/// <item>
	/// <b>true</b> — six <c>MobCollision</c> objects per mob (<c>Skill.cs:190-204</c>),
	/// each holding the MOB's OWN box (<c>Mob.cs:786</c>) and each firing
	/// <c>Collision()</c> once at its scheduled tick (<c>Mob.cs:788-792</c>). But
	/// their <c>OnCollision</c> is gated on <c>sprite is SkillCollision</c>
	/// (<c>Mob.cs:797</c>) and in this branch <c>Count = 1</c>, so the only
	/// SkillCollision alive is the one <c>DoMove</c> kills without ever calling
	/// <c>Collision()</c> on it (<c>Skill.cs:462-466</c>). And its own
	/// <c>OnCollision</c> returns on the first line for a MultiStrike skill
	/// (<c>:480-481</c>). Nothing consumes either event.
	/// </item>
	/// </list>
	/// <para>
	/// So: a MultiStrike skill casts, plays its pose, spawns its sprites and deals
	/// NO damage, in the reference.
	/// </para>
	/// <para>
	/// <b>W-083, 2026-10-17:</b> that port is REVERSED, and this is the single
	/// biggest reason "the skill does nothing" was true here.
	/// <c>SkillCastService.MultiStrike</c> is false for exactly three ids
	/// (<c>1121008</c>, <c>1001005</c>, <c>1311012</c>) and true for every other
	/// skill, and this method opened with <c>if (plan.MultiStrike) return;</c>.
	/// So three skills in the whole archive dealt damage and the rest played a
	/// complete animation, passed the box test, and hit nothing.
	/// </para>
	/// <para>
	/// ADR-0005 draws the line at "would this make a tester read the wrong
	/// result". A skill that swings, that a mob is standing in, and that changes
	/// no hit point is exactly that: the tester concludes the collision box is
	/// broken. And the thing MultiStrike selects between -- six collision
	/// sprites over the cast's timeline versus one -- cannot mean anything in a
	/// port that resolves a whole cast on one frame anyway. It only had the power
	/// to switch damage off.
	/// </para>
	/// <para>
	/// What still decides whether a hit lands is untouched and is the part that is
	/// about the skill: the box test below, <c>HasCollisionBox</c>, and the "no
	/// damage node, no damage" rule (W-061). How MANY mobs a cast reaches is
	/// <c>plan.MobCount</c> and nothing else: there is no <c>break</c> and no
	/// <c>CanCollision</c> latch in this loop, and <see cref="Skill.cs:486"/>'s
	/// <c>Mob.CanCollision = false</c> is a MapleNecrocer mechanism, not this
	/// reference's — the reference's own limit is the <c>objcount</c> in
	/// <c>find_closest</c> (Combat.cpp:1265).
	/// </para>
	/// </remarks>
	private void ApplySkillHits(MapSimSession session, CharacterPhysics body, SkillCastPlan plan)
	{
		// W-061: a skill the archive gives no damage node deals none.
		//
		// Measured 2026-10-16: 175 of 535 skill entries carry `level/<n>/damage`,
		// and 0 carry no `level` node at all. The 360 without one are mostly
		// buffs, and "a buff deals no damage" is the honest reading of an absent
		// value. Null, never 0 (R-5).
		if (plan.Damage is null) return;

		// ---- W-084. The box, the multiplier, the targets: the reference's own.
		//
		// What this method used to be is a port of MapleNecrocer's `Skill.cs` -- a
		// program pm/PARITY.md does not accept -- and it was wrong in the ways that
		// matter: `Skill.cs:168-178` MultiStrike used as a DAMAGE GATE (63 of 66
		// skills hit nothing), an `+18` offset that is in no reference, and "the
		// first mob whose box overlaps, then stop".
		//
		// The range, and this took three attempts to get right:
		//
		//   SkillData.cpp:79-83  Rectangle range; if (lt && rb) range = Rectangle(lt, rb);
		//   Skill.cpp:459-461    melee:  !stats.range.empty() -> attack.range = stats.range
		//   Skill.cpp:441-456    ranged: reach = stats.attack_range, else the player's
		//                                projectilerange (250, or 300 for a bow), and
		//                                hrange is forced to 1.0 because that reach is
		//                                absolute (:433)
		//   CharStats.cpp:57     projectilerange = bow/crossbow ? 300 : 250
		//
		// So on this archive -- which carries BOTH lt and rb on every skill level --
		// a melee cast's box IS the skill's lt/rb, and the character's reach is the
		// fallback for a skill with neither. Reading `lt`/`rb` as the box was RIGHT;
		// replacing it with the character's range was wrong, and so was removing the
		// MultiStrike gate while leaving everything else as it was.
		bool haveBox = plan.HasCollisionBox;
		int left = haveBox ? plan.Ltx!.Value : -session.Status.ProjectileRange;
		int right = haveBox ? plan.Rbx!.Value : 0;
		int top = haveBox ? plan.Lty!.Value : -75;
		int bottom = haveBox ? plan.Rby!.Value : 25;
		// Combat.cpp:660  int16_t hrange = static_cast<int16_t>(range.left() * attack.hrange);
		// and hrange is the skill's `range` node / 100 (SkillData.cpp:77, default
		// 1.0). Applied to the NEAR edge only. Forced to 1.0 when the range came
		// from the character's absolute reach, per Skill.cpp:433.
		//
		// The CAST is what truncates, not the rounding: `static_cast<int16_t>` on a
		// float truncates toward zero. `Math.Round` rounded instead, which is a
		// different box whenever `left * HRange` is not a whole number -- a skill at
		// range 0.75 over lt=-200 reached -150 where the reference reaches -149.
		int near = haveBox ? (int)(left * plan.HRange) : left;

		// Combat.cpp:662-679, verbatim. `toleft` is facing LEFT.
		bool facingRight = session.Facing >= 0;
		float bx0 = facingRight ? body.X - right : body.X + near;
		float bx1 = facingRight ? body.X - near : body.X + right;
		Box box = new Box(
			(int)Math.Round(bx0), (int)Math.Round(body.Y + top),
			(int)Math.Round(bx1), (int)Math.Round(body.Y + bottom));

		// Combat.cpp:1226-1272 `find_closest`, transcribed whole:
		//
		//     std::multimap<uint16_t, int32_t> distances;
		//     for (auto& mmo : *objs)
		//         if (mob && mob->is_alive() && mob->is_in_range(range))
		//             distances.emplace(mob->get_position().distance(origin), mob->get_oid());
		//     std::vector<int32_t> targets;
		//     for (auto& iter : distances) { if (targets.size() >= objcount) break; targets.push_back(iter.second); }
		//
		// The `objcount` limit is applied to the OUTPUT, never to the scan: every
		// in-range mob is weighed and only then are the nearest `objcount` taken.
		//
		// W-085. This loop carried `&& hits.Count < want` in its CONDITION, so it
		// stopped scanning as soon as `mobcount` mobs had been collected — the first
		// `mobcount` in SESSION ORDER, which is a map's own `life` entries first and
		// then whatever was summoned, and nothing to do with distance. The sort below
		// could not undo it: it only reordered mobs that had already been chosen.
		//
		// MEASURED 2026-10-18 on `1111008` (mobCount 6, box ±200, 8 summoned mobs all
		// inside): the six hit were session slots 24,25,26,27,28,29 — the six
		// lowest indices, contiguous — while the six NEAREST were 25,26,27,28,30,31.
		// A mob 8px away was left standing and a mob 46px away was struck. Same run on
		// `2121007` (mobCount 15) hit slots 24..37, again contiguous from the bottom.
		// The scan is now unbounded, exactly like the reference's.
		int want = Math.Max(1, plan.MobCount);
		List<(MobPatrol Patrol, string MobId, MobFrameGeometry Geometry, int Dist, int Index)> hits = [];
		for (int i = 0; i < session.Mobs.Count; i++)
		{
			// (mobPatrol, mobId, kind, _) -- the tuple order is (patrol, ID, kind, extra),
			// as Swing() destructures it. Reading it the other way round swaps the id
			// and the kind and the loop quietly skips everything.
			var (mobPatrol, mobId, kind, _) = session.Mobs[i];
			if (!mobPatrol.Alive || kind == "n") continue;

			MobAnimation animation = MobAnimation.For(session, i, kind, mobPatrol);
			MobFrameGeometry geometry = _combat.MobFrame(mobId, animation.Stance, animation.FrameIndex, kind);
			if (geometry is null) continue;

			Box other = geometry.At(mobPatrol.X, mobPatrol.Y);
			if (other.IsEmpty || !box.Overlaps(other)) continue;

			// Combat.cpp:1243  uint16_t distance = mob->get_position().distance(origin);
			// -> Point::distance  (Point.h:80-83)  -> Point::length (Point.h:60-65),
			// which is `static_cast<T>(std::sqrt(a*a + b*b))` with T = int16_t: BOTH
			// axes, truncated toward zero, not rounded.
			//
			// This was `Math.Abs(mobPatrol.X - body.X)` — horizontal only. Two mobs at
			// the same |dx| but different heights therefore sorted as tied when the
			// reference separates them, and the order the cut-off is applied in decides
			// WHO gets hit when a skill reaches further than the crowd.
			//
			// The int16 wrap in `length()` is deliberately not reproduced: it only
			// bites past 32767px, which is further than the widest map.
			int dx = (int)Math.Round(mobPatrol.X - body.X);
			int dy = (int)Math.Round(mobPatrol.Y - body.Y);
			hits.Add((mobPatrol, mobId, geometry, (int)Math.Sqrt((double)(dx * dx + dy * dy)), i));
		}

		// Nearest first, and TIES IN SESSION ORDER. `std::multimap<uint16_t,int32_t>`
		// keeps equal keys in insertion order (Combat.cpp:1228,1244) and iterates them
		// in that order (:1263), so two mobs at the same integer distance are taken in
		// the order the scan found them. `List<T>.Sort` is NOT stable, so the index is
		// the tiebreaker written into the comparison rather than left to the sort.
		hits.Sort((a, b) => a.Dist != b.Dist ? a.Dist.CompareTo(b.Dist) : a.Index.CompareTo(b.Index));

		// `for (auto& iter : distances) { if (targets.size() >= objcount) break; ... }`
		// -- the cut is on the ALREADY-SORTED list, so a mobCount larger than the crowd
		// in range simply hits the whole crowd and nothing else.
		if (hits.Count > want)
		{
			hits.RemoveRange(want, hits.Count - want);
		}

		// ---- the effect this cast draws on whatever it hits.
		//
		// `Mob.cpp:1558` adds the animation to the MOB's own effect layer, once per
		// struck mob, at the moment of the hit:
		//
		//     effects.add(animation, DrawArgument(shift, f), z);
		//
		// so it is per mob and it is a "now" -- published on the frame the hit
		// lands and gone on the next, exactly like the damage number beside it.
		SkillHitSprite[] hitFx = ChooseHitSprites(plan);

		foreach (var (mobPatrol, mobId, geometry, _, mobIndex) in hits)
		{
			// Flat test damage (ADR-0006). The reference's client does not compute
			// this at all -- CharStats.cpp:435 reads "TODO: Random stuff, need to
			// find the actual formula somewhere" and the server owns the value -- so a
			// fixed readable one is closer to the reference than DamageRoll.Roll was.
			//
			// W-090. ONE HIT PER DAMAGE LINE, and the number of lines is the skill's
			// own ttackCount / ulletCount.
			//
			//   Mob.cpp:1603-1612, 1620
			//     std::vector<std::pair<int32_t, bool>> result(attack.hitcount);
			//
			//   Skill.cpp:397-405
			//     case Attack::RANGED: attack.hitcount = stats.bulletcount;
			//     default:            attack.hitcount = stats.attackcount;
			//
			// MEASURED 2026-10-18: 1121008 carries level/1/attackCount = 2, so the
			// reference hits TWICE -- and this port was dealing ONE hit, i.e. 100
			// where the reference deals 200, with one damage number where the
			// reference shows two. SkillCastPlan.HitCount carried the number from
			// W-089 and NOTHING consumed it.
			int hitLines = Math.Max(1, plan.HitCount);
			var (headX, headY) = geometry.HeadAt(mobPatrol.X, mobPatrol.Y);
			for (int line = 0; line < hitLines; line++)
			{
				int dealt = mobPatrol.TakeHit(SimTuning.PlayerDamage);
				session.PendingDamage.Add(new MapSimDamageNumber
				{
					MobId = mobId,
					Value = dealt,
					X = headX,
					Y = headY,
					Miss = false
				});
			}

			if (hitFx.Length == 0)
			{
				AddTargetTiles(session, plan, mobIndex);
				continue;
			}

			// `Mob.cpp:1540-1553`: `pos` picks the shift and only `pos == 0` has
			// one -- 1..4 all `break` with `shift` still (0,0). The head is
			// therefore not an offset the client guesses: `get_head_position`
			// (Mob.cpp:1460-1468) returns the mob's own `head` node, which this
			// server has already resolved one line above for the damage number.
			foreach (SkillHitSprite sprite in hitFx)
			{
				double shiftX = 0, shiftY = 0;
				if (sprite.Pos == 0)
				{
					// `position.shift_x((flip && !noflip) ? -head.x() : head.x())` --
					// the mob's OWN facing mirrors its head, so the shift is
					// `head.x` for a mob facing right and `-head.x` for one facing
					// left. The client's mob frame already carries that facing and
					// already mirrors the body about it, so sending the signed
					// shift keeps ONE resolution of "where is this mob's head"
					// instead of a second one in the browser.
					//
					// `noflip` (Mob.cpp:73) is the mob's `info/noFlip`, which makes
					// a mob that never turns keep its art unflipped -- so its head
					// is never mirrored either. No mob in the open Mob.nx carries
					// the node (measured: 0 of 155), so this is the `!noflip`
					// branch on this archive; it is read anyway because the
					// reference reads it and an archive can change.
					(int hx, int hy) = geometry.Head;
					bool mirrorHead = mobPatrol.Facing > 0 && !_combat.MobFlags(mobId).NoFlip;
					shiftX = mirrorHead ? -hx : hx;
					shiftY = hy;
				}

				var effect = new MapSimMobHitEffect
				{
					Pos = sprite.Pos,
					Z = sprite.Z,
					Frames = sprite.Frames,
					ShiftX = shiftX,
					ShiftY = shiftY,
					// `user.flip`, the ATTACKER's -- SkillHitEffect.h:47 takes it
					// as a parameter and every caller passes `user.flip`
					// (SkillHitEffect.cpp:28, SkillHitEffect.h:97), which is the
					// caster's own facing. Reading it off the mob here would mirror
					// the hit art every time the mob turned to face its attacker,
					// which is the one thing the reference never does.
					//
					// W-093. `user.flip` IS `facing_right`.
					//
					//   SkillHitEffect.cpp:28   effect.apply(target, user.flip);
					//   SkillHitEffect.h:47-49 void apply(Mob& t, bool flip)
					//                               { t.show_effect(animation, pos, z, flip); }
					//   Mob.cpp:1558            effects.add(animation, DrawArgument(shift, f), z);
					//   DrawArgument.h:34       DrawArgument(Point position, bool flip)
					//                               : DrawArgument(position, flip, 1.0f) {}
					//   DrawArgument.h:43       pos and center BOTH become `position`
					//   DrawArgument.h:143-152  rlt = pos - center - origin = -origin
					//                           left = cx + xscale * rlt.x
					//
					// and the reference NAMES the identity itself:
					//
					//   Combat.cpp:1100   bool facing_right = player.getflip();
					//   Combat.cpp:1405   bool facing_right = player.getflip();
					//   Summon.cpp:424    bool player_flip = player.getflip();
					//                                // true: 面向右, false: 面向左
					//
					// so `flip == facing_right`, and with `pos == center == shift` the
					// left edge is `shift.x + origin.x` when facing RIGHT and
					// `shift.x - origin.x` when facing LEFT.
					//
					// `!facingRight` was the INVERSE. It had been that way since before
					// W-093 and was reverted once on the strength of "user.flip is not
					// facing_right" -- which took reading Combat.cpp:1100 to disprove.
					Flipped = facingRight,
					CastSeq = session.SkillCastSeq,
					Sprite = sprite,
				};
				if (!session.PendingHitEffects.TryGetValue(mobIndex, out List<MapSimMobHitEffect>? list))
				{
					list = new List<MapSimMobHitEffect>();
					session.PendingHitEffects[mobIndex] = list;
				}
				list.Add(effect);
				// The effect lives for its ANIMATION, not for one frame.
				//
				// The reference's mob holds the effect in its own effect layer and
				// retires it when the animation ends (EffectLayer.cpp:44-61, the layer's own
				// `update`). Publishing it on the single frame the hit landed on and
				// clearing it there is the same idea taken too far: this client
				// polls the server every ~16 ms and the server advances many frames
				// per poll, so the frame that carried the effect was already gone
				// before anything could draw it. MEASURED 2026-10-17: the effect was
				// on the wire on every one of those frames and the canvas drew
				// ZERO of them.
				//
				// The length is the sprite's own delays, in frames of 1/60 s --
				// the same clock the client walks the animation on -- but with a
				// FLOOR of half a second. MEASURED 2026-10-17: at the bare
				// animation length (11 frames for a 2-frame 90ms sprite) the client
				// NEVER saw it -- it polls every ~16 ms and the server advances up
				// to 8 frames per poll, so the whole effect was consumed inside
				// one poll and the canvas drew none of it. The client retires the
				// animation on its own clock, so holding it a little longer costs
				// nothing but the extra frames the client ignores.
				int holdFrames = (int)Math.Ceiling(
					sprite.Delays.Sum(d => (double)(d > 0 ? d : 100)) / (1000.0 / 60.0));
				session.HitEffectUntil[mobIndex] = session.Frame + Math.Max(30, holdFrames);
			}

			AddTargetTiles(session, plan, mobIndex);
		}
	}

	/// <summary>
	/// 3111003's ground decal on one struck mob: <c>TileFootHoldUseEffect::
	/// apply_at_target(Mob&amp;)</c>, <c>SkillUseEffect.cpp:203-212</c>, transcribed
	/// whole.
	/// </summary>
	/// <param name="session">The session, for the <c>rand()</c> stream and the cast id.</param>
	/// <param name="plan">The cast plan, for the variants.</param>
	/// <param name="mobIndex">
	/// The index of the struck mob, so the decal is published on THAT mob's own
	/// effect list — <c>PendingHitEffects</c> is index-keyed for the reason its
	/// remarks give.
	/// </param>
	/// <remarks>
	/// <code>
	/// void TileFootHoldUseEffect::apply_at_target(Mob&amp; target) const
	/// {
	///     if (tile_animations.empty()) return;
	///     const size_t variant = static_cast&lt;size_t&gt;(rand()) % tile_animations.size();
	///     target.show_effect(tile_animations[variant], 1, 4, false);
	/// }
	/// </code>
	/// <para>
	/// Four things travel on that one line and every one of them is honoured:
	/// </para>
	/// <list type="bullet">
	/// <item><b><c>% size()</c></b> — inclusive of the last variant (see
	/// <see cref="SkillCastPlan.TileCount"/>).</item>
	/// <item><b><c>pos == 1</c></b> — <c>Mob.cpp:1546-1548</c> breaks with
	/// <c>shift</c> still <c>(0,0)</c>, so the decal hangs at the mob's own feet
	/// and never at its head. That is <c>ShiftX = ShiftY = 0</c> below.</item>
	/// <item><b><c>z == 4</c></b> — its own layer key, in front.</item>
	/// <item><b>flip <c>false</c></b> — NOT the caster's facing. The caster-side
	/// tile mirrors (<c>Char.cpp:657</c>); this one must not, and reusing the hit
	/// path's <c>Flipped = !facingRight</c> would mirror a decal the reference
	/// leaves upright.</item>
	/// </list>
	/// <para>
	/// <b>Speed 1.0.</b> <c>Mob::show_effect</c> calls the three-argument
	/// <c>EffectLayer::add</c> (<c>EffectLayer.cpp:68-71</c>), whose speed is
	/// <c>1.0f</c> — real time — where the caster-side decal runs at
	/// <c>get_real_attackspeed()</c>. One skill's art, two clocks, and the
	/// difference is on the wire rather than in the browser for that reason.
	/// </para>
	/// <para>
	/// Called once per struck mob because the reference calls it from
	/// <c>Skill::apply_hiteffects</c> (<c>Skill.cpp:524-525</c>), which the client
	/// runs for every mob the attack lands on.
	/// </para>
	/// <para>
	/// <b>Why the tile travels as a <see cref="SkillHitSprite"/>.</b> It is
	/// published on the SAME list as the hit flash and drawn by the SAME pass
	/// (<c>walkview.drawMobHitEffectPass</c>), which already reads
	/// <c>pos</c>/<c>z</c>/<c>shift</c>/<c>flipped</c>/<c>castSeq</c>/<c>sprite</c>
	/// off one entry. The two payloads are the same shape — <c>imagePath</c>,
	/// <c>containerPath</c>, <c>frames</c>, <c>origins</c>, <c>delays</c> — and the
	/// two extra fields hold exactly the literals <c>Mob::show_effect</c> was
	/// called with (<c>Pos = 1</c>, <c>Z = 4</c>), so nothing is invented and no
	/// third rendering path is added. Giving the entry a second sprite type would
	/// mean the client reading <c>sprite ?? tileSprite</c> on every effect to
	/// learn nothing about the 60-odd entries that are plain hit flashes.
	/// </para>
	/// </remarks>
	private static void AddTargetTiles(MapSimSession session, SkillCastPlan plan, int mobIndex)
	{
		// Skill.cpp:524-525 — `if (skillid == 3111003 && tileeffect)`.
		if (!plan.TileAtTarget || plan.Tiles.Count == 0)
			return;

		// :205  if (tile_animations.empty()) return;
		// :207-208  const size_t variant = rand() % tile_animations.size();
		SkillTileSprite tile = plan.Tiles[session.CombatRandom.Next(plan.Tiles.Count)];

		if (!session.PendingHitEffects.TryGetValue(mobIndex, out List<MapSimMobHitEffect>? list))
		{
			list = new List<MapSimMobHitEffect>();
			session.PendingHitEffects[mobIndex] = list;
		}
		list.Add(new MapSimMobHitEffect
		{
			// `target.show_effect(tile_animations[variant], 1, 4, false)` — the
			// positional arguments, kept as the entry's own pos/z.
			Pos = 1,
			Z = 4,
			Frames = tile.Frames,
			// pos == 1 leaves `shift` at (0,0) (Mob.cpp:1546-1548): the mob's feet.
			ShiftX = 0,
			ShiftY = 0,
			// The literal `false` at :209, NOT the caster's facing.
			Flipped = false,
			CastSeq = session.SkillCastSeq,
			// EffectLayer.cpp:68-71 — the three-argument `add` is speed 1.0f.
			Speed = 1f,
			Sprite = new SkillHitSprite(
				// `Group` is the hit GROUP this entry stands in; it is not a hit
				// group and the client reads it only to label the plan output, so
				// the variant's own index is the honest answer here.
				tile.Variant,
				tile.Variant,
				tile.ImagePath,
				tile.ContainerPath,
				tile.Frames,
				tile.Origins,
				tile.Delays,
				1,
				4),
		});

		int holdFrames = (int)Math.Ceiling(
			tile.Delays.Sum(d => (double)(d > 0 ? d : 100)) / (1000.0 / 60.0));
		session.HitEffectUntil[mobIndex] = session.Frame + Math.Max(30, holdFrames);
	}

	/// <summary>
	/// The one hit animation this port draws per struck mob.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Declared own-design, and the reason is a node this port does not model.</b>
	/// The reference picks among several groups <c>hit/0</c>, <c>hit/1</c>, … and
	/// the choice is made by the ATTACK, not by the archive:
	/// </para>
	/// <list type="bullet">
	/// <item><c>FirstTargetHitEffect</c> (<c>SkillHitEffect.h:85-90</c>) —
	/// <c>effects[user.splash_hit]</c>: the first target gets one animation, a
	/// splashed one gets another.</item>
	/// <item><c>ByHitHitEffect</c> (<c>SkillHitEffect.h:104-115</c>) —
	/// <c>effects[user.second_hit]</c>, the damage line.</item>
	/// <item><c>TwoHandedHitEffect</c> / <c>ByLevelHitEffect</c> /
	/// <c>BySkillLevelHitEffect</c> — by weapon or by level.</item>
	/// <item><c>RandomHitEffect</c> (<c>SkillHitEffect.cpp:31-41</c>) — <c>rand()</c>
	/// over the groups.</item>
	/// </list>
	/// <para>
	/// <c>splash_hit</c>, <c>second_hit</c> and the second weapon are all
	/// <c>AttackUser</c> fields this port has no source for: <c>Attack.h:167-176</c>
	/// fills them from the SERVER's attack packet, and there is no server here.
	/// MEASURED over the open Skill.nx: <b>32</b> skills carry more than one
	/// <c>hit</c> group, so this is not a rare shape.
	/// </para>
	/// <para>
	/// So: <b>the FIRST group, played whole.</b> That is <c>SingleHitEffect</c>
	/// (<c>SkillHitEffect.cpp:24</c>, <c>src["hit"]["0"]</c>) — which is a real
	/// class in the reference and is exactly this rule. It is a stated choice, not
	/// a silent omission, and it is wrong for exactly the 32 multi-group skills.
	/// </para>
	/// </remarks>
	private static SkillHitSprite[] ChooseHitSprites(SkillCastPlan plan)
	{
		// ONE animation per hit, not one per frame.
		//
		// `Mob.cpp:1558` is a single `effects.add(animation, ...)`, and the
		// `Animation` it adds already contains every frame -- `Animation.cpp:166-193`
		// builds that list from the `hit/<group>` node. `SkillHitSprite` is the same
		// shape as `SkillCastSprite`, which is ONE sprite: `frames`, `origins` and
		// `delays` describe the whole animation and `imagePath` is its first frame.
		//
		// Publishing one entry per frame (which the first version of this did)
		// would draw the whole animation once per frame on top of itself -- 15
		// identical blits for 15111001's 15-frame hit, and 2 for 1121008's 2-frame
		// one -- so the flash is drawn N times brighter than the reference draws it.
		SkillHitSprite? first = null;
		foreach (SkillHitSprite sprite in plan.HitEffects)
		{
			// Group 0 only, for the reason in the remarks above. Frames of the
			// chosen group are all `Group == 0`, and they are identical apart from
			// `ImagePath` -- so the FIRST one is the animation.
			if (sprite.Group != 0) continue;
			first = sprite;
			break;
		}
		return first is null ? Array.Empty<SkillHitSprite>() : new[] { first };
	}
	private MapTransferDto? TakePortal(MapSimSession session, FootholdPhysics.Portal portal)
	{
		long type = portal.Type;
		if ((ulong)(type - 1) > 1uL || 1 == 0)
		{
			return null;
		}
		LastPortalFailure = null;
		// The region folder is the FIRST digit of the map id, not the last two.
		// `Stage.cpp:315-318`, the authority:
		//     std::string strid  = string_format::extend_id(mapid, 9);
		//     std::string prefix = std::to_string(mapid / 100000000);
		//     ... nl::nx::map["Map"]["Map" + prefix][strid + ".img"];
		// Integer division by 1e8 of a nine-digit id IS its first digit, and the
		// 9-wide `strid` is what the `:000000000` below already produces.
		//
		// This said `portal.ToMap % 100`, which is the last TWO digits. MEASURED
		// 2026-10-16 against the archive, for the two real exits on the test map
		// (`portal/tm` = 100000003 and 100000005, both `pt` = 2):
		//     f4/Map/Map1/100000003.img -> 200      f4/Map/Map3/100000003.img -> 404
		//     f4/Map/Map1/100000005.img -> 200      f4/Map/Map5/100000005.img -> 404
		// So every portal built a path that does not exist, LoadMap threw, and the
		// catch below swallowed it into LastPortalFailure with PendingTransfer
		// left null -- walking into a door did nothing, silently, forever. The
		// sibling site `MapEditorService.Minimap.cs:118` already used
		// `mapId.ToString()[0]`; two halves of one rule had drifted apart.
		string text = $"{session.Archive}/Map/Map{portal.ToMap / 100000000}/{portal.ToMap:000000000}.img";
		FootholdPhysics footholdPhysics;
		try
		{
			footholdPhysics = new FootholdPhysics(LoadMap(text));
		}
		catch (Exception ex)
		{
			LastPortalFailure = $"load {text}: {ex.GetType().Name}: {ex.Message}";
			return null;
		}
		FootholdPhysics.Foot? foot = null;
		foreach (FootholdPhysics.Portal portal2 in footholdPhysics.Portals)
		{
			if (portal2.IsSpawn)
			{
				foot = footholdPhysics.FindBelow(portal2.X, portal2.Y - 2);
				break;
			}
		}
		if (!foot.HasValue)
		{
			LastPortalFailure = "no surface under the spawn portal of " + text;
			return null;
		}
		string path = session.Path;
		session.Path = text;
		session.Map = footholdPhysics;
		session.Body = new CharacterPhysics(footholdPhysics, foot.Value, session.Facing);
		session.Mobs.Clear();
		SpawnMobs(session);
		return new MapTransferDto(path, text, portal.ToMap, portal.Name, (int)session.Body.X, (int)session.Body.Y);
	}

	private void Swing(MapSimSession session, CharacterPhysics body, MapSimInput input)
	{
		// A DEAD CHARACTER DOES NOT SWING. See the note at the call site: the
		// player's attack sits outside `if (!session.Status.Dead)`, which guards the
		// MOB-side combat, and nothing here re-checked it — so a corpse kept
		// swinging, kept killing mobs and kept earning EXP on the frame it reported
		// `playerDead: true`.
		if (session.Status.Dead) return;

		// THE HIT RESOLVES FIRST, AND ALWAYS.
		//
		// Player.cpp:1250-1253 -- `can_attack()` ends in `&& !attacking`, so the
		// reference cannot BEGIN a second swing while one is playing; the hit of
		// the first is not re-armed by holding the key, it lands on its own
		// schedule (Combat.cpp:1794, `get_attackdelay(0)`).
		//
		// This ran the other way round, and it was not a detail: arming re-wrote
		// `PendingSwingHitTicks` and then the resolve decremented it, every frame,
		// for as long as the key was down. The counter could never reach zero, so
		// HOLDING the attack key dealt no damage at all -- a player who held the
		// button, which is what a player does, hit nothing forever. MEASURED: 40
		// frames of a held `attack` key, `lastSwingBox` stayed null and the mob's
		// HP never moved. Tapping the key worked, which is why it survived: every
		// probe up to here TAPPED.
		ResolveSwingHit(session, body);

		// MobAttack.cpp:50's twin on the player's side: one swing at a time.
		if (body.AttackPoseTicks > 0)
		{
			return;
		}
		if (body.OnLadder.HasValue || !body.OnGround)
		{
			return;
		}
		AttackBox attackBox = _combat.AttackBox(session.Weapon);
		if (attackBox.Box.IsEmpty)
		{
			return;
		}
		string[] array = _combat.AttackStancesFor(session.EquippedWeapon);
		string stance = "";
		if (array.Length != 0)
		{
			stance = array[session.CombatRandom.Next(array.Length)];

			// The pose length and the hit delay both come from the archive now.
			// They used to be a flat 48 ticks with the hit applied on the very
			// frame the key went down -- see CombatService.SwingPoseTicks /
			// SwingHitDelayTicks for the citations and the measured numbers.
			int[] delays = _combat.BodyActionDelays(stance);
			int speed = _combat.WeaponAttackSpeed(session.EquippedWeapon);
			int poseTicks = CombatService.SwingPoseTicks(delays, speed, FallbackSwingPoseTicks);
			session.LastSwingPoseMs = poseTicks * (1000.0 / 60.0);
			body.SwingPose(stance, poseTicks);
			session.PendingSwingHitTicks = CombatService.SwingHitDelayTicks(delays, speed, 0);
			session.PendingSwingBox = attackBox.At(body.X, body.Y, session.Facing < 0);
			session.SwingHitPending = true;
		}
	}

	/// <summary>
	/// The flat pose length used only when the archive has no delays for the stance.
	/// </summary>
	/// <remarks>
	/// 48 ticks = 800ms at 60Hz, which is what swingO1/swingT1 measure to in the
	/// open archive (`Character/00002000.img/swingO1/{0,1,2}/delay` = 300/150/350).
	/// It is a FALLBACK for an unreadable archive, not the answer -- before this
	/// change it was the answer for every weapon in the game.
	/// </remarks>
	private const int FallbackSwingPoseTicks = 48;

	/// <summary>
	/// Apply the swing's hit, if the delay has run out.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The box is REBUILT at the moment the hit resolves, not kept from the keypress.
	/// The reference does the same: <c>Combat.cpp:1794</c> asks for
	/// <c>get_attackdelay(0)</c> and searches the map then, so both sides of the
	/// overlap test are evaluated at the hit.
	/// </para>
	/// <para>
	/// MEASURED, and this is not a detail. The delay is ~18 ticks on a 1H sword, and
	/// a mob's own box CHANGES with its animation frame
	/// (<c>_combat.MobFrame(mobId, stance, frameIndex)</c> is a per-frame lookup).
	/// Capturing the player's box at the keypress and testing it 18 frames later
	/// left the swing resolving on schedule and silently missing every time --
	/// poseMs 616.67, delayTicks 18, pending True -> False, damage never.
	/// </para>
	/// <para>
	/// The character cannot have moved in between: <c>SwingPose</c> locks the body
	/// for the whole pose, which is exactly the window the delay sits inside.
	/// </para>
	/// </remarks>
	private void ResolveSwingHit(MapSimSession session, CharacterPhysics body)
	{
		if (session.PendingSwingHitTicks > 0)
		{
			session.PendingSwingHitTicks--;
			return;
		}
		if (!session.SwingHitPending)
		{
			return;
		}
		session.SwingHitPending = false;
		session.LastSwingMiss = null;

		AttackBox box0 = _combat.AttackBox(session.Weapon);
		if (box0.Box.IsEmpty)
		{
			return;
		}
		Box box = box0.At(body.X, body.Y, session.Facing < 0);
		session.LastSwingBox = box;
		if (box.IsEmpty)
		{
			return;
		}

		for (int i = 0; i < session.Mobs.Count; i++)
		{
			var (mobPatrol, mobId, kind, _) = session.Mobs[i];
			if (!mobPatrol.Alive)
			{
				continue;
			}
			MobAnimation mobAnimation = MobAnimation.For(session, i, kind, mobPatrol);
			MobFrameGeometry mobFrameGeometry = _combat.MobFrame(mobId, mobAnimation.Stance, mobAnimation.FrameIndex, kind);
			if ((object)mobFrameGeometry == null)
			{
				continue;
			}
			Box other = mobFrameGeometry.At(mobPatrol.X, mobPatrol.Y);
			if (other.IsEmpty || !box.Overlaps(other))
			{
				// The FIRST miss, not the last: the loop runs over every mob on the
				// map and overwrites this each time, so a single-mob map reports the
				// far one and hides the near one that actually mattered.
				session.LastSwingMiss ??= "";
				session.LastSwingMiss +=
					$"[{mobId}@{mobPatrol.X:F0} {other.Left},{other.Top},{other.Right},{other.Bottom}]";
				continue;
			}
			// 2026-10-17: flat test damage, the same `SimTuning.PlayerDamage` a
			// skill uses. DamageRoll.Roll -- level scaling, the mob's `info/PDDamage`
			// defence term, two floors -- is still implemented and is the
			// reference's formula, but it is not what a test reads: it made one
			// swing a number nobody could check by eye.
			int damage = SimTuning.PlayerDamage;
			int value = mobPatrol.TakeHit(damage);
			if (mobPatrol.Dying && !session.ExpPaidFor.Contains(mobPatrol))
			{
				session.ExpPaidFor.Add(mobPatrol);
				long num = _combat.MobExp(mobId);
				if (num > 0)
				{
					session.PendingExp += num;
					session.PendingLevels += session.Status.GainExp(num);
				}
			}
			var (x, y) = mobFrameGeometry.HeadAt(mobPatrol.X, mobPatrol.Y);
			session.PendingDamage.Add(new MapSimDamageNumber
			{
				MobId = mobId,
				Value = value,
				X = x,
				Y = y,
				Miss = false
			});
			break;
		}
	}

	/// <summary>
	/// Swap the equipped weapon on a live character, or disarm it with null.
	/// </summary>
	/// <returns>False when no live character carries that id.</returns>
	/// <remarks>
	/// The swing group is derived per swing inside <c>Swing</c>, not cached here, so there
	/// is nothing to invalidate and this is safe to call mid-animation: a pose already in
	/// flight keeps the stance it started with, and the next swing picks from the new
	/// weapon's group.
	/// </remarks>
	public bool SetEquippedWeapon(string id, string? weapon)
	{
		// The one-slot convenience, expressed as the general case: a weapon and
		// nothing else. Keeping the two on ONE path is what stops them disagreeing
		// about what "equipped" means, which is exactly the bug class this project
		// keeps hitting (three id-width defects in one sitting, W-041).
		var items = string.IsNullOrWhiteSpace(weapon)
			? null
			: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [WeaponSlot] = weapon.Trim() };
		return SetEquipped(id, items);
	}

	/// <summary>
	/// Replace the WHOLE equipped set and re-derive the sheet from it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Every equipped item contributes, not only the weapon. The reference's sheet is
	/// base + the sum over the inventory's EquipStat totals (Player.cpp:309-313), and
	/// the archive carries <c>incPDD</c> on 326 of 840 sampled items, <c>incSTR</c> on
	/// 123, <c>incDEX</c> on 134 — so a coat changes accuracy, max HP and max damage,
	/// not only what the character looks like.
	/// </para>
	/// <para>
	/// An EMPTY string is a strip and a MISSING key is an absence, and they are not
	/// the same statement: the client sends the whole set every time, so a missing
	/// key means "this slot was never equipped" while <c>""</c> means "and take it
	/// off". Both end with nothing in that slot, which is the right answer for both.
	/// </para>
	/// </remarks>
	public bool SetEquipped(string id, IReadOnlyDictionary<string, string>? items)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession? session))
			return false;

		session.EquippedItems.Clear();
		if (items is not null)
		{
			foreach ((string slot, string image) in items)
			{
				if (string.IsNullOrWhiteSpace(image)) continue;
				if (!EquipSlots.Contains(slot)) continue;
				session.EquippedItems[slot] = image.Trim();
			}
		}

		// The swing box and the sheet both read the main hand. `Weapon-2` is the
		// second-hand slot and the reference reads the swing group from the weapon
		// in hand, so filling the off hand must not change the pose or the damage.
		session.EquippedWeapon = session.EquippedItems.TryGetValue(WeaponSlot, out string? hand)
			? hand
			: null;

		// The sheet follows the equipment. Before this, equipping a weapon changed
		// the POSE and the swing's CLOCK (AttackStancesFor / WeaponAttackSpeed) but
		// not a single number the swing did damage with. Player::recalc_stats
		// (Player.cpp:299-360) re-derives the whole sheet on an equip change, so that
		// is what happens here: a FRESH sheet plus every item, not an increment.
		PlayerStatus rebuilt = BuildStatus(session.EquippedItems);
		// Invulnerable is a session setting, not a sheet derivation, and it is NOT
		// re-derived by recalc_stats in either reference. Dropping it here made an
		// equipped character mortal mid-test with no visible cause.
		rebuilt.Invulnerable = session.Status.Invulnerable;
		// W-084: the REACH follows the weapon in hand, because the reach is the
		// character's and a bow reaches further. CharStats.cpp:57
		//   projectilerange = Weapon::is_bow_or_crossbow(weapontype) ? 300 : 250;
		// with the type read as `equipid / 10000` (WeaponData.cpp:28-29) and
		// BOW = 145, CROSSBOW = 146 (Character/Inventory/Weapon.h).
		// Lost on rebuild otherwise, which is how it read 250 with a bow on.
		rebuilt.ProjectileRange = CombatService.ProjectileRange(session.EquippedWeapon);
		session.Status = rebuilt;
		return true;
	}

	/// <summary>The client slot name the main hand is chosen in.</summary>
	private const string WeaponSlot = "Weapon-1";

	/// <summary>
	/// Turn damage-taking on or off for a live session.
	/// </summary>
	/// <remarks>
	/// <c>Player.cpp:1252</c>, <c>can_attack()</c>: <c>!has_buff(Buffstat::Id::STUN)</c>
	/// and <c>Stage.cpp:794</c>, <c>player.is_invincible()</c> — the client has
	/// an invincibility flag the character carries, and both the attack path and the
	/// damage path test it.
	///
	/// <para>
	/// It existed here only as a START parameter, which no caller could reach: the
	/// browser never sent it. MEASURED, that made a 50 HP character on
	/// <c>f4/Map/Map1/100000004.img</c> dead within seconds of walking into the
	/// mobs — <c>info/PADamage</c> is 90 to 150 there against a level-1 sheet's 50 HP
	/// — and a simulator whose purpose is to watch a mob's mechanics is one where
	/// you die before the projectile fires.
	/// </para>
	/// </remarks>
	/// <returns>The frame after the change, or null when the session is gone.</returns>
	public MapSimFrame? SetInvulnerable(string sessionId, bool value)
	{
		if (!_sessions.TryGetValue(sessionId, out MapSimSession? session))
			return null;
		lock (Gate)
		{
			session.Status.Invulnerable = value;
			session.Invulnerable = value;
			// Turning it ON is a heal: `Dead => Hp <= 0 && !Invulnerable` already
			// stops reporting the character as dead, but a corpse still has 0 HP and
			// would be one hit away from being one again.
			if (value && session.Status.Hp <= 0)
				session.Status.Hp = session.Status.MaxHp;
			session.BodyDeadPoseCleared = session.Status.Dead;
		}
		return Describe(session, session.Frame);
	}

	/// <summary>
	/// The slots that carry equipment stats.
	/// </summary>
	/// <remarks>
	/// The client's <c>PICKABLE_SLOTS</c> (avatar.js:148-151) minus the four that are
	/// not equipment: <c>Head</c> and <c>Body</c> are the character's own images
	/// (<c>00012000.img</c> / <c>00002000.img</c>), and <c>Hair-1</c> / <c>Face-1</c>
	/// are hair and face. None of the four is under a category folder
	/// <c>EquipStatsService</c> looks in, so they would resolve to nothing anyway —
	/// they are filtered here so the sheet's failure cannot look like "that slot
	/// contributes nothing".
	/// </remarks>
	private static readonly HashSet<string> EquipSlots = new(StringComparer.OrdinalIgnoreCase)
	{
		"Cap-1", "Coat", "Pants", "Longcoat", "Cape", "Shield", "Glove", "Shoes",
		"Weapon-1", "Weapon-2",
	};

	/// <summary>
	/// The character sheet for a given set: the reference's level-1 base plus each
	/// item's own <c>info/inc*</c> totals.
	/// </summary>
	/// <remarks>
	/// Every item is applied in turn, so <c>PlayerStatus</c> accumulates rather than
	/// replacing. That is the reference's own shape —
	/// <c>Inventory::recalc_stats</c> (Inventory.cpp:47-48) then
	/// <c>Player::recalc_stats</c> (Player.cpp:309-313) sum the inventory and add
	/// every EquipStat to the base.
	/// </remarks>
	private PlayerStatus BuildStatus(IReadOnlyDictionary<string, string>? items)
	{
		var status = new PlayerStatus();
		// W-084: seed the reach from the main hand, so a session opened WITH a bow
		// reaches like one. `SetEquipped` re-derives it on every change; this is the
		// path that builds the FIRST sheet, where an unarmed 250 would otherwise be
		// baked in and the weapon set later would have to correct it.
		status.ProjectileRange = CombatService.ProjectileRange(
			items is not null && items.TryGetValue(WeaponSlot, out string? hand) ? hand : null);
		if (items is null) return status;
		foreach (string image in items.Values)
		{
			if (string.IsNullOrWhiteSpace(image)) continue;
			EquipStatsDto? equips = _equips?.Describe(image);
			if (equips is { Found: true }) status.ApplyEquip(equips);
		}
		return status;
	}

	/// <summary>What a binding request produced, and what it refused and why.</summary>
	public sealed record SkillBindingResult(
		IReadOnlyDictionary<string, string> Bindings,
		IReadOnlyList<string> Rejected);

	/// <summary>The reference's hotkey choices, verbatim: "A" through "Z".</summary>
	/// <remarks>
	/// <c>SkillForm.Designer.cs:50</c> hardcodes those twenty-six items into
	/// <c>comboBox1</c>. A caller offering any other key would be offering a control
	/// the reference does not have, so a key outside this set is refused rather than
	/// quietly accepted.
	/// </remarks>
	public static bool IsBindableKey(string key) =>
		key.Length == 1 && key[0] is >= 'A' and <= 'Z';

	/// <summary>
	/// Letters this project's MOVEMENT already owns, so a skill bound to one can
	/// never fire.
	/// </summary>
	/// <remarks>
	/// <para>
	/// OWN DESIGN, and a third exclusion the reference does not have.
	/// <c>SkillForm.cs:156-162</c> excludes a duplicate skill and a duplicate key,
	/// and nothing else — because the reference MOVES ON THE ARROW KEYS
	/// (<c>InputKey.h</c>) and ATTACKS ON LEFT_CONTROL (<c>MapleCharacter.cs:436-450</c>).
	/// Its twenty-six letters are free.
	/// </para>
	/// <para>
	/// This project added WASD and Z. Both are GONE or accounted for:
	/// <c>livewalk.keyFor</c> now binds only the four arrow keys
	/// (<c>Keyboard.cpp:78-81</c>) — WASD was this project's own addition, was
	/// never declared as one, and was removed — leaving only Z, which is declared
	/// at the call site with the reason it exists at all (Ctrl cannot reach the
	/// handler).
	/// </para>
	/// <para>
	/// <c>livewalk.skillFor</c> checks <c>keyFor(e.key)</c> BEFORE
	/// <c>skillKeys.get(key)</c>, so a skill bound to Z is silently dead: the
	/// panel would say "1 bound" and nothing would ever cast. MEASURED 2026-10-16:
	/// the server accepted A, D, W, S and Z with <c>rejected: []</c>.
	/// </para>
	/// <para>
	/// Refusing the binding with a reason is better than accepting it and losing
	/// the key, and better than hiding the letter in the dropdown — the same
	/// reasoning the panel already applies to a key that is "in use"
	/// (walkview.js:1855-1862): an exclusion the caller cannot see is an exclusion
	/// they learn only by it working.
	/// </para>
	/// </remarks>
	public static readonly IReadOnlySet<string> MovementKeys =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Z" };

	/// <summary>
	/// A key the reference's form offers AND this project's movement does not own.
	/// </summary>
	public static bool IsUsableSkillKey(string key)
		=> IsBindableKey(key) && !MovementKeys.Contains(key);

	/// <summary>
	/// Replace the whole binding map, applying the reference's two mutual
	/// exclusions in the order the rows were sent.
	/// </summary>
	/// <param name="requested">Skill id and key, in the order the caller sends them.</param>
	/// <remarks>
	/// <c>SkillForm.cs:156-174</c>. For each row, in order: drop any existing
	/// binding for that SKILL, drop any existing binding for that KEY, then add.
	/// Processing in submission order is what makes the last row win a collision ??
	/// the same reason the reference rebuilds from the grid instead of patching.
	/// </remarks>
	public SkillBindingResult? BindSkills(string id, IReadOnlyList<(string SkillId, string Key)> requested)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession? session))
			return null;

		var rejected = new List<string>();
		var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var skillToKey = new Dictionary<string, string>(StringComparer.Ordinal);

		for (int i = 0; i < requested.Count; i++)
		{
			string skillId = requested[i].SkillId?.Trim() ?? "";
			string key = requested[i].Key?.Trim() ?? "";

			if (skillId.Length == 0)
			{
				rejected.Add($"row {i}: no skill id given");
				continue;
			}
			if (!IsBindableKey(key))
			{
				// Named with the reference's own bound, so the message says what is
				// allowed rather than merely that something was not.
				rejected.Add($"row {i}: '{key}' is not a key the reference offers (SkillForm.Designer.cs:50 hardcodes A-Z)");
				continue;
			}
			if (MovementKeys.Contains(key))
			{
				// The third exclusion, and the only one the reference does not have.
				// See MovementKeys for why the collision is this project's.
				rejected.Add($"row {i}: '{key}' is bound to movement or attack in this "
					+ "simulator, so a skill on it could never be pressed");
				continue;
			}

			if (skillToKey.TryGetValue(skillId, out string? displaced))
				built.Remove(displaced);
			built.Remove(key);

			built[key] = skillId;
			skillToKey[skillId] = key;
		}

		lock (session.InputLock)
		{
			session.SkillHotKeys.Clear();
			foreach (KeyValuePair<string, string> pair in built)
				session.SkillHotKeys[pair.Key] = pair.Value;
		}

		return new SkillBindingResult(
			new Dictionary<string, string>(session.SkillHotKeys, StringComparer.OrdinalIgnoreCase),
			rejected);
	}

	/// <summary>
	/// Drop every binding ??the reference's grid losing its rows.
	/// </summary>
	/// <remarks>
	/// <c>SkillForm.cs:168</c> starts every rebuild with <c>Skill.HotKeyList.Clear()</c>.
	/// There is no separate "clear" verb in the reference because deleting the last
	/// row and rebuilding does this for free; the explicit route exists because a
	/// browser has to be able to say it.
	/// </remarks>
	public int? ClearSkillBindings(string id)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession? session))
			return null;

		lock (session.InputLock)
		{
			int count = session.SkillHotKeys.Count;
			session.SkillHotKeys.Clear();
			return count;
		}
	}

	/// <summary>The bindings as the caller set them, for a client that re-reads.</summary>
	public IReadOnlyDictionary<string, string>? SkillBindings(string id)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession? session))
			return null;
		lock (session.InputLock)
			return new Dictionary<string, string>(session.SkillHotKeys, StringComparer.OrdinalIgnoreCase);
	}

	public bool Hold(string id, MapSimInput input)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession value))
		{
			return false;
		}
		lock (value.InputLock)
		{
			value.Held.Left = input.Left;
			value.Held.Right = input.Right;
			value.Held.Up = input.Up;
			value.Held.Down = input.Down;
		}
		value.LastInputMs = Environment.TickCount64;
		value.ManualUntilMs = 0L;
		return true;
	}

	internal MapSimSession? Any()
	{
		using (IEnumerator<MapSimSession> enumerator = _sessions.Values.GetEnumerator())
		{
			if (enumerator.MoveNext())
			{
				return enumerator.Current;
			}
		}
		return null;
	}

	/// <summary>One named session, or null. The counterpart to <see cref="Any"/>.</summary>
	/// <remarks>
	/// <see cref="Any"/> hands back whichever session happens to be first in the
	/// dictionary, which on a long-lived server is very often a session a caller
	/// leaked and never advanced -- one whose per-frame logs are empty. A debug
	/// endpoint that can be pointed at a session is a diagnostic; one that always
	/// answers about an arbitrary session is a coin flip.
	/// </remarks>
	internal MapSimSession? Find(string id)
	{
		lock (Gate)
		{
			return _sessions.TryGetValue(id, out MapSimSession? session) ? session : null;
		}
	}

	internal FootholdPhysics.Foot? FindStart(FootholdPhysics physics, int x, int? startY)
	{
		return MapEditorService.FindStartFootholdShared(physics, x, startY);
	}

	internal MapDocument LoadMap(string path)
	{
		return _maps.LoadForReadingShared(path);
	}

	public MapSimFrame Start(string path, int? startX, int? startY, int direction, string? weapon = null, string? equippedWeapon = null, bool invulnerable = false, IReadOnlyDictionary<string, string>? items = null)
	{
		Sweep(Environment.TickCount64);
		FootholdPhysics footholdPhysics;
		FootholdPhysics.Foot? foot;
		lock (Gate)
		{
			footholdPhysics = new FootholdPhysics(LoadMap(path));
			int num = 0;
			int num2 = 0;
			foreach (FootholdPhysics.Portal portal in footholdPhysics.Portals)
			{
				if (portal.IsSpawn)
				{
					num = portal.X;
					num2 = portal.Y;
					break;
				}
			}
			int x = startX ?? num;
			int num3 = startY ?? (num2 - 2);
			foot = FindStart(footholdPhysics, x, num3);
			if (!foot.HasValue)
			{
				foot = footholdPhysics.FindBelow(x, num3);
			}
			if (!foot.HasValue)
			{
				return new MapSimFrame
				{
					Error = $"nothing to stand on at x={startX}, y={startY}"
				};
			}
		}
		CharacterPhysics characterPhysics = new CharacterPhysics(footholdPhysics, foot.Value, (direction == 0) ? 1 : direction);
		string text = Guid.NewGuid().ToString("N").Substring(0, 16);
		long tickCount = Environment.TickCount64;
		MapSimSession mapSimSession = new MapSimSession
		{
			Id = text,
			Path = path,
			Archive = path.Split('/', 2)[0],
			Map = footholdPhysics,
			Body = characterPhysics,
			LastFoothold = foot.Value.Id.ToString(),
			LastX = characterPhysics.X,
			Facing = ((direction >= 0) ? 1 : (-1)),
			LastInputMs = tickCount,
			LastWakeMs = tickCount,
			CombatRandom = new Random(StableSeed(text + ":combat")),
			Weapon = (string.IsNullOrWhiteSpace(weapon) ? null : weapon),
			EquippedWeapon = (string.IsNullOrWhiteSpace(equippedWeapon) ? null : equippedWeapon),
			Invulnerable = invulnerable
		};
		mapSimSession.Status.Invulnerable = invulnerable;
		SpawnMobs(mapSimSession);
		_sessions[text] = mapSimSession;

		// The rest of the armour, after the session exists -- SetEquipped needs a
		// stored session, and it is the one place that knows how to build a sheet
		// from a set.
		//
		// `items` REPLACES the set, so a caller that sent none would wipe the very
		// weapon `equippedWeapon` just named. That is not theoretical: it made
		// `attack-test.ps1` report "attackStance was EMPTY on every swing" the
		// moment this call was added. So the weapon seeds the set and an explicit
		// map then replaces it wholesale -- the two answer different questions
		// (swing box vs sheet) and a caller may legitimately send only one.
		var seed = string.IsNullOrWhiteSpace(equippedWeapon)
			? null
			: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				[WeaponSlot] = equippedWeapon.Trim(),
			};
		SetEquipped(text, items ?? seed);
		return Describe(mapSimSession, 0);
	}

	/// <summary>
	/// Put a dead character back on its feet at the map's spawn portal.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>Stage::respawn(int8_t portalid)</c> and <c>Player::respawn(Point, bool)</c>
	/// (<c>Stage.cpp:497-508</c>, <c>Player.cpp:210-251</c>), reached from the
	/// revive prompt <c>UIStateGame.cpp:548-556</c> puts the character on. Its
	/// comment says what the rest of this method exists for: <i>"Coming back from a
	/// death leaves the state behind otherwise, and a revived character would stay
	/// a tombstone."</i>
	/// </para>
	/// <para>
	/// MEASURED before this existed, and it is a dead end that looks like a broken
	/// build rather than a death: a corpse's <c>Swing</c> and <c>CastSkill</c> both
	/// refuse (correctly — a corpse does not fight), and with nothing to revive it
	/// the walk never ended. On <c>f4/Map/Map0/000000000.img</c> with
	/// <c>4220000</c> summoned, contact damage (<c>PADamage 135</c>) killed a 50 HP
	/// character on frame 0 and from then on the attack key did nothing, the skill
	/// key did nothing, the character would not walk, and
	/// <c>/revive</c>, <c>/respawn</c> and <c>/portal</c> were all 404. The user's
	/// report — "the player cannot attack, and cannot cast skills either" — is this
	/// and nothing else.
	/// </para>
	/// <para>
	/// What is restored, per <c>Player.cpp:210-251</c>: the position, the whole
	/// movement state (<c>hspeed/vspeed/vx/vy/inputx/inputy/fhid/fhlayer/detached
	/// </c>), <c>attacking = false</c>, the ladder, and the STAND state. What is NOT
	/// restored is HP — the revive prompt spends a scroll or the wheel, and this
	/// simulator has neither, so HP comes back to full and that is declared here
	/// rather than silently assumed.
	/// </para>
	/// </remarks>
	/// <returns>The frame the character comes back on, or null when the session is gone.</returns>
	public MapSimFrame? Revive(string sessionId)
	{
		if (!_sessions.TryGetValue(sessionId, out MapSimSession? session))
			return null;

		lock (Gate)
		{
			int x = 0;
			int y = 0;
			bool have = false;
			foreach (FootholdPhysics.Portal portal in session.Map.Portals)
			{
				if (!portal.IsSpawn)
					continue;
				x = portal.X;
				y = portal.Y;
				have = true;
				break;
			}
			if (!have)
			{
				// No spawn portal to go back to. Staying where it fell is better than
				// refusing: the walk continues and the character is simply alive again.
				x = (int)session.Body.X;
				y = (int)session.Body.Y;
			}
			// The same two steps `Start` takes, for the same reason: the spawn portal's
			// own Y is where the marker is, and the body stands `2` above it on
			// whatever foothold is under there (`Start`, `startY ?? portalY - 2`).
			// Using the portal's raw Y instead put the character at y=0 -- the top of
			// the map -- and MEASURED: it revived there and walked happily along a
			// ledge it had no business being on.
			y -= 2;
			FootholdPhysics.Foot? foot = FindStart(session.Map, x, y) ?? session.Map.FindBelow(x, y);
			if (!foot.HasValue)
				return null;
		
			// STAND ON THE FOOTHOLD, not at the coordinates we asked for.
			//
			// CharacterPhysics.Respawn takes the position verbatim (CharacterPhysics.cs:202-207),
			// while the CONSTRUCTOR -- which is what Start uses -- derives it from the
			// foothold's own end (_y = start.Y1 / start.Y2, :147-148). Revive passed the spawn
			// portal's Y, and where that portal is nowhere near the ground the body was placed
			// in mid-air with OnGround = true and a foothold 97 px below it.
			//
			// MEASURED on f4/Map/Map0/000000000.img: the session spawns at (0, 95) and walks;
			// after a revive the same body was at (0, -2) and pressing Right moved it 0 px for
			// 20 frames -- the foothold test rejects every step because the body is not on the
			// platform it claims. A revived character that cannot walk is the whole reason the
			// revive suite exists, and it was the spawn PORTAL's y that put it there.
			//
			// X stays where the portal put it whenever that x is over the foothold, because the
			// portal is what says where the spawn point is; only Y comes from the platform,
			// because only the platform knows how high the ground is.
			FootholdPhysics.Foot f = foot.Value;
			int placeX = x < f.X1 ? f.X1 : x > f.X2 ? f.X2 : x;
			int placeY = placeX <= (f.X1 + f.X2) / 2 ? f.Y1 : f.Y2;
			session.Body.Respawn(f, placeX, placeY);
		session.LastFoothold = foot.Value.Id.ToString();
			session.LastX = session.Body.X;
			session.PrevX = session.Body.X;
			session.PrevY = session.Body.Y;
			session.CurrentPortal = session.Map.FindPortal(x, y);
			session.PendingSwingHitTicks = 0;
			session.SwingHitPending = false;
			session.PendingSwingBox = null;
			session.SkillPoseTicks = 0;
			session.LastInputMs = Environment.TickCount64;
			session.LastWakeMs = Environment.TickCount64;
			// Player.cpp:247 `death_handled = false` and `:248 set_state(STAND)`.
			session.Status.Revive();
			session.BodyDeadPoseCleared = false;
		}
		return Describe(session, session.Frame);
	}

	/// <summary>
	/// What the reference's summon form does, and what came back.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Own-design, declared:</b> the benchmark has no session, so it cannot
	/// report anything; this DTO is how the caller learns that 3 of 5 requested
	/// summons landed outside the map bounds and were dropped. The reference drops
	/// them silently (<c>MobForm.cs:117</c> is an <c>if</c> with no <c>else</c>).
	/// </para>
	/// </remarks>
	public sealed class SummonResult
	{
		public string MobId { get; set; } = "";

		/// <summary>The id that actually draws, after <c>info/link</c> was followed.</summary>
		public string ResolvedId { get; set; } = "";

		public int Requested { get; set; }

		public int Spawned { get; set; }

		/// <summary>Why the rest did not appear. Empty when everything landed.</summary>
		public List<string> Rejected { get; } = new List<string>();

		/// <summary>Indices in <c>Mobs</c> that this call added.</summary>
		public List<int> Indexes { get; } = new List<int>();
	}

	/// <summary>
	/// Summon mobs around the character, mirroring <c>MobForm.cs:107-124</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The reference's button, verbatim in behaviour: spawn <c>count</c> mobs, each
	/// at a uniformly random x within 100px of the player
	/// (<c>Random.Next((int)Game.Player.X - 100, (int)Game.Player.X + 100)</c>),
	/// dropped entirely when that x falls outside <c>[Map.Left, Map.Right]</c>, and
	/// placed 100px ABOVE the player. The count box is parsed as an integer and a
	/// non-numeric value spawns nothing at all.
	/// </para>
	/// <para>
	/// Two guards come from <c>Mob.cs:102-175</c>: the mob must exist in the archive
	/// (<c>MobForm.cs:109</c>), and after <c>info/link</c> is followed the RESOLVED id
	/// must carry <c>stand/0</c> or <c>fly/0</c> (<c>Mob.cs:123</c>) ??a linked mob
	/// whose target has no art is not summonable, and that is a different failure from
	/// "no such mob".
	/// </para>
	/// <para>
	/// The move type comes from the same block (<c>Mob.cs:166-175</c>): no
	/// <c>move</c> node means a stationary mob at speed 0, <c>jump</c> beats
	/// <c>move</c>, and <c>fly</c> beats both. Speed is
	/// <c>(1 + info/speed/100) * 2</c>, default 2 (<c>Mob.cs:148-151</c>).
	/// </para>
	/// </remarks>
	private SummonResult Summon(MapSimSession session, string mobId, int count, string? kind = null)
	{
		SummonResult result = new SummonResult { MobId = mobId, Requested = count };
		if (string.IsNullOrWhiteSpace(mobId))
		{
			result.Rejected.Add("no mob id given");
			return result;
		}

		MobSpriteDto? sprite = MobAnimation.Sprites?.Describe(null, mobId, kind);
		if (sprite is null || string.IsNullOrEmpty(sprite.ImagePath))
		{
			// MobForm.cs:109 -- "no such mob" is a refusal, not an exception.
			result.Rejected.Add($"{mobId}: not in any open Mob archive");
			return result;
		}
		result.ResolvedId = sprite.MobId;

		// Mob.cs:123 -- the guard is on the RESOLVED id, because info/link is what
		// decides where the art is.
		bool drawable = sprite.Actions.Any(a => (a.Name == "stand" || a.Name == "fly") && a.Frames.Count > 0);
		if (!drawable)
		{
			result.Rejected.Add($"{mobId} (draws as {sprite.MobId}): has no stand/0 or fly/0");
			return result;
		}

		// Mob.cs:166-175, most specific wins: fly > jump > move, and a mob with no
		// move node cannot move at all.
		bool hasMove = sprite.Actions.Any(a => a.Name == "move" && a.Frames.Count > 0);
		bool hasJump = sprite.Actions.Any(a => a.Name == "jump" && a.Frames.Count > 0);
		bool hasFly = sprite.Actions.Any(a => a.Name == "fly" && a.Frames.Count > 0);
		MobPatrol.MobMoveType moveType = hasFly ? MobPatrol.MobMoveType.Fly : (hasJump ? MobPatrol.MobMoveType.Jump : MobPatrol.MobMoveType.Move);
		// Mob.cs:147-151 for the number, Mob.cs:166-170 for the override: a mob with
		// no `move` node is given speed 0 and stands, whatever its `info/speed` says.
		float moveSpeed = hasMove ? _combat.MobSpeed(mobId) : 0f;
		// Mob.cpp:83 -- `canmove = anisrc["move"].size() > 0 || canfly`.
		bool canMove = hasMove || hasFly;
		var (noFlip, notAttack) = _combat.MobFlags(mobId);

		CharacterPhysics body = session.Body;

		// MobForm.cs:117 compares against Map.Left / Map.Right. Those names are the
		// REFERENCE's; here the walkable extent is `FootholdPhysics.Bounds`, a
		// nullable (Left, Top, Right, Bottom) tuple -- and `Foot.Left`/`Foot.Right`
		// are per-foothold endpoints, which would have compared a spawn x against
		// a single foothold's span. A map with no usable foothold has null bounds
		// and every summon is rejected, which is the honest answer.
		FootholdPhysics.Foot? foot;
		int left;
		int right;
		lock (Gate)
		{
			foot = session.Map.FindBelow((int)body.X, (int)body.Y);
			var bounds = session.Map.Bounds;
			if (bounds is null)
			{
				result.Rejected.Add("map has no usable foothold, so there is nowhere to spawn");
				return result;
			}
			(left, _, right, _) = bounds.Value;
		}

		lock (Gate)
		{
			for (int i = 0; i < count; i++)
			{
				// MobForm.cs:116 -- Random.Next's upper bound is EXCLUSIVE, so this is
				// a half-open 200px window around the player, not 201 positions.
				// MobForm.cs:111 makes a `new Random()` per CLICK, which two clicks in the
				// same tick would seed identically. This session's seeded generator is the
				// same object combat already draws from, so a summon is reproducible from
				// the session id -- which matters because `StableSeed` is what every other
				// spawn in this class uses.
				int x = session.CombatRandom.Next((int)body.X - 100, (int)body.X + 100);
				// Hoisted out of the loop: the flag and the number are properties of
				// the mob, not of the copy, and asking the archive once per summon
				// would re-read the same node for every one of them.
				var (canTouch, touchDamage) = _combat.MobTouchAttack(mobId);
				bool firstAttack = _combat.MobFirstAttack(mobId);
				if (x <= left || x >= right)
				{
					result.Rejected.Add($"{mobId}: x={x} is outside [{left}, {right}]");
					continue;
				}
				if (!foot.HasValue)
				{
					result.Rejected.Add($"{mobId}: no foothold under the character to spawn on");
					break;
				}

				// Resolved BEFORE the patrol is built. Reading it after means the dictionary
				// is still empty at construction time and every summoned mob gets an
				// EMPTY attack list -- which looks exactly like "this mob has no
				// attacks" and would quietly disable the whole attack path.
				if (kind != "n" && !session.MobLevels.ContainsKey(mobId))
				{
					session.MobLevels[mobId] = _combat.MobLevel(mobId);
					session.MobNames[mobId] = _strings.GetMobName(int.TryParse(mobId, out int nameId) ? nameId : -1) ?? "";
					session.MobAttacks[mobId] = _combat.MobAttacks(mobId);
				}

				MobPatrol patrol = new MobPatrol(
					session.Map, foot.Value,
					StableSeed(session.Id + ":summon:" + i),
					0, 0, moveSpeed, moveType,
					x, SimTuning.MobHp,
					canTouchAttack: canTouch, touchDamage: touchDamage, firstAttack: firstAttack,
					canMove: canMove, noFlip: noFlip, notAttack: notAttack,
					attacks: session.MobAttacks[mobId]);
				int index = session.Mobs.Count;
				session.Mobs.Add((patrol, mobId, kind ?? "m", foot.Value.Layer ?? ""));
				session.SummonedIndexes.Add(index);
				result.Indexes.Add(index);
				result.Spawned++;
			}
		}
		return result;
	}

	/// <summary>Summon into a session by id, for the HTTP layer. Null when the id is unknown.</summary>
	public SummonResult? Summon(string sessionId, string mobId, int count, string? kind)
	{
		MapSimSession? session;
		lock (Gate)
		{
			if (!_sessions.TryGetValue(sessionId, out session))
			{
				return null;
			}
		}
		return Summon(session, mobId, count, kind);
	}

	/// <summary>Despawn by session id, for the HTTP layer. Null when the session is gone.</summary>
	public int? DespawnSummoned(string sessionId)
	{
		MapSimSession? session;
		lock (Gate)
		{
			if (!_sessions.TryGetValue(sessionId, out session))
			{
				return null;
			}
		}
		return DespawnSummoned(session);
	}

	/// <summary>
	/// Remove every summoned mob and leave the map's own spawns alone.
	/// </summary>
	/// <remarks>
	/// <c>MobForm.cs:126-140</c> kills every mob whose id is in <c>SummonedList</c>.
	/// Matching by id rather than by identity is the defect noted on
	/// <see cref="MapSimSession.SummonedIndexes"/>: on a map whose <c>life</c> names
	/// the same mob id, that button would also kill the map's own.
	/// </remarks>
	private int DespawnSummoned(MapSimSession session)
	{
		int removed = 0;
		lock (Gate)
		{
			foreach (int index in session.SummonedIndexes)
			{
				if (index < 0 || index >= session.Mobs.Count)
				{
					continue;
				}
				session.Mobs[index].Patrol.Kill();
				removed++;
			}
			session.SummonedIndexes.Clear();
		}
		return removed;
	}

	private void SpawnMobs(MapSimSession session)
	{
		MapDocument mapDocument;
		lock (Gate)
		{
			mapDocument = LoadMap(session.Path);
		}
		int num = StableSeed(session.Id);
		foreach (MapLife item2 in mapDocument.Life)
		{
			if (item2.Hide == 1)
			{
				continue;
			}
			string text = item2.Type ?? "";
			bool flag = string.Equals(text, "n", StringComparison.Ordinal);
			bool flag2 = string.Equals(text, "m", StringComparison.Ordinal);
			if (!flag && !flag2)
			{
				continue;
			}
			int num2 = (int)item2.X.GetValueOrDefault();
			int num3 = (int)(item2.Cy ?? item2.Y.GetValueOrDefault());
			FootholdPhysics.Foot? foot;
			lock (Gate)
			{
				foot = FindStart(session.Map, num2, num3);
				if (!foot.HasValue)
				{
					foot = session.Map.FindBelow(num2, num3);
				}
			}
			if (foot.HasValue)
			{
				string text2 = item2.Id ?? "";
				int patrolLeft = (int)item2.Rx0.GetValueOrDefault();
				int patrolRight = (int)item2.Rx1.GetValueOrDefault();
				lock (Gate)
				{
					// 2026-10-17: every mob spawns with `SimTuning.MobHp`, not the archive's
					// own ``info/maxHP`` and not the harmless-mob constant 8 -- so a test
					// reads the same number on every creature. One hit is `SimTuning.PlayerDamage`,
					// so ten hits kill anything.
					int maxHp = SimTuning.MobHp;
					// The archive's own contact-attack flag and damage, read once per
					// mob. Without this the mob's hit was `MaxHp / 8`, a number in
					// neither the reference nor the archive (43 where the archive
					// says 90), and mobs the archive marks as harmless hit anyway.
					var (canTouch, touchDamage) = flag
						? (false, 0)
						: _combat.MobTouchAttack(text2);
					bool firstAttack = !flag && _combat.MobFirstAttack(text2);

					// The animation set decides whether this creature walks at all.
					// Mob.cs:166-175 (`move` absent -> MoveSpeed 0 and Stand; `jump`
					// then `fly` override the type) and Mob.cpp:81-83 (canmove/canjump/
					// canfly read off the same three nodes). Hardcoding MoveType.Move
					// and a 2px speed here gave EVERY map mob a patrol the archive
					// only grants about 60% of them -- MEASURED over two disjoint
					// 158-mob slices of Mob.nx, the `move` node is on 89 and 101.
					float spawnSpeed = 2f;
					bool spawnCanMove = true;
					bool spawnNoFlip = false;
					bool spawnNotAttack = false;
					MobPatrol.MobMoveType spawnType = MobPatrol.MobMoveType.Move;
					if (!flag)
					{
						MobSpriteDto? art = MobAnimation.Sprites?.Describe(null, text2, "m");
						if (art is not null && !string.IsNullOrEmpty(art.ImagePath))
						{
							bool mMove = art.Actions.Any(a => a.Name == "move" && a.Frames.Count > 0);
							bool mJump = art.Actions.Any(a => a.Name == "jump" && a.Frames.Count > 0);
							bool mFly = art.Actions.Any(a => a.Name == "fly" && a.Frames.Count > 0);
							spawnCanMove = mMove || mFly;
							spawnSpeed = mMove ? _combat.MobSpeed(text2) : 0f;
							(spawnNoFlip, spawnNotAttack) = _combat.MobFlags(text2);
							spawnType = mFly
								? MobPatrol.MobMoveType.Fly
								: mJump ? MobPatrol.MobMoveType.Jump : MobPatrol.MobMoveType.Move;
						}
						else
						{
							// A `life` id no open archive holds. The reference has
							// already thrown by here (Mob.cs:147 resolves the node and
							// would NRE); this keeps the spawn visible instead of
							// inventing an animation set for it.
							spawnSpeed = 0f;
							spawnCanMove = false;
						}
					}

					// Same ordering rule as the summon path: resolve before the patrol is
					// built, or the dictionary is still empty at construction and every
					// map mob silently gets an empty attack list.
					if (!flag && !session.MobLevels.ContainsKey(text2))
					{
						session.MobLevels[text2] = _combat.MobLevel(text2);
						session.MobNames[text2] = _strings.GetMobName(int.TryParse(text2, out int mnid) ? mnid : -1) ?? "";
						session.MobAttacks[text2] = _combat.MobAttacks(text2);
					}

					MobPatrol item = (flag ? new MobPatrol(session.Map, foot.Value, num + session.Mobs.Count, 0, 0, 2f, MobPatrol.MobMoveType.Move, num2, maxHp)
					{
						Frozen = true
					} : new MobPatrol(session.Map, foot.Value, num + session.Mobs.Count, patrolLeft, patrolRight, spawnSpeed, spawnType, num2, maxHp,
						canTouchAttack: canTouch, touchDamage: touchDamage, firstAttack: firstAttack,
						canMove: spawnCanMove, noFlip: spawnNoFlip, notAttack: spawnNotAttack,
						attacks: session.MobAttacks[text2]));
					session.Mobs.Add((item, text2, text, foot.Value.Layer ?? ""));
					// The label, once, exactly where the reference reads it
					// (Npc.cs:171-186, inside Spawn).
					if (text == "n" && !session.NpcLabels.ContainsKey(text2))
						session.NpcLabels[text2] = _combat.NpcText(text2);
					// The lines, once, exactly where the reference reads them
					// (Npc.cpp:113-133, inside the constructor). The initial delay
					// is CHAT_MIN_DELAY + rand(CHAT_DELAY_SPREAD) — NOT zero, so a
					// row of NPCs does not talk in unison and a quest giver does
					// not speak before its marker is worked out 60 ticks in
					// (Npc.cpp:198-203).
					if (text == "n" && !session.NpcChats.ContainsKey(text2))
					{
						NpcChatDto chat = _chats?.Describe(text2) ?? new NpcChatDto();
						session.NpcChats[text2] = new MapSimSession.NpcChatState
						{
							Lines = [.. chat.General],
							States = chat.States.Count,
							DelayFrames = NpcChatService.ChatMinDelayMs / MsPerFrame +
								session.CombatRandom.Next(NpcChatService.ChatDelaySpreadMs / MsPerFrame + 1),
						};
					}
				}
			}
		}
	}

	private static int StableSeed(string id)
	{
		uint num = 2166136261u;
		foreach (char c in id)
		{
			num ^= c;
			num *= 16777619;
		}
		return (int)num;
	}

	public MapSimFrame Step(string id, MapSimInput input, bool manual = false)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession value))
		{
			return new MapSimFrame
			{
				Error = "no such session -- start one before stepping"
			};
		}
		long num = (value.LastInputMs = Environment.TickCount64);
		value.ManualUntilMs = num + 250;
		// W-089. A DETERMINISTIC lease, for a caller that says it is measuring.
		//
		// The 250 ms lease above is a guess: it assumes the caller comes back
		// within a quarter of a second. MEASURED 2026-10-18, that guess is wrong
		// for any caller that thinks for longer than that, and the symptom is not
		// a timeout -- the request came back in 2.1 ms -- but the server's own
		// free-running clock injecting frames in the gap:
		//
		//   no think time            0 / 60 requests got an injected frame
		//   200 ms think time        2 / 60 requests got one, +10 frames
		//
		// with the same latency distribution. So the window is the time the CLIENT
		// spends deciding what to send next, and a lease cannot fix that -- only a
		// longer lease pushes the probability further away without ever removing
		// it.
		//
		// `manual: true` therefore says "I am driving this session frame by frame"
		// and the background clock stops touching it entirely, until the idle
		// reaper takes the session away. It is OPT-IN: the browser never asks for
		// it, so the browser's behaviour is unchanged.
		if (manual)
			value.ManualMode = true;
		Advance(value, input);
		lock (Gate)
		{
			return Describe(value, value.Frame);
		}
	}

	public bool End(string id)
	{
		MapSimSession value;
		return _sessions.TryRemove(id, out value);
	}

	public MapSimFrame? Peek(string id)
	{
		if (!_sessions.TryGetValue(id, out MapSimSession value))
		{
			return null;
		}
		lock (Gate)
		{
			return Describe(value, value.Frame);
		}
	}

	private MapSimFrame Describe(MapSimSession session, int frame)
	{
		CharacterPhysics body = session.Body;
		int facing = session.Facing;
		string text = body.StandingOn?.Id.ToString() ?? "";
		// W-082b: the direction is the INPUT's, set in Advance before this frame's
		// combat resolves, so a cast is aimed on the same frame the key was pressed.
		// Deriving it here from the body's movement was both a frame late and wrong
		// whenever the body did not move. A knockback still moves the body without
		// changing which way the character is LOOKING, which is what the reference
		// does too -- PlayerStates only calls set_direction from key handlers.
		session.LastX = body.X;
		MapSimFrame mapSimFrame = new MapSimFrame
		{
			SessionId = session.Id,
			Frame = frame,
			X = body.X,
			Y = body.Y,
			Speed = body.Speed,
			VelocityY = body.VelocityY,
			Facing = facing,
			OnGround = body.OnGround,
			OnLadder = body.OnLadder.HasValue,
			Foothold = text,
			// A running cast OVERRIDES whatever the physics said the body was doing.
			// MapleCharacter.cs:1472,1482,1509,1513,1522,1642 all gate walk, jump,
			// prone and the ordinary attack on `!Skill.PlayEnded`, so while a skill is
			// playing the body is playing the skill's pose and nothing else. Reporting
			// `body.LastAction` here would say `walk` under a swing animation.
			Action = session.SkillPoseTicks > 0 ? "skill" : body.LastAction,
			AttackStance = body.AttackStance,
			SkillStance = session.SkillStance,
			SkillId = session.SkillId,
			SkillCastSeq = session.SkillCastSeq,
			SkillEffects = session.SkillEffects,
			// W-088. The caster-side ground decals, published for the whole
			// animation rather than the one frame they were spawned on, for the
			// reason HitEffects are (see MapSimSession.TileEffectUntil): this client
			// polls every ~16 ms and the server advances up to 8 frames per poll, so
			// a one-frame publication is consumed inside a single poll and the
			// canvas draws none of it.
			TileEffects = session.Frame <= session.TileEffectUntil
				? session.PendingTileEffects
				: new List<MapSimTileEffect>(),
			FootholdChanged = (text != session.LastFoothold),
			FellOut = (body.FellOutOfMap ? "fell past the bottom of the map with no foothold below" : null),
			Bounds = DescribeBounds(session),
			Status = session.Status,
			ExpGained = session.PendingExp,
			LevelsGained = session.PendingLevels,
			PlayerDamage = session.PendingPlayerDamage,
			PlayerDead = session.Status.Dead
		};
		FootholdPhysics.Portal? currentPortal = session.CurrentPortal;
		object portal;
		if (currentPortal.HasValue)
		{
			FootholdPhysics.Portal valueOrDefault = currentPortal.GetValueOrDefault();
			portal = new PortalDto(valueOrDefault.Name, valueOrDefault.ToName, valueOrDefault.ToMap, valueOrDefault.Type);
		}
		else
		{
			portal = null;
		}
		mapSimFrame.Portal = (PortalDto?)portal;
		mapSimFrame.Transfer = session.PendingTransfer;
		MapSimFrame mapSimFrame2 = mapSimFrame;
		session.PendingTransfer = null;
		session.PendingPlayerDamage = 0;
		session.PendingExp = 0L;
		session.PendingLevels = 0;
		session.LastFoothold = text;
		mapSimFrame2.Mobs = new List<MapSimMobFrame>(session.Mobs.Count);
		for (int i = 0; i < session.Mobs.Count; i++)
		{
			var (mobPatrol, mobId, kind, footholdLayer) = session.Mobs[i];
			if (!mobPatrol.Dead)
			{
				MobAnimation mobAnimation = MobAnimation.For(session, i, kind, mobPatrol);
				var entry = kind == "m" ? mobPatrol.AttackEntries : default;
				mapSimFrame2.Mobs.Add(new MapSimMobFrame
				{
					MobId = mobId,
					X = mobPatrol.X,
					Y = mobPatrol.Y,
					Foothold = mobPatrol.Foothold.Id,
					FootholdLayer = footholdLayer,
					Kind = kind,
					Facing = mobPatrol.Facing,
					Direction = mobPatrol.Direction,
					OnGround = mobPatrol.OnGround,
					Reversals = mobPatrol.Reversals,
					Dead = mobPatrol.Dead,
					Hp = mobPatrol.Hp,
					MaxHp = mobPatrol.MaxHp,
					Dying = mobPatrol.Dying,
					Stance = mobAnimation.Stance,
					FrameIndex = mobAnimation.FrameIndex,
					FrameCount = mobAnimation.FrameCount,
					Stun = mobPatrol.Stun,
					SpritePath = mobAnimation.SpritePath,
					// The NPC label, carried on the mob's own entry so the client does
					// not have to know which of them are NPCs. Empty for a mob: the
					// reference only ever builds an NpcText for an NPC (Npc.cs:43, :51).
					NpcName = kind == "n" && session.NpcLabels.TryGetValue(mobId, out var npc) ? npc.Name : "",
					NpcFunc = kind == "n" && session.NpcLabels.TryGetValue(mobId, out var npc2) ? npc2.Func : "",
					NpcHideName = kind == "n" && session.NpcLabels.TryGetValue(mobId, out var npc3) && npc3.HideName,
					Speech = kind == "n" && session.NpcChats.TryGetValue(mobId, out var chat) ? chat.Showing : "",
					SpeechFrames = kind == "n" && session.NpcChats.TryGetValue(mobId, out var chat2) ? chat2.ShowingFrames : 0,
					KnockbackPx = kind == "m" ? mobPatrol.LastKnockbackPx : 0,
					KnockbackFacing = kind == "m" ? mobPatrol.LastKnockbackFacing : 0,
					AttackEntryCount = kind == "m" ? entry.Count : 0,
					AttackEntryX = kind == "m" ? entry.X : 0,
					AttackEntryY = kind == "m" ? entry.Y : 0,
					AttackEntryElapsedMs = kind == "m" ? entry.ElapsedMs : 0,
					AttackEntryDelayMs = kind == "m" ? entry.DelayMs : 0,
					AttackEntryLifetimeMs = kind == "m" ? entry.LifetimeMs : 0,
					AttackEntryType = kind == "m" ? entry.Type : 0,
					AttackEntryBallAction = kind == "m" ? entry.BallAction : "",
					AttackEntryBallFrameCount = kind == "m" ? entry.BallFrameCount : 0,
					AttackEntryBallFrameIndex = kind == "m" ? entry.BallFrameIndex : 0,
					AttackEntryFlipped = kind == "m" && entry.Flipped,
				// W-070: every entry, not just the first. The scalars above stay for
				// the single-entry path the viewer and the suites already use.
				AttackEntries = kind == "m"
					? mobPatrol.EveryEntry.Select(e => new MapSimAttackEntry
					{
						X = e.X,
						Y = e.Y,
						ElapsedMs = e.ElapsedMs,
						DelayMs = e.DelayMs,
						LifetimeMs = e.LifetimeMs,
						Type = e.Type,
						BallAction = e.BallAction,
						BallFrameCount = e.BallFrameCount,
						BallFrameIndex = e.BallFrameIndex,
						Flipped = e.Flipped,
					}).ToList()
					: new List<MapSimAttackEntry>(),
					// The effects this cast put on THIS mob. Keyed by the loop's own
					// index, not by mob id, for the reason PendingHitEffects is:
					// a map can name the same id twice and an id-keyed map would
					// hand every struck mob's flash to whichever one comes last.
					HitEffects = session.PendingHitEffects.TryGetValue(i, out List<MapSimMobHitEffect>? hitFx)
						&& session.HitEffectUntil.TryGetValue(i, out int until) && session.Frame <= until
						? hitFx
						: new List<MapSimMobHitEffect>(),
					Level = kind == "n" ? 0 : session.MobLevels.TryGetValue(mobId, out int lv) ? lv : 0,
					MobName = kind == "n" ? "" : session.MobNames.TryGetValue(mobId, out string? mn) ? mn ?? "" : ""
				});
			}
		}
		if (session.PendingDamage.Count > 0)
		{
			mapSimFrame2.Damage = new List<MapSimDamageNumber>(session.PendingDamage);
			session.PendingDamage.Clear();
		}
		// The hit effects are retired by their OWN animation length, not by the
		// frame they arrived on: the client polls the server faster than the server
		// answers, so a one-frame publication is never drawn. See W-084.
		foreach (int index in session.HitEffectUntil.Keys.ToList())
		{
			if (session.Frame > session.HitEffectUntil[index])
			{
				session.HitEffectUntil.Remove(index);
				session.PendingHitEffects.Remove(index);
			}
		}
		// The caster-side ground decals are retired the same way and on the same
		// clock. Clearing the list is what stops it growing for the life of the
		// session: a cast lays down up to a few hundred entries and a walk that
		// casts a few hundred times would otherwise hold all of them.
		if (session.Frame > session.TileEffectUntil)
			session.PendingTileEffects.Clear();
		return mapSimFrame2;
	}

	private static MapSimBounds? DescribeBounds(MapSimSession session)
	{
		(int, int, int, int)? bounds = session.Map.Bounds;
		if (bounds.HasValue)
		{
			(int, int, int, int) valueOrDefault = bounds.GetValueOrDefault();
			return new MapSimBounds
			{
				Left = valueOrDefault.Item1,
				Top = valueOrDefault.Item2,
				Right = valueOrDefault.Item3,
				Bottom = valueOrDefault.Item4,
				ViewportHeight = 600
			};
		}
		return null;
	}

	private void Sweep(long now)
	{
		foreach (KeyValuePair<string, MapSimSession> session in _sessions)
		{
			if (now - session.Value.LastInputMs > 120000)
			{
				_sessions.TryRemove(session.Key, out MapSimSession _);
			}
		}
	}
}
