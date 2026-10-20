using System.Globalization;
using MapleBench.Services.Look;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

// NOTE ON CITATIONS. The `.cs`/`.js` citations below name MapleNecrocer
// (C:\MUWORK\GAME\_necrocer_src\MapleNecrocer) -- this port's own source,
// which has no counterpart in the authoritative reference: clean-client
// (C:\MUWORK\GAME\MPSH\clean-client\src) contains zero `.cs` files. Where a
// MapleNecrocer citation disagrees with clean-client, clean-client wins -- see
// pm/PARITY.md, "The C++ wins". These were not re-audited for content; some
// record a port's deliberate divergence.

namespace MapleBench.Services.Simulate;

/// <summary>
/// One moment in a skill's cast, in frames from the cast's first tick.
/// </summary>
public sealed record SkillCastEvent(
    int Tick,
    string Kind,
    int Index,
    string Detail)
{
    public override string ToString() => $"t{Tick,3} {Kind}#{Index} {Detail}";
}

/// <summary>One cast sprite the reference would spawn, with the path it draws from.</summary>
/// <param name="Node">The archive node it came from -- <c>effect</c>, <c>ball</c>, ??/param>
/// <param name="Frames">How many frames it carries.</param>
/// <param name="ImagePath">The first frame, which is what the reference draws.</param>
/// <param name="Bullet">Whether this node is the <c>ball</c>, the one that travels.</param>
/// <param name="ContainerPath">
/// The sprite's own container, one level above <paramref name="ImagePath"/>.
/// </param>
/// <remarks>
/// The container is carried because the frame duration lives THERE and not on any
/// frame: <c>AnimationService.cs:258-263</c> established that against the bytes
/// (<c>stand1/0/delay</c> = 500 with no <c>delay</c> under <c>stand1/0/body</c>), and
/// the reference reads it from the same level (<c>MapleCharacter.cs:395</c>,
/// <c>Iter2.GetInt("delay")</c>). Reading it per-frame yields the reader's 100ms
/// fallback instead of the archive's timing ??measured on 1121008's effect, 900ms
/// that way against the archive's 1080ms.
/// </remarks>
public sealed record SkillCastSprite(string Node, int Frames, string ImagePath, bool Bullet, string ContainerPath,
    /// <summary>
    /// Each frame's own <c>origin</c>, in the same order as the frames the client
    /// walks. Never null: a frame with no origin contributes (0,0).
    /// </summary>
    /// <remarks>
    /// W-075. MEASURED on 1121008:
    /// <code>
    ///   effect/0/0  canvas 66 x 37   origin 30, 23
    ///   effect/0/1  canvas 142 x 95  origin 49, 81
    /// </code>
    /// They differ per frame, so one number for the container would be wrong on
    /// every frame but the first. The client was drawing with an invented
    /// <c>- w/2, - h*0.72</c> around the character's FEET, which is why the user
    /// saw the effect start at the boots instead of at the character.
    /// </remarks>
    IReadOnlyList<PointDto> Origins,
    /// <summary>
    /// Each frame's own <c>delay</c>, in ms, 100 where the archive carries none.
    /// </summary>
    /// <remarks>
    /// W-076. <c>Animation.cpp:64-67</c>: <c>delay = src["delay"]; if (delay == 0)
    /// delay = 100;</c>. MEASURED on 1121008: all nine frames are 120, so the
    /// animation is 1080 ms of sprite time. The client was advancing on a
    /// hardcoded 90 ms per frame, which plays it at 75% speed and wraps on
    /// <c>% n</c> rather than ending.
    /// </remarks>
    IReadOnlyList<int> Delays,
    /// <summary>
    /// The effect's <c>z</c>: negative draws it BEHIND the character, 0 and up
    /// in front. 0 when the archive carries none.
    /// </summary>
    /// <remarks>
    /// W-076. <c>SkillUseEffect.h:50</c> reads <c>z = src["z"]</c>; the effects
    /// go into <c>effects[z]</c>; <c>EffectLayer.cpp:24</c> draws everything up to
    /// and including index -1 in <c>drawbelow</c>, and <c>Char.cpp:95</c> calls
    /// that BEFORE the avatar while <c>:240</c> calls <c>drawabove</c> after. We
    /// painted every effect in one pass after the avatar, so any effect authored
    /// with a negative z would cover the character. 1121008 carries no z at all,
    /// so it is a 0 and draws in front -- which is what it already did.
    /// </remarks>
    int Z,
    /// <summary>
    /// The caster's real attack speed, the factor the sprite clock runs at.
    /// </summary>
    /// <remarks>
    /// W-076. <c>Char.cpp:607-612</c> <c>get_real_attackspeed()</c> is
    /// <c>1.7f - speed/10</c>, and <c>EffectLayer.h:59-61</c> advances
    /// <c>TIMESTEP * speed</c> per tick, so the sprite runs at <c>speed</c> times
    /// real time and a frame lasts <c>delay / speed</c> ms. With delay 120 and
    /// speed 1.7 that is 70.6 ms per frame, not the 90 this client invented.
    /// </remarks>
    float Speed)
{
    /// <summary>
    /// Which cast this sprite belongs to, stamped on the PUBLISHED copy.
    /// </summary>
    /// <remarks>
    /// W-094. The client's effect clock used to be keyed on
    /// <c>live.state.skillCastSeq</c> -- the SEQUENCE NUMBER OF THE LAST FRAME
    /// THE SERVER SENT. That number arrives one frame LATE: the frame that
    /// carries the new cast also carries the new sequence, but until it lands
    /// the client is still holding the old one, so the first paint of a new cast
    /// ran the PREVIOUS cast's clock -- already at its last frame.
    /// <para>
    /// MEASURED 2026-10-18: a cast's effect frames came out
    /// <c>[8, 0, 0, 0, 0, 1, 1, ...]</c> -- frame 8 of nine, then frame 0. The
    /// first thing a person sees is the tail of the last effect, which reads as a
    /// pop, and it is what "抖動" looks like.
    /// </para>
    /// <para>
    /// The sprite carries its own cast, so the clock is keyed on the thing being
    /// drawn rather than on a counter that trails it.
    /// </para>
    /// </remarks>
    public int CastSeq { get; init; }
}

/// <summary>One image of the effect drawn on whatever the skill hit.</summary>
/// <param name="Group">Which <c>hit/&lt;group&gt;</c> it came from.</param>
/// <param name="Index">Which frame of that group.</param>
/// <param name="ImagePath">The frame itself, <c>hit/&lt;group&gt;/&lt;index&gt;</c>.</param>
/// <param name="ContainerPath">
/// The frames' own container, one level above <paramref name="ImagePath"/>: the
/// node the <c>pos</c>/<c>z</c> nodes sit beside.
/// </param>
/// <param name="Frames">How many frames the container holds.</param>
/// <param name="Origins">
/// Each frame's own <c>origin</c>, in the order the client walks them. Never null:
/// a frame with no origin contributes (0,0).
/// </param>
/// <param name="Delays">Each frame's own <c>delay</c>, in ms, 100 where absent.</param>
/// <param name="Pos">
/// The <c>pos</c> node, which says WHERE on the mob the effect hangs. TWO values,
/// not five ??see the remarks.
/// </param>
/// <param name="Z">
/// The <c>z</c> node: negative draws BEHIND the mob, 0 and up in front.
/// </param>
/// <remarks>
/// <para>
/// <c>SkillHitEffect.h:37-45</c> reads <c>pos</c> and <c>z</c> off the EFFECT node
/// ??the same node the frames hang from, which for <c>SingleHitEffect</c> is
/// <c>src["hit"]["0"]</c> (<c>SkillHitEffect.cpp:24</c>) ??and both are used by
/// <c>Mob::show_effect</c> (<c>Mob.cpp:1536-1559</c>): <c>pos</c> picks the shift,
/// <c>z</c> picks which half of the mob's effect layer draws it.
/// </para>
/// <para>
/// <b>W-089 (C2): <c>pos</c> is a TWO-valued field in this reference, not a
/// five-valued one.</b> <c>Mob.cpp:1543-1556</c>, transcribed whole:
/// </para>
/// <code>
/// switch (pos)
/// {
/// case 0: shift = get_head_position(Point&lt;int16_t&gt;()); break;
/// case 1: break;
/// case 2: break;
/// case 3: break;
/// case 4: break;
/// }
/// effects.add(animation, DrawArgument(shift, f), z);
/// </code>
/// <para>
/// Only <c>case 0</c> assigns anything. <c>shift</c> is a default-constructed
/// <c>Point&lt;int16_t&gt;</c> = (0,0), so <c>case 1</c>..<c>case 4</c> are four
/// spellings of "no shift" ??and so is any value the switch does not name at all. A
/// hit effect therefore hangs either at the mob's HEAD (<c>get_head_position</c>
/// adds the mob animation's own head vector and mirrors its X by
/// <c>(flip &amp;&amp; !noflip)</c>, <c>Mob.cpp:1460-1468</c>) or at the mob's ORIGIN,
/// its feet.
/// </para>
/// <para>
/// The ground decal confirms it rather than contradicting it.
/// <c>SkillUseEffect.cpp:203-212</c> calls
/// <c>target.show_effect(tile_animations[variant], 1, 4, false);</c> with the comment
/// "pos=1 is the mob's foot anchor". <c>1</c> is not a special foot value; it is the
/// first of the four cases that does nothing, and 2, 3 and 4 would draw the identical
/// decal. The choice of <c>1</c> there NAMES the fall-through; it is not a distinct
/// anchor.
/// </para>
/// <para>
/// Which means <b>this reader needs no branch</b>: it passes the archive's own number
/// through, and the reference collapses 1..4 itself. 0 stays the head, which is also
/// what an ABSENT <c>pos</c> means ??<c>SkillHitEffect.h:43</c> assigns an
/// <c>int8_t</c> from an <c>nl::node</c> that yields 0 for a name it does not have,
/// and <c>Mob.cpp:2242</c> passes a literal 0 on its own. MEASURED 2026-10-18 across
/// the 66 plans: pos 0 on 409 sprites, 1 on 99, 2 on 48, 3 on 78, 4 on 3 (5121005's)
/// ??and all four of the last land at the feet, per the switch above.
/// </para>
/// <para>
/// MEASURED 2026-10-17 over the open Skill.nx (<c>GET /api/children</c> on every
/// <c>skill/&lt;id&gt;/hit</c>): of <b>239</b> hit groups, <b>53</b> carry a
/// group-level <c>pos</c> (values 1..4) and <b>0</b> carry a group-level
/// <c>z</c> ??while <b>110</b> individual FRAMES carry a <c>z</c>. So the two
/// nodes really do live at different depths in this archive, and a reader that
/// looked for both in one place would find one and silently drop the other. They
/// are read where each is written: <c>pos</c> off the group, <c>z</c> off the
/// first frame that carries one (all but two groups agree across their frames;
/// where they disagree the FIRST is used and see the reader).
/// </para>
/// <para>
/// <c>z</c> is the value on the group when the group carries none, and 0 when
/// nothing does ??never an invented one. 54 of the frames read <c>z = 0</c> and
/// one reads <c>1</c>, so the default is both the common case and the only
/// in-front value this archive actually uses.
/// </para>
/// </remarks>
public sealed record SkillHitSprite(
    int Group, int Index, string ImagePath, string ContainerPath,
    int Frames,
    IReadOnlyList<PointDto> Origins,
    IReadOnlyList<int> Delays,
    int Pos,
    int Z);

/// <summary>
/// One ground decal variant of <c>skill/&lt;id&gt;/tile/&lt;n&gt;</c>.
/// </summary>
/// <param name="Variant">
/// The variant's own index, which is also its <c>tile/&lt;n&gt;</c> node name.
/// </param>
/// <param name="ImagePath">The animation's first frame, <c>tile/&lt;n&gt;/0</c>.</param>
/// <param name="ContainerPath">
/// The variant node itself ??one level above <paramref name="ImagePath"/>. The
/// client builds every frame URL from this, exactly as it does for a hit sprite.
/// </param>
/// <param name="Frames">How many frames the variant holds.</param>
/// <param name="Origins">Each frame's own <c>origin</c>; (0,0) where absent.</param>
/// <param name="Delays">Each frame's own <c>delay</c> in ms, 100 where absent.</param>
/// <remarks>
/// <para>
/// <c>SkillUseEffect.cpp:168-173</c> builds one <c>Animation</c> per variant ??
/// <c>tile_animations.emplace_back(variant)</c> ??and every frame of that
/// animation is spawned by <c>Char::show_attack_effect_at</c>
/// (<c>SkillUseEffect.cpp:257</c>). So a variant is an ANIMATION, not a still:
/// it carries <c>origins</c> and <c>delays</c> exactly like
/// <see cref="SkillHitSprite"/>, which is what this record is modelled on.
/// </para>
/// <para>
/// Before this port the record held only <c>(Variant, ImagePath)</c>, which is
/// why no tile could ever be drawn: there was no frame count to walk, no
/// origin to place the rectangle with and no delay to advance on.
/// </para>
/// </remarks>
public sealed record SkillTileSprite(
    int Variant, string ImagePath, string ContainerPath,
    int Frames,
    IReadOnlyList<PointDto> Origins,
    IReadOnlyList<int> Delays);

/// <summary>
/// One row of the reference's castable-skill grid: id, the book it lives in,
/// its icon and its localised name.
/// </summary>
/// <remarks>
/// <c>SkillForm.cs:111</c> and <c>:132</c> add exactly these four cells, in this
/// order, and both name the icon <c>Skill/&lt;GetJobImg&gt;.img/skill/&lt;id&gt;/icon</c>
/// and the name <c>String/Skill.img/&lt;id&gt;/name</c>. Icon and name are both
/// optional in practice: the reference reads them without a presence check
/// (:107-110) and lets a missing node render as nothing, so an empty string here
/// means "absent", not "broken".
/// </remarks>
public sealed record SkillListEntry(string SkillId, string BookId, string IconPath, string Name,
    string Action = "");

/// <summary>
/// Everything the archive says about casting one skill, plus the timeline the
/// reference derives from it.
/// </summary>
public sealed record SkillCastPlan(
    string SkillId,
    string BookId,
    string EntryPath,
    IReadOnlyList<string> Actions,
    IReadOnlyList<SkillCastSprite> Effects,
    IReadOnlyList<SkillHitSprite> HitEffects,
    IReadOnlyList<SkillTileSprite> Tiles,
    /// <summary>
    /// How many variants <c>rand() % tile_animations.size()</c> can pick ??
    /// that is, <see cref="Tiles"/>' own count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>W-088. This used to be <c>variant - 1</c>, which was a transcription of
    /// the WRONG call site.</b> The bound that spawns a tile is
    /// <c>SkillUseEffect.cpp:254</c>:
    /// </para>
    /// <code>size_t anim_idx = static_cast&lt;size_t&gt;(rand()) % tile_animations.size();</code>
    /// <para>
    /// <c>%size()</c> is INCLUSIVE of the last entry, so every variant is
    /// reachable. <c>Skill.cs:284-305</c> (MapleNecrocer, the program this
    /// service was originally ported from) is the other form and does use
    /// <c>Random.Next(0, tileCount)</c>, whose upper bound is exclusive ??which
    /// is where the <c>- 1</c> came from, and it silently dropped the last
    /// decal of every skill. 3111003 read 4 variants but could only ever draw
    /// the first 3.
    /// </para>
    /// <para>
    /// Measured on the open archive, the old count also counted
    /// <c>effectDistance</c> as a variant: the tile node's children are
    /// <c>0..n-1</c> plus a scalar <c>effectDistance</c>, and walking them all
    /// gave 3111003 a "fourth variant" whose <c>tile/effectDistance/0</c> is not
    /// a canvas at all.
    /// </para>
    /// </remarks>
    int TileCount,
    int? Ltx,
    int? Lty,
    int? Rbx,
    int? Rby,
    int DamageWaitTime,
    int CollisionCount,
    int TotalTime,
    /// <summary>
    /// W-084. How many mobs one cast hits, from the skill's own <c>mobCount</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reference's number and it is a DIFFERENT axis from
    /// <see cref="HitCount"/>. They were conflated for a while: see
    /// <see cref="MultiStrike"/>'s history in the service remarks.
    /// </para>
    /// <para>
    /// <c>Skill.cpp:394</c>   <c>attack.mobcount = stats.mobcount;</c>
    /// <c>SkillData.cpp:69</c> <c>uint8_t mobcount = (uint8_t)sub["mobCount"].get_integer(1);</c>
    /// <c>RegularAttack.cpp:33</c> <c>attack.mobcount = 1;</c>
    /// <c>Combat.cpp:1044</c>   Slash Blast is special-cased to 6.
    /// </para>
    /// <para>
    /// So every skill hits <i>one</i> mob unless its archive says otherwise.
    /// </para>
    /// </remarks>
    int MobCount,
    /// <summary>
    /// W-089. How many TIMES one mob is struck, from the skill's own
    /// <c>attackCount</c> (or <c>bulletCount</c> when the skill throws a projectile).
    /// Default 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the field that used to be called <c>MultiStrike</c>, and the
    /// reference has no <c>MultiStrike</c> at all.</b> What it does have is two
    /// independent counters, read off the same level node:
    /// </para>
    /// <code>
    /// SkillData.cpp:68   uint8_t attackcount = (uint8_t)sub["attackCount"].get_integer(1);
    /// SkillData.cpp:69   uint8_t mobcount    = (uint8_t)sub["mobCount"].get_integer(1);
    /// SkillData.cpp:70   uint8_t bulletcount = (uint8_t)sub["bulletCount"].get_integer(1);
    /// Skill.cpp:394      attack.mobcount = stats.mobcount;
    /// Skill.cpp:397-405  switch (attack.type) {
    ///                     case Attack::RANGED: attack.hitcount = stats.bulletcount; break;
    ///                     default:            attack.hitcount = stats.attackcount; break; }
    /// </code>
    /// <para>
    /// "How many mobs" (<see cref="MobCount"/>) and "how many times each" (this) are
    /// different questions. Reading one as the other is what made the old bool claim
    /// that 56 of the 66 castable skills hit more than once when only 9 of them carry
    /// an <c>attackCount</c> above 1 at all.
    /// </para>
    /// <para>
    /// MEASURED 2026-10-18 over the 66: <c>attackCount</c> is present on <b>10</b>
    /// (values 2, 4, 8, 10 -- all <c>Long</c>), and <c>bulletCount</c> on <b>1</b>
    /// (<c>13101005</c>, value 0). So this reads 1 for the other 55.
    /// </para>
    /// <para>
    /// The ranged branch is chosen from <see cref="Projectile"/>, which is the
    /// archive-driven half of the reference's test; the other half
    /// (<c>SkillData::is_ranged()</c>, a ~150-entry literal id table,
    /// <c>SkillData.cpp:139-333</c>) is deliberately NOT transcribed -- see the
    /// service remarks.
    /// </para>
    /// </remarks>
    int HitCount,
    /// <summary>
    /// W-089. Whether the skill carries a <c>ball</c> node -- the reference's
    /// <c>projectile</c> flag, and the branch that picks
    /// <see cref="HitCount"/>'s source node.
    /// </summary>
    /// <remarks>
    /// <c>Skill.cpp:201-216</c>:
    /// <code>
    /// bool hasball      = src["ball"].size() &gt; 0;
    /// bool bylevelball  = src["level"]["1"]["ball"].size() &gt; 0;
    /// if (bylevelball)      bullet = BySkillLevelBullet(...);
    /// else if (hasball)     bullet = SingleBullet(src);
    /// else { bullet = RegularBullet(); projectile = false; }
    /// </code>
    /// and <c>Skill.cpp:373</c> feeds it into the attack type:
    /// <c>if (projectile || SkillData::get(skillid).is_ranged()) RANGED;</c>.
    /// MEASURED 2026-10-18: 5 of the 66 carry <c>ball</c> (12111006, 2121003,
    /// 2221003, 3111003, 5221008) and none carries <c>level/&lt;n&gt;/ball</c>.
    /// </remarks>
    bool Projectile,
    /// <summary>
    /// W-084. The reach multiplier, the skill's <c>range</c> divided by 100.
    /// </summary>
    /// <remarks>
    /// <c>SkillData.cpp:77</c> <c>float hrange = (float)sub["range"].get_real(100.0) / 100;</c>
    /// -- a node named <c>range</c>, not the box. <c>Combat.cpp:660</c> applies it to
    /// the near edge only: <c>int16_t hrange = range.left() * attack.hrange;</c>, so
    /// 1.0 leaves the reach alone and a smaller value pulls the near edge in.
    /// Default 1.0 when the skill has no such node. MEASURED 2026-10-18: present on
    /// 2 of the 66 (<c>13101005</c>, <c>4121008</c>), both <c>Long</c>; 4121008's
    /// 150 gives the one non-1.0 <see cref="HRange"/> in the set.
    /// </remarks>
    double HRange,
    int MaxLevel,
    IReadOnlyList<SkillCastEvent> Timeline,
    /// <summary>
    /// How long the longest effect sprite plays, in milliseconds; 0 when the skill
    /// carries no effect sprite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cast lock is built on THIS, not on <see cref="TotalTime"/>, and they are
    /// nowhere near each other on this archive. <c>Skill.cs:410</c> ends the cast
    /// with <c>Skill.PlayEnded = AnimEnd;</c>, set by the <c>SkillSprite</c> from its
    /// own animation end, and that flag is what refuses another cast
    /// (<c>Skill.cs:321</c>) and movement
    /// (<c>MapleCharacter.cs:1472,1482,1509,1513,1522,1642</c>).
    /// <c>TotalTime</c> (<c>Skill.cs:187</c>) is how long the collision objects live.
    /// </para>
    /// <para>
    /// Measured over twelve castable skills: eleven have effect animations of
    /// 60-450ms while their <c>TotalTime</c> is 57 ticks (950ms); only
    /// <c>1121008</c> is close (1080ms against 67 ticks). Locking on
    /// <c>TotalTime</c> therefore held the character still roughly SIXTEEN times
    /// longer than the reference for most skills -- and 1121008 is precisely the one
    /// skill a single-sample spot check happens to land on.
    /// </para>
    /// <para>
    /// The LONGEST effect, not the shortest: the reference assigns
    /// <c>PlayEnded</c> from every SkillSprite in turn and so is order-dependent,
    /// but the longest is the only reading under which a sprite that is still
    /// playing keeps the character locked, which is the flag's entire job.
    /// </para>
    /// </remarks>
    int EffectMs = 0,
    /// <summary>
    /// The skill level the plan was resolved at, and that level's own
    /// <c>damage</c> node ??null when the archive carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// W-061. Measured 2026-10-16 through <c>/api/inspect</c> over the whole
    /// Skill.nx: <b>175 of 535</b> skill entries carry <c>level/1/damage</c>, and
    /// <b>0</b> carry no <c>level</c> node at all. Values over the 175: min 1,
    /// p25 72, median 104, p90 265, max 1210.
    /// </para>
    /// <para>
    /// This is the attacker's side of the roll, and it is the ONLY number here
    /// with no reference behind it ??see the service remarks. Null means absent,
    /// never zero (R-5): 360 skills carry no damage node and most of those are
    /// buffs, which deal none.
    /// </para>
    /// </remarks>
    int? SkillLevel = null,
    int? Damage = null,
    /// <summary>
    /// Which of <see cref="SkillCastService.DamageNodes"/> <see cref="Damage"/>
    /// came from — <c>damage</c>, <c>mad</c> or <c>fixdamage</c>. Empty when the
    /// level carries none of them.
    /// </summary>
    /// <remarks>
    /// Reported so the reason a skill does or does not hit is answerable without a
    /// debugger: a plan showing <c>damage: 430, damageNode: "mad"</c> is a
    /// different situation from <c>damage: 430, damageNode: "damage"</c>, and both
    /// are different from no node at all.
    /// </remarks>
    string DamageNode = "",
    /// <summary>
    /// W-089. Which node <see cref="HitCount"/> came from -- <c>attackCount</c>,
    /// <c>bulletCount</c>, or empty when neither exists and the reference's own
    /// default of 1 was used.
    /// </summary>
    /// <remarks>
    /// Carried for the same reason <see cref="DamageNode"/> is: a plan that reports
    /// <c>hitCount: 1, hitCountNode: ""</c> says "the archive is silent" and a plan
    /// that reports <c>hitCount: 1, hitCountNode: "attackCount"</c> says "the archive
    /// said one", and those are different situations.
    /// </remarks>
    string HitCountNode = "",
    /// <summary>
    /// W-089. The skill's <c>affected</c> animation -- the one played on the
    /// character AFTER the cast, not during it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Skill.cpp:107-110</c>:
    /// </para>
    /// <code>
    /// // CUser::ShowSkillAffected plays the skill's 'affected' animation.
    /// if (src["affected"]["0"].data_type() == nl::node::type::bitmap)
    ///     affectedeffect = std::make_unique&lt;SingleUseEffectPrepare&gt;(src, "affected");
    /// else
    ///     affectedeffect = std::make_unique&lt;NoUseEffect&gt;();
    /// </code>
    /// <para>
    /// It is kept OUT of <see cref="Effects"/> on purpose. The reference draws it from
    /// <c>Skill::apply_affectedeffects</c> (<c>Skill.cpp:276-281</c>), which runs after
    /// the hit, while <see cref="EffectMs"/> -- the cast lock -- is built from the
    /// casting sprites only. Folding it in would make a 7-frame heal animation hold
    /// the character still for its whole length, which no reference does.
    /// </para>
    /// <para>
    /// MEASURED 2026-10-18: exactly one of the 66 carries it --
    /// <c>2301002</c> (&#x7FA4;&#x4F53;&#x6CBB;&#x6108;, Holy Heal) with seven frames.
    /// </para>
    /// </remarks>
    IReadOnlyList<SkillCastSprite>? Affected = null,
    /// <summary>
    /// Which <c>hit</c> groups the reference can actually draw, and which one it
    /// picks. Diagnostic; <see cref="HitEffects"/> still carries every group the
    /// archive holds, because that is the image inventory a client needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Skill.cpp:113-156</c> chooses ONE class and each class reads at most two
    /// groups:
    /// </para>
    /// <list type="bullet">
    /// <item><c>5101003</c> &rarr; <c>ByHitHitEffect</c>, <c>hit/0</c> or <c>hit/1</c> by damage line;</item>
    /// <item><c>3101005</c> &rarr; <c>FirstTargetHitEffect</c>, <c>hit/0</c> for the first target and <c>hit/1</c> for splash;</item>
    /// <item><c>4211004</c> &rarr; <c>RandomHitEffect</c>, one of <c>hit/0..N</c> at random;</item>
    /// <item><c>CharLevel/10/hit</c> non-empty &rarr; <c>ByLevelHitEffect</c> / <c>ByLevelTwoHHitEffect</c>;</item>
    /// <item><c>level/1/hit</c> non-empty &rarr; <c>BySkillLevelHitEffect</c>;</item>
    /// <item><c>hit/0</c> and <c>hit/1</c> &rarr; <c>TwoHandedHitEffect</c>, by weapon;</item>
    /// <item><c>hit/0</c> &rarr; <c>SingleHitEffect</c>;</item>
    /// <item>none &rarr; <c>NoHitEffect</c>.</item>
    /// </list>
    /// <para>
    /// MEASURED 2026-10-18 over the 66: 60 skills resolve to <c>SingleHitEffect</c>,
    /// 3 to <c>TwoHandedHitEffect</c> (15101003, 5101004, 4221007), 1 to
    /// <c>RandomHitEffect</c> (4211004, six groups), 2 to <c>ByLevelHitEffect</c>
    /// (5001001, 5001002). No skill reaches the <c>level/1/hit</c> or the two
    /// id-specific classes. <c>hit/0</c> is in EVERY branch above, so a client that
    /// always plays group 0 draws something the reference could also draw.
    /// </para>
    /// </remarks>
    string HitSelector = "NoHitEffect",
    /// <summary>
    /// The child names of the level node this plan read the box and the damage
    /// from — empty when the skill has no level node at the character's level.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty array as the default: <c>Array.Empty</c> is not a
    /// compile-time constant (CS1736), and this is a diagnostic surface whose
    /// "not resolved" state is worth telling apart from "resolved, no children".
    /// </remarks>
    string[]? LevelKeys = null,
    /// <summary>
    /// The ground area a tile grid covers, as the reference's own
    /// <c>affected_area</c>: (<c>left</c>, <c>right</c>, <c>top</c>, <c>bottom</c>)
    /// RELATIVE to the caster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SkillUseEffect.cpp:187-199</c>:
    /// </para>
    /// <code>
    /// nl::node lv = src["level"]["30"];
    /// if (!lv) lv = src["level"]["1"];
    /// if (lv && lv["lt"] && lv["rb"])
    ///     affected_area = Rectangle&lt;int16_t&gt;(lv["lt"], lv["rb"]);
    /// else
    ///     affected_area = Rectangle&lt;int16_t&gt;(-400, 400, -350, 250);
    /// </code>
    /// <para>
    /// i.e. level 30, then level 1, then the official client's own default ??
    /// <b>not</b> the collision box this plan already carries in
    /// <see cref="Ltx"/> and friends. They are different rectangles on this
    /// archive: 3111003's level-30 box is (-150,-100)-(150,100) but its level-1
    /// box is (-130,-100)-(130,100), and 5121001's is (-400,-100)-(25,20), none
    /// of which the damage path reads.
    /// </para>
    /// <para>
    /// A tuple literal is not a compile-time constant, so this cannot be written
    /// as a default parameter value (CS1736). <c>null</c> is the wire for
    /// "the level-30 box, else the level-1 box, else this literal" ??resolve it
    /// through <see cref="DefaultTileArea"/> rather than inlining the numbers a
    /// second time.
    /// </para>
    /// </remarks>
    [property: System.Text.Json.Serialization.JsonIgnore]
    (int Left, int Right, int Top, int Bottom)? TileArea = null,
    /// <summary>
    /// The X step between columns of the tile grid, from the tile node's own
    /// <c>effectDistance</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SkillUseEffect.cpp:178-184</c>:
    /// </para>
    /// <code>
    /// int32_t dist = tilesrc["effectDistance"].get_integer(0);
    /// if (dist &gt; 0) effect_distance = static_cast&lt;int16_t&gt;(dist);
    /// else          effect_distance = (skillid == SkillId::GENESIS) ? 150 : 180;
    /// </code>
    /// <para>
    /// <b>MEASURED 2026-10-17</b>, <c>GET /api/children</c> on
    /// <c>skill/&lt;id&gt;/tile</c> of the seven skills that carry a tile node:
    /// 2121007 ??180, 12111003 ??180, 2221007 ??180, 2321008 ??<b>150</b>,
    /// 3111003 ??20, 5121001 ??200, 12111005 ??<i>absent</i>.
    /// </para>
    /// <para>
    /// <b>Correction to an earlier reading of this file:</b> the 150 belongs to
    /// <c>SkillId::GENESIS</c>, and <c>SkillId.h:172</c> declares
    /// <c>GENESIS = 2321008</c>. It is NOT 2121007 (that one is
    /// <c>Genesis_212</c>, "?萎?銋"). Both were believed to be Genesis; only
    /// 2321008 is the one the step default names. 2121007 reads 180 from its own
    /// archive node anyway, so on this archive the two agree and the constant
    /// never fires ??but the id the code tests is 2321008, and that is what is
    /// transcribed.
    /// </para>
    /// <para>
    /// <c>SkillUseEffect.cpp:243</c> re-guards it:
    /// <c>int16_t step = (effect_distance &gt; 0) ? effect_distance : 180;</c>
    /// </para>
    /// </remarks>
    int TileStep = 180,
    /// <summary>
    /// Whether the tile hangs on the CASTER (<c>false</c>) or on each struck mob
    /// (<c>true</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are decided by one skill id, and it is not a property of the
    /// archive:
    /// </para>
    /// <code>
    /// Skill.cpp:219-226   apply_useeffects: if (tileeffect &amp;&amp; skillid != 3111003) tileeffect-&gt;apply(user);
    /// Skill.cpp:524-525   apply_hiteffects: if (skillid == 3111003 &amp;&amp; tileeffect) tileeffect-&gt;apply_at_target(target);
    /// </code>
    /// <para>
    /// so 3111003 (?蝞? is the only skill whose ground decals land on the
    /// victims, at their feet, unflipped and at REAL time ??while every other
    /// skill grids them across the map under the caster, at the caster's attack
    /// speed.
    /// </para>
    /// </remarks>
    bool TileAtTarget = false,
    /// <summary>
    /// Whether the tile area is re-read at the CASTER's own skill level and
    /// mirrored by their facing. Only 5121001 (瞏??箸殿).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SkillUseEffect.cpp:224-232</c>:
    /// </para>
    /// <code>
    /// if (tile_skill_id == 5121001)
    /// {
    ///     const auto level = nl::nx::skill["512.img"]["skill"]["5121001"]["level"]
    ///         [std::to_string(target.get_skilllevel(tile_skill_id))];
    ///     if (level["lt"] &amp;&amp; level["rb"])
    ///         area = Rectangle&lt;int16_t&gt;(level["lt"], level["rb"]);
    ///     if (target.is_facing_right())
    ///         area = Rectangle&lt;int16_t&gt;(-area.right(), -area.left(), area.top(), area.bottom());
    /// }
    /// </code>
    /// <para>
    /// The mirror is the same shape <c>MobAttackGeometry.h:45-49</c> uses for a
    /// mob's attack box, and it exists because the box is authored for a
    /// LEFT-facing cast. Every other skill uses the area verbatim.
    /// </para>
    /// </remarks>
    bool TileAreaFollowsFacing = false)
{
    public bool HasCollisionBox => Ltx is not null && Lty is not null && Rbx is not null && Rby is not null;

    public int BoxWidth => HasCollisionBox ? Rbx!.Value - Ltx!.Value : 0;
    public int BoxHeight => HasCollisionBox ? Rby!.Value - Lty!.Value : 0;

    /// <summary>
    /// <see cref="Affected"/>, flattened to an empty list rather than null so a
    /// caller can enumerate it without a null check.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<SkillCastSprite> AffectedSprites =>
        Affected ?? System.Array.Empty<SkillCastSprite>();

    // The tile area, flattened onto the wire as four integers.
    //
    // System.Text.Json serialises a ValueTuple as `{}` unless a tuple converter is
    // registered, and none is: MEASURED, `/api/skill-cast/plan` answered
    // `"tileArea": {}` for every skill, so the one field that decides WHERE a
    // ground decal lands read as empty on a surface whose whole job is to be read.
    // Four named ints are also what this record already does for the collision box
    // (`Ltx`/`Lty`/`Rbx`/`Rby`), so the wire shape stops being a special case.
    [System.Text.Json.Serialization.JsonIgnore]
    public (int Left, int Right, int Top, int Bottom) ResolvedTileArea =>
        TileArea ?? SkillCastService.DefaultTileArea;

    /// <summary><see cref="ResolvedTileArea"/>'s left edge, relative to the caster.</summary>
    public int TileAreaLeft => ResolvedTileArea.Left;

    /// <summary><see cref="ResolvedTileArea"/>'s right edge, relative to the caster.</summary>
    public int TileAreaRight => ResolvedTileArea.Right;

    /// <summary><see cref="ResolvedTileArea"/>'s top edge, relative to the caster.</summary>
    public int TileAreaTop => ResolvedTileArea.Top;

    /// <summary><see cref="ResolvedTileArea"/>'s bottom edge, relative to the caster.</summary>
    public int TileAreaBottom => ResolvedTileArea.Bottom;
}

/// <summary>
/// Cast-time resolution for skills, and the timeline the reference derives from
/// the archive.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>_necrocer_src/MapleNecrocer/Client/Skill.cs</c>. Every rule
/// below cites the reference line it came from; nothing here is inferred.
/// </para>
/// <para>
/// The reference is a v2xx-lineage editor client, and this archive is v83. The
/// parts that survive that difference are the ones about node NAMES and node
/// SHAPE, and those are the parts used here. Damage numbers are not modelled:
/// the reference has no server, so it never computes one ??<see cref="Plan"/>
/// reports the geometry and the timing, and the damage stays where it already
/// lives, in the simulator's own swing path.
/// </para>
/// <para>
/// <b>W-089.</b> Two whole classes of MapleNecrocer literal were removed in this
/// pass, both because the authoritative reference has nothing they could be a
/// transcription of:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>MultiStrike</c> &#8212; <c>skillId is not ("1121008" or "1001005" or
/// "1311012")</c>, MapleNecrocer <c>Skill.cs:168-178</c>. The reference's own
/// counters are <c>attackCount</c> and <c>mobCount</c>, read off the level node
/// (<c>SkillData.cpp:68-69</c>) and kept apart (<c>Skill.cpp:394,397-405</c>). They
/// are now <see cref="SkillCastPlan.HitCount"/> and
/// <see cref="SkillCastPlan.MobCount"/>.
/// </item>
/// <item>
/// <c>MultiStrike</c>'s downstream effect, which was to re-drive the same bool from
/// <c>mobCount &gt; 1</c> &#8212; i.e. to answer "how many mobs" on a field named
/// "how many times". 56 of the 66 plans disagreed with <c>attackCount &gt; 1</c>.
/// </item>
/// </list>
/// <para>
/// What did NOT change: every literal the reference itself contains. The tile
/// default rectangle, the Genesis 150 step, the 5121001 facing mirror and the 3111003
/// at-target decal are all <c>SkillUseEffect.cpp</c>/<c>Skill.cpp</c> text and stay.
/// </para>
/// </remarks>
public sealed class SkillCastService
{
    private readonly WzSessionService _session;

    private readonly AnimationService _animation;

    public SkillCastService(WzSessionService session, AnimationService animation)
    {
        _session = session;
        _animation = animation;
    }

    /// <summary>
    /// The official client's own <c>affected_area</c> fallback, used when a skill's
    /// <c>level/30</c> and then its <c>level/1</c> carry no <c>lt</c>/<c>rb</c>.
    /// </summary>
    /// <remarks>
    /// <c>SkillUseEffect.cpp:197-199</c> ??
    /// <c>else affected_area = Rectangle&lt;int16_t&gt;(-400, 400, -350, 250);</c>
    /// </remarks>
    public static readonly (int Left, int Right, int Top, int Bottom) DefaultTileArea =
        (-400, 400, -350, 250);

    /// <summary>
    /// The skill book an id lives in.
    /// </summary>
    /// <remarks>
    /// <c>Skill.cs:31-37</c>. A nine-digit id starting "800" is an item, and an
    /// item's book is the hundred it sits in; everything else is the ten
    /// thousand. Verified against this archive: <c>1100000 -> 110.img</c>,
    /// <c>1121008 -> 112.img</c>, both of which resolve.
    /// </remarks>
    public static string BookId(string skillId)
    {
        string digits = skillId.Trim();
        return digits.Length == 9 && digits.StartsWith("800", StringComparison.Ordinal)
            ? (int.Parse(digits, CultureInfo.InvariantCulture) / 100).ToString(CultureInfo.InvariantCulture)
            : (int.Parse(digits, CultureInfo.InvariantCulture) / 10000).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Frames between the cast starting and the first hit landing.
    /// </summary>
    /// <remarks>
    /// <c>Skill.cs:53-150</c>, transcribed whole. It is a hardcoded table keyed
    /// on skill id with a default of 15 ??not a formula, not a curve ??so it is
    /// copied rather than re-derived. An id absent from the table gets the
    /// reference's own default.
    /// </remarks>
    public static int DamageWaitTime(string skillId)
    {
        int id = int.Parse(skillId, CultureInfo.InvariantCulture);
        return id switch
        {
            1311012 or 4341052 or 5101004 or 65121008 or 400040006 => 5,
            61111101 or 142121031 or 400041024 or 5221026 or 101120202 => 10,
            1321012 or 2211010 or 2301005 or 15111022 or 25121005
                or 101100100 or 101110202 or 101110203 or 155111211 or 400051042 => 15,
            3121015 or 11101008 or 31221002 or 41121017 => 20,
            15121002 or 1121008 or 1001005 or 1121015 or 2211002 or 5121017
                or 61121104 or 61121105 or 65111100 or 41121018 or 65121100 => 25,
            5321000 or 25121007 or 400041021 => 30,
            2221007 or 65121002 => 35,
            5121016 or 5121052 or 15121052 or 101120102 or 36121011 or 400021002 => 40,
            5221052 or 27111101 or 32121004 => 45,
            27111303 or 27121202 => 50,
            31221052 => 55,
            2321008 or 2121007 or 41121052 or 400001014 => 70,
            61121052 => 85,
            5121013 => 95,
            4341011 => 120,
            36121052 => 160,
            _ => 15,
        };
    }

    /// <summary>
    /// W-089. How many TIMES one mob is struck, from the skill's own
    /// <c>attackCount</c> -- or from <c>bulletCount</c> when the skill throws a
    /// projectile. Default 1 when the archive carries neither.
    /// </summary>
    /// <param name="levelNode">
    /// The <c>level/&lt;n&gt;</c> node the rest of the plan read, or null.
    /// </param>
    /// <param name="projectile">
    /// Whether the skill carries a <c>ball</c> node ??<c>Skill.cpp:201-216</c>.
    /// </param>
    /// <param name="node">Which node the number came from, "" when both are absent.</param>
    /// <remarks>
    /// <para>
    /// <b>This replaces <c>MultiStrike(string)</c>, which was deleted.</b> That method
    /// was <c>skillId is not ("1121008" or "1001005" or "1311012")</c>, transcribed
    /// from MapleNecrocer <c>Skill.cs:168-178</c> ??a program this project does not
    /// treat as authoritative. Three magic ids, no archive behind them, and the
    /// authoritative reference has no such concept to transcribe.
    /// </para>
    /// <para>
    /// The reference's own answer is a NUMBER off the level node:
    /// </para>
    /// <code>
    /// SkillData.cpp:68   uint8_t attackcount = (uint8_t)sub["attackCount"].get_integer(1);
    /// SkillData.cpp:70   uint8_t bulletcount = (uint8_t)sub["bulletCount"].get_integer(1);
    /// Skill.cpp:397-405  switch (attack.type) {
    ///                     case Attack::RANGED: attack.hitcount = stats.bulletcount; break;
    ///                     default:            attack.hitcount = stats.attackcount; break; }
    /// </code>
    /// <para>
    /// <b>What the three ids actually had in common: nothing.</b> Measured
    /// 2026-10-18 against this archive:
    /// </para>
    /// <list type="bullet">
    /// <item><c>1121008</c> (<c>SkillId.h:70</c>, BRANDISH) carries
    /// <c>level/1/attackCount = 2</c> (<c>Long</c>) ??the reference strikes it TWICE,
    /// and the old guard was the one calling it single-strike. Only one of the three
    /// is castable here at all; <c>1001005</c> (<c>SkillId.h:42</c>, SLASH_BLAST) has
    /// no <c>hit</c> node so <c>Castable()</c> never lists it, and <c>1311012</c> is
    /// not a declared <c>SkillId</c> in the reference.</item>
    /// <item><c>attackCount</c> is present on <b>10 of 66</b> castable skills and is
    /// <b>2 on 1311001</b>, <b>4 on 11111006 / 14101006 / 21120006</b>, <b>8 on
    /// 5121004</b>, <b>10 on 4211006</b> ??every one of which the guard called
    /// "multi-strike" for no reason connected to the archive.</item>
    /// <item><c>bulletCount</c> exists on exactly one castable skill,
    /// <c>13101005</c>, and its value is <b>0</b>.</item>
    /// </list>
    /// <para>
    /// And the third thing the old guard got wrong: after W-084 the bool had been
    /// re-driven from <c>mobCount &gt; 1</c>, so it answered "how many Mobs", which
    /// is a different question. 56 of the 66 plans disagreed with
    /// <c>attackCount &gt; 1</c>. Both axes are now separate fields,
    /// <see cref="SkillCastPlan.MobCount"/> and <see cref="SkillCastPlan.HitCount"/>.
    /// </para>
    /// <para>
    /// <b>Residual, stated rather than hidden:</b> the reference's branch is on
    /// <c>attack.type == RANGED</c>, which is
    /// <c>projectile || SkillData::is_ranged()</c> (<c>Skill.cpp:373</c>), and
    /// <c>is_ranged()</c> is a ~150-entry literal id table
    /// (<c>SkillData.cpp:139-333</c>). Only the archive-driven half
    /// (<c>projectile</c>, i.e. "has a <c>ball</c> node") is read here. MEASURED: 5 of
    /// the 66 carry <c>ball</c> and <b>none of them carries <c>bulletCount</c></b>,
    /// so both branches answer 1 for every skill on this archive and the table would
    /// change nothing today. Transcribing 150 ids to make a number identical is the
    /// kind of guess this file does not make.
    /// </para>
    /// </remarks>
    public int HitCountOf(WzObject? levelNode, bool projectile, out string node)
    {
        node = "";
        if (levelNode is null) return 1;
        // RANGED first, then the default branch -- Skill.cpp:397-405.
        foreach (string name in projectile
            ? new[] { "bulletCount", "attackCount" }
            : new[] { "attackCount", "bulletCount" })
        {
            if (Child(levelNode, name) is not { } child) continue;
            if (!TryReadReal(child, out double raw)) continue;
            node = child.Name;
            // SkillData.cpp casts to uint8_t, so a negative or oversized authored
            // value wraps there. Clamped rather than wrapped: the byte the reference
            // gets out of the cast is never something a plan should report.
            return (int)Math.Clamp((long)Math.Round(raw), 0L, 255L);
        }
        return 1;
    }

    /// <summary>
    /// The node names that may carry a skill's damage, in the order they are
    /// tried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SkillData.cpp:58,65,66</c> reads <c>damage</c>, <c>mad</c> and
    /// <c>fixdamage</c> off the same level node. Physical skills carry
    /// <c>damage</c>; a mage, rook or night lord skill carries <c>mad</c> and has
    /// no <c>damage</c> at all — MEASURED, 22 of the 66 castable skills.
    /// </para>
    /// <para>
    /// <c>damagepc</c> / <c>maddpc</c> are the PLAYER-side numbers, the ones used
    /// when a character casts the skill rather than when a mob uses it, and a skill
    /// may carry them instead of the mob-side pair. MEASURED 2026-10-18: the four
    /// tutorial-book skills (<c>10001009</c>, <c>10001020</c>, <c>20001009</c>,
    /// <c>20001020</c>) have <c>level/1</c> = <c>damagepc, lt, mobCount, rb</c> and
    /// nothing else. Since this simulator only ever casts as a player, that is the
    /// node that governs them.
    /// </para>
    /// </remarks>
    public static readonly string[] DamageNodes =
        ["damage", "damagepc", "mad", "maddpc", "fixdamage"];

    /// <summary>
    /// The node names under a skill that may carry a cast sprite, in the
    /// reference's own order.
    /// </summary>
    /// <remarks>
    /// <c>Skill.cs:154-155</c>, verbatim including order. The loop is
    /// <c>for (int i = 0; i &lt;= 10; i++)</c>, i.e. all eleven.
    /// </remarks>
    public static readonly string[] EffectNodes =
    {
        "effect", "effect0", "effect1", "effect2", "effect3",
        "screen", "screen0", "ball", "keydown", "keydown0", "keydowned",
    };

    /// <summary>
    /// W-089. How far past <see cref="EffectNodes"/>' last <c>effectN</c> the walk
    /// keeps going looking for one more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's <c>MultiUseEffect</c> (<c>SkillUseEffect.cpp:98-110</c>) is
    /// UNBOUNDED:
    /// </para>
    /// <code>
    /// int8_t no = -1;
    /// nl::node sub = src["effect"];
    /// while (sub) {
    ///     effects.push_back(sub);
    ///     no++;
    ///     sub = src["effect" + std::to_string(no)];
    /// }
    /// </code>
    /// <para>
    /// i.e. <c>effect</c>, <c>effect0</c>, <c>effect1</c>, ... until one is missing.
    /// The literal list above stops at <c>effect3</c>, so a skill with a fourth
    /// variant would lose it. It is bounded here because a typo or an inlined
    /// archive can make an unbounded walk a long loop, and 64 is far past anything
    /// this archive uses. MEASURED 2026-10-18: the deepest is
    /// <c>effect3</c> (1311001, 1311002, 15111003, 5111006), so the extra walk adds
    /// nothing today and costs one lookup per plan.
    /// </para>
    /// <para>
    /// <c>screen</c>, <c>screen0</c>, <c>keydown</c>, <c>keydown0</c> and
    /// <c>keydowned</c> are <b>not</b> in the authoritative reference at all ??they
    /// come from MapleNecrocer <c>Skill.cs:154-155</c>, the secondary reference. They
    /// are kept because they are real archive nodes on real skills (4 skills carry
    /// <c>screen</c>, 2 carry <c>keydown</c>) and dropping a sprite the archive
    /// authored would be a worse answer than drawing one the reference does not.
    /// <c>ball</c> is in both: the reference reads it as the PROJECTILE
    /// (<c>Skill.cpp:201-216</c>), not as a cast sprite.
    /// </para>
    /// </remarks>
    public const int MaxMultiEffectIndex = 64;

    /// <summary>
    /// The one skill whose ground decal lands on the VICTIM rather than on the
    /// caster, and the one skill whose decal area is re-read per cast and
    /// mirrored by facing. Both are plain literals in the reference, transcribed.
    /// </summary>
    /// <remarks>
    /// <code>
    /// Skill.cpp:223             if (tileeffect &amp;&amp; skillid != 3111003) tileeffect-&gt;apply(user);
    /// Skill.cpp:524             if (skillid == 3111003 &amp;&amp; tileeffect) tileeffect-&gt;apply_at_target(target);
    /// SkillUseEffect.cpp:222   if (tile_skill_id == 5121001) { re-read lt/rb; mirror when facing right; }
    /// </code>
    /// Named rather than inlined at the call site so the two places that need
    /// them cannot disagree about which id means what.
    /// </remarks>
    public const string TileTargetSkillId = "3111003";

    /// <summary>See <see cref="TileTargetSkillId"/>.</summary>
    public const string TileFacingSkillId = "5121001";

    /// <summary>
    /// The area a tile grid covers when the skill's level nodes carry no
    /// <c>lt</c>/<c>rb</c>: the official client's own default rectangle.
    /// </summary>
    /// <remarks>
    /// <c>SkillUseEffect.cpp:199</c> ??<c>affected_area =
    /// Rectangle&lt;int16_t&gt;(-400, 400, -350, 250);</c> The comment beside it
    /// names it "rcAffectedArea in official client", so it is the archive's
    /// absence answered by the client, not a number invented here. Order is
    /// <c>(left, right, top, bottom)</c> because that is the reference's
    /// <c>Rectangle(T left, T right, T top, T bottom)</c>.
    /// </remarks>
    public static readonly (int Left, int Right, int Top, int Bottom) TileDefaultArea = (-400, 400, -350, 250);

    /// <summary>
    /// The <c>lt</c>/<c>rb</c> at one named level of a skill, or null when the
    /// archive carries neither corner there.
    /// </summary>
    /// <param name="skillId">The skill's own id.</param>
    /// <param name="level">The level node's name, e.g. <c>"1"</c>.</param>
    /// <remarks>
    /// <para>
    /// This exists for <see cref="TileFacingSkillId"/> alone. The reference
    /// re-reads the box at the CASTER's own skill level on every cast ??
    /// <c>SkillUseEffect.cpp:226-229</c>:
    /// </para>
    /// <code>
    /// const auto level = nl::nx::skill["512.img"]["skill"]["5121001"]["level"]
    ///     [std::to_string(target.get_skilllevel(tile_skill_id))];
    /// if (level["lt"] &amp;&amp; level["rb"]) area = Rectangle&lt;int16_t&gt;(level["lt"], level["rb"]);
    /// </code>
    /// <para>
    /// That is a per-cast read of a node <see cref="Plan"/> already walks while
    /// resolving the damage, so the box comes back through <see cref="Corners"/>
    /// rather than a second vector reader. Returning null when either corner is
    /// missing is the <c>level["lt"] &amp;&amp; level["rb"]</c> test itself, and it is
    /// what leaves the cached area in place ??the reference keeps its previous
    /// <c>area</c> when the re-read finds nothing, it does not reset.
    /// </para>
    /// </remarks>
    public (int Left, int Right, int Top, int Bottom)? TileAreaAtLevel(string skillId, int level)
    {
        if (!int.TryParse(skillId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return null;

        lock (_session.Gate)
        {
            string book = BookId(skillId);
            foreach (OpenFile file in _session.SelectRoleSources("Skill", null))
            {
                foreach (string prefix in new[] { "", "Skill/" })
                {
                    if (_session.TryResolve($"{file.Id}/{prefix}{book}.img/skill/{skillId}")
                        is not { } entry)
                        continue;
                    WzObject? levelNode = Child(entry, "level");
                    if (levelNode is null)
                        return null;
                    // The level NAME, not a path: `["std::to_string(level)"]` indexes
                    // the level map by its own key.
                    if (Child(levelNode, level.ToString(CultureInfo.InvariantCulture)) is not { } at)
                        return null;
                    return Corners(at) is { } c ? (c.Left, c.Right, c.Top, c.Bottom) : null;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Build the cast plan for a skill, or null when the archive has no such skill.
    /// </summary>
    /// <param name="skillId">The skill's own id, e.g. <c>1121008</c>.</param>
    /// <param name="characterLevel">
    /// The caster's level. It decides <b>which</b> skill level the damage node is
    /// read at, clamped to the skill's own <c>maxLevel</c>.
    /// </param>
    /// <remarks>
    /// Defaults to 1 so the read-only endpoints (<c>/api/skill-cast/plan</c>, which
    /// has no character) keep reporting a level-1 plan rather than guessing one.
    /// The simulator itself passes <c>session.Status.Level</c>, the same source
    /// <c>DamageRoll</c> already gets for a swing.
    /// </remarks>
    /// <param name="realAttackSpeed">
    /// W-076. The factor the effect sprite clock runs at: <c>Char.cpp:607-612</c>
    /// computes <c>1.7f - speed/10</c> and <c>EffectLayer.h:59-61</c> advances
    /// <c>TIMESTEP * speed</c> per tick, so a frame lasts <c>delay / speed</c> ms.
    /// Passed in rather than derived here because this service has no equipment ??
    /// CombatService owns that reading, and reading the weapon in a second class
    /// is how two copies of a number drift apart. 1.7f is what the reference gives
    /// a character whose weapon carries no <c>info/attackSpeed</c>.
    /// </param>
    public SkillCastPlan? Plan(string skillId, int characterLevel = 1, float realAttackSpeed = 1.7f)
    {
        if (!int.TryParse(skillId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return null;

        lock (_session.Gate)
        {
            string book = BookId(skillId);
            string? entryPath = null;
            WzObject? entry = null;

            foreach (OpenFile file in _session.SelectRoleSources("Skill", null))
            {
                foreach (string prefix in new[] { "", "Skill/" })
                {
                    string path = $"{file.Id}/{prefix}{book}.img/skill/{skillId}";
                    if (_session.TryResolve(path) is { } found)
                    {
                        entry = found;
                        entryPath = path;
                        break;
                    }
                }
                if (entry is not null)
                    break;
            }

            if (entry is null || entryPath is null)
                return null;

            // ---- how many TIMES a mob is struck, and whether the skill throws
            // something. W-089. Both are properties of the ARCHIVE, not of the
            // skill id, which is why they are read here rather than looked up in
            // a table:
            //     Skill.cpp:201  bool hasball     = src["ball"].size() > 0;
            //     Skill.cpp:204  if (bylevelball) ... else if (hasball) bullet = SingleBullet(src);
            //                     else { bullet = RegularBullet(); projectile = false; }
            WzObject? levelRoot = Child(entry, "level");
            bool projectile = Child(entry, "ball") is not null
                || (levelRoot is not null
                    && Child(levelRoot, "1") is { } one
                    && Child(one, "ball") is not null);

            // ---- action: the stances the character plays while casting.
            //
            // W-089 (C3). The reference's ladder, Skill.cpp:158-199, in order:
            //
            //   hasaction0 && hasaction1 -> TwoHandedAction  (action/0, action/1)
            //   hasaction0                 -> SingleAction    (action/0)
            //   prepare/action is a string -> SingleAction    (prepare/action)
            //   is_attack():
            //       level/1/action is a string -> ByLevelAction (level/<skilllevel>/action)
            //       else                       -> RegularAction / Genesis special
            //   else                      -> NoAction
            //
            // Only the entry node was read before, so a skill whose entry `action`
            // is an EMPTY sub-property -- which is a node in its own right and
            // therefore never null -- fell straight through to "alert".
            //
            // MEASURED 2026-10-18: exactly two of the 66 hit that, 1311001 and
            // 1311002, and both carry a full 30-level ladder:
            //     1311001  entry/action = []  level/*/action = 1=burster1 ... 15=burster1
            //              16=burster2 ... 30=burster2
            // so the plan said "alert" where the reference plays "burster1". Both
            // ids are ATTACK-flagged in the reference (SkillData.cpp:146-147,
            // DRAGON_BUSTER / PA_BUSTER), which is the `is_attack()` gate that puts
            // ByLevelAction in reach.
            //
            // `prepare/action` is transcribed too even though nothing on this
            // archive reaches it: 15101003 and 5101004 both carry
            // `prepare = {action:"dash", time:0}`, but their entry `action/0` is a
            // string ("screw"), and that branch comes FIRST -- so the reference
            // plays "screw" as well, and this was already right.
            var actions = new List<string>();
            WzObject? actionNode = Child(entry, "action");
            if (actionNode is not null)
            {
                foreach (WzObject frame in Children(actionNode))
                {
                    string name = AsText(frame) ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                        actions.Add(name);
                }
            }
            if (actions.Count == 0
                && Child(entry, "prepare") is { } prepare
                && Child(prepare, "action") is { } prepareAction
                && AsText(prepareAction) is { } held
                && !string.IsNullOrWhiteSpace(held))
            {
                // Skill.cpp:169-171 -- `prepare/action` is the held pose a charge
                // skill freezes in before the release.
                actions.Add(held);
            }
            if (actions.Count == 0 && levelRoot is not null && maxLevelOf(levelRoot) > 0)
            {
                // Skill.cpp:175-181 ByLevelAction, and its lookup is EXACT:
                //     int32_t level = target.get_skilllevel(skillid);
                //     auto iter = actions.find(level);
                //     if (iter != actions.end()) target.attack(iter->second);
                // i.e. the level the character has the skill AT, not "the largest
                // at or below". The same clamp the damage read uses supplies it.
                if (Child(levelRoot, skillLevelOf(levelRoot, characterLevel)
                        .ToString(CultureInfo.InvariantCulture)) is { } actionLevel
                    && Child(actionLevel, "action") is { } byLevel
                    && AsText(byLevel) is { } name2
                    && !string.IsNullOrWhiteSpace(name2))
                {
                    actions.Add(name2);
                }
            }
            if (actions.Count == 0)
                actions.Add("alert");

            // ---- effect/screen/ball/... : the cast sprites.
            // Skill.cs:247-254. A node holding "0/0" is addressed by its "0"
            // child and its first frame; otherwise it is the node itself.
            var effects = new List<SkillCastSprite>();
            int effectMs = 0;

            void AddCastSprite(string nodeName, WzObject node, string basePath)
            {
                // Skill.cs:251-254. A node that holds "0/0" is addressed through
                // its "0" child; anything else is addressed as itself.
                WzObject? zero = Child(node, "0");
                bool nested = zero is not null && Child(zero, "0") is not null;
                WzObject holder = nested ? zero : node;
                string parentPath = nested ? $"{basePath}/0" : basePath;
                int frames = Children(holder).Count;
                // W-076: every frame's own `origin` and `delay`, read
                // here while the frame nodes are already in hand -- they
                // differ per frame (measured 30,23 then 49,81 on
                // 1121008) and walking the tree twice per effect per
                // cast would cost a second resolution.
                var origins = new List<PointDto>(frames);
                var delays = new List<int>(frames);
                foreach (WzObject frameNode in Children(holder))
                {
                    origins.Add(OriginOf(frameNode));
                    delays.Add(DelayOf(frameNode));
                }
                // SkillUseEffect.h:50 reads z off the effect node.
                int z = 0;
                foreach (string zHolder in new[] { parentPath, basePath })
                {
                    WzObject? zn = Child(_session.TryResolve(zHolder), "z");
                    if (zn is WzIntProperty zi) { z = zi.Value; break; }
                    if (zn is WzLongProperty zl) { z = (int)zl.Value; break; }
                }
                effects.Add(new SkillCastSprite(nodeName, frames, $"{parentPath}/0",
                                               nodeName == "ball", parentPath,
                                               origins, delays, z, realAttackSpeed));

                // How long that sprite actually plays for, in ms, read
                // through the ONE animation reader in this codebase.
                //
                // `AnimationService` is the authority ??MobSpriteService
                // (:54-56) injects it for the same reason, and its own
                // remarks (AnimationService.cs:258-263) are where the
                // "delay lives on the container" rule is written down
                // against verified bytes. A private reader here is what
                // produced 900ms where the archive says 1080ms.
                effectMs = Math.Max(effectMs, _animation.Describe(parentPath).TotalMs);
            }

            // W-089 (C3). `CharLevel/<n>/effect` REPLACES the entry-level use
            // effect, it does not join it.
            //
            // Skill.cpp:46-52:
            //     bool byleveleffect = src["CharLevel"]["10"]["effect"].size() > 0;
            //     if (byleveleffect)      useeffect = ByLevelUseEffect(src);
            //     else if (multieffect)    useeffect = MultiUseEffect(src);
            //     else { ... SingleUseEffect / TwoHandedUseEffect / NoUseEffect }
            //
            // and `ByLevelUseEffect`'s constructor (SkillUseEffect.cpp:118-125)
            // reads ONLY `sub["effect"]` for each `CharLevel/<n>` -- so when the
            // branch is taken, `skill/<id>/effect` and `effect0..N` are never
            // spawned at all.
            //
            // MEASURED 2026-10-18: exactly two of the 66, 5001001 (Flash Fist) and
            // 5001002 (Somersault Kick), and both carry the entry node too, so
            // reading the wrong one drew the wrong punch:
            //     5001001  CharLevel/10/effect = 0..4 (5 canvases)
            //     5001002  CharLevel/10/effect = 0..6 (7 canvases)
            WzObject? charLevel = Child(entry, "CharLevel");
            bool byLevelEffect = charLevel is not null
                && Child(charLevel, "10") is { } ten
                && Child(ten, "effect") is { } tenEffect
                && Children(tenEffect).Count > 0;

            if (byLevelEffect)
            {
                if (CharLevelPick(charLevel!, characterLevel, "effect") is { } chosen
                    && Child(chosen.Node, "effect") is { } effectNode)
                {
                    AddCastSprite($"CharLevel/{chosen.Key}/effect", effectNode,
                        $"{entryPath}/CharLevel/{chosen.Key}/effect");
                }
            }
            else
            {
                foreach (string nodeName in EffectNodes)
                {
                    if (Child(entry, nodeName) is { } node)
                        AddCastSprite(nodeName, node, $"{entryPath}/{nodeName}");
                }
                // SkillUseEffect.cpp:98-110 walks `effect`, `effect0`, `effect1`,
                // ... until one is missing. The fixed list above stops at
                // effect3; this keeps going so a fourth variant is not silently
                // dropped. See MaxMultiEffectIndex.
                for (int i = EffectNodes.Count(n => n.StartsWith("effect", StringComparison.Ordinal)
                        && n.Length > "effect".Length);
                     i < MaxMultiEffectIndex; i++)
                {
                    if (Child(entry, $"effect{i}") is not { } more) break;
                    AddCastSprite($"effect{i}", more, $"{entryPath}/effect{i}");
                }

                // Skill.cpp:86-92 -- the held/charging pose, when the skill has one.
                //     if (src["prepare"]) {
                //         bool isanimationPrepare = src["prepare"]["0"].data_type() == bitmap;
                //         if (isanimationPrepare) { prepare_animation = Animation(src["prepare"]);
                //                                    useeffectPrepare = SingleUseEffectPrepare(src); } }
                // The `data_type() == bitmap` test is the whole gate: a `prepare`
                // holding only `action`/`time` is NOT a sprite. MEASURED 2026-10-18:
                // 15101003 and 5101004 both carry `prepare = {action:"dash",
                // time:0}` and neither has a `prepare/0`, so nothing changes today;
                // the branch is transcribed because it is the reference's rule and
                // costs one lookup.
                if (Child(entry, "prepare") is { } prepareNode
                    && Child(prepareNode, "0") is WzCanvasProperty)
                {
                    AddCastSprite("prepare", prepareNode, $"{entryPath}/prepare");
                }
            }

            // Skill.cpp:107-110 -- the post-cast animation, kept OUT of `effects`
            // so it cannot lengthen the cast lock (see SkillCastPlan.Affected).
            List<SkillCastSprite>? affectedSprites = null;
            if (Child(entry, "affected") is { } affectedNode
                && Child(affectedNode, "0") is WzCanvasProperty)
            {
                int before = effects.Count;
                int msBefore = effectMs;
                AddCastSprite("affected", affectedNode, $"{entryPath}/affected");
                if (effects.Count > before)
                {
                    affectedSprites = effects.GetRange(before, effects.Count - before);
                    // Undo the side effects on the CAST: the lock is the casting
                    // sprites' longest, and `affected` is played after the hit.
                    effects.RemoveRange(before, effects.Count - before);
                    effectMs = msBefore;
                }
            }

            // ---- hit: the effect drawn on the thing that got hit.
            //
            // `SkillHitEffect.cpp:24`  SingleHitEffect::SingleHitEffect
            //     : effect(src["hit"]["0"])
            // so the effect node is one GROUP, `hit/<g>`, and the frames hang off
            // it. `Animation.cpp:166-193` then builds the frame list out of that
            // node, and its rules are the ones this reader follows:
            //
            //     if (istexture) frames.push_back(src);
            //     else { std::set<int16_t> frameids;
            //            for (auto sub : src)
            //                if (sub.data_type() == bitmap) frameids.insert(atoi(sub.name()));
            //            for (auto& fid : frameids) frames.push_back(src[to_string(fid)]); }
            //
            // Three consequences, all of which the archive exercises:
            //   - ONLY BITMAP children are frames. `pos`, `hitAfter` and the like
            //     are not, and counting them as frames would put holes in the
            //     animation.
            //   - The names go through `or_default<int16_t>(name, -1)` and are
            //     only taken when >= 0, so a non-numeric child is not a frame.
            //   - `frameids` is a `std::set<int16_t>`, so the frames are walked in
            //     NUMERIC order and a gap is skipped rather than counted through.
            //     MEASURED: 22 of 239 groups are not `0..n-1` ??`121.img/1211002`
            //     carries `1,2,3,5` with no `0`, and eight groups carry
            //     `0..9,10..22` interleaved ??so walking the node's children in
            //     ARCHIVE order and re-numbering them would play those backwards.
            var hitEffects = new List<SkillHitSprite>();

            // W-089 (C3). The reference picks ONE `SkillHitEffect` subclass and that
            // subclass names the node(s) it reads. `CharLevel/<n>/hit` REPLACES the
            // entry-level `hit` the same way `CharLevel/<n>/effect` replaces `effect`:
            //
            //   Skill.cpp:113-114  bool bylevelhit     = src["CharLevel"]["10"]["hit"].size() > 0;
            //                       bool byskilllevelhit = src["level"]["1"]["hit"].size() > 0;
            //   Skill.cpp:134-143  if (bylevelhit)      hiteffect = ByLevelHitEffect / ByLevelTwoHHitEffect;
            //                       else if (byskilllevelhit) hiteffect = BySkillLevelHitEffect;
            //   SkillHitEffect.cpp:51-58  ByLevelHitEffect reads `sub["hit"]["0"]` per CharLevel key
            //
            // MEASURED 2026-10-18: two of the 66 take that branch (5001001, 5001002)
            // and both carry an entry-level `hit` too, so the wrong node was being
            // read; NO skill carries `level/<n>/hit`, so BySkillLevelHitEffect is
            // transcribed but inert.
            string hitBase = $"{entryPath}/hit";
            WzObject? hitNode = Child(entry, "hit");
            bool bySkillLevelHit = false;
            bool byLevelHit = charLevel is not null
                && Child(charLevel, "10") is { } tenHit
                && Child(tenHit, "hit") is { } tenHitNode
                && Children(tenHitNode).Count > 0;

            if (byLevelHit
                && CharLevelPick(charLevel!, characterLevel, "hit") is { } levelHit
                && Child(levelHit.Node, "hit") is { } hit)
            {
                hitBase = $"{entryPath}/CharLevel/{levelHit.Key}/hit";
                hitNode = hit;
            }
            else if (levelRoot is not null
                && skillLevelOf(levelRoot, characterLevel) is int sl && sl > 0
                && Child(levelRoot, sl.ToString(CultureInfo.InvariantCulture)) is { } skillLevelNode
                && Child(skillLevelNode, "hit") is { } skillHit)
            {
                // Skill.cpp:114,141 -- BySkillLevelHitEffect reads `level/<skilllevel>/hit/0`.
                hitBase = $"{entryPath}/level/{sl}/hit";
                hitNode = skillHit;
                bySkillLevelHit = true;
            }

            if (hitNode is not null)
            {
                int g = 0;
                foreach (WzObject group in Children(hitNode))
                {
                    // One read of the group, shared by every frame it holds. The
                    // per-frame `origin`/`delay` readers are the SAME two the
                    // cast sprite above uses -- a second reader here is how the two
                    // halves of one animation come to disagree about a number.
                    foreach (SkillHitSprite sprite in HitSprites(hitBase, group))
                        hitEffects.Add(sprite with { Group = g });
                    g++;
                }
            }

            // Which class the reference would have built, for the diagnostic.
            // Skill.cpp:113-156, in the order it tests. `HitEffects` still carries
            // EVERY group the archive holds -- that is the image inventory a client
            // needs -- and group 0 is inside every branch of the reference, so a
            // client that always plays group 0 draws something drawable.
            //
            // MEASURED 2026-10-18, and these are the expected results:
            //   60 x SingleHitEffect
            //    3 x TwoHandedHitEffect   15101003, 5101004, 4221007 (hit/0 AND hit/1)
            //    1 x RandomHitEffect      4211004
            //    2 x ByLevelHitEffect     5001001, 5001002
            string hitSelector =
                skillId == "5101003" ? "ByHitHitEffect"
                : skillId == "3101005" ? "FirstTargetHitEffect"
                : skillId == "4211004" ? "RandomHitEffect"
                : byLevelHit ? "ByLevelHitEffect"
                : bySkillLevelHit ? "BySkillLevelHitEffect"
                : Child(hitNode, "1") is not null ? "TwoHandedHitEffect"
                : Child(hitNode, "0") is not null ? "SingleHitEffect"
                : "NoHitEffect";

            // ---- tile: ground decals. SkillUseEffect.cpp:162-201, the
            // `TileFootHoldUseEffect` constructor, transcribed whole.
            //
            //     nl::node tilesrc = src["tile"];
            //     if (tilesrc) {
            //         for (size_t i = 0; i < 30; i++) {
            //             nl::node variant = tilesrc[std::to_string(i)];
            //             if (!variant || !variant["0"]) break;
            //             tile_animations.emplace_back(variant);
            //         }
            //         int32_t dist = tilesrc["effectDistance"].get_integer(0);
            //         effect_distance = dist > 0 ? dist : ((skillid == GENESIS) ? 150 : 180);
            //     }
            //
            // The loop is INDEXED and stops at the first gap, which is the part
            // that matters: the node also carries a scalar `effectDistance`
            // (MEASURED ??6 of the 7 tile skills carry one) and walking the
            // children instead of indexing 0..29 made that scalar the LAST
            // variant, so `rand() % size` could roll a decal whose "animation"
            // is the integer 20.
            var tiles = new List<SkillTileSprite>();
            int tileCount = 0;
            int tileStep = 180;
            (int Left, int Right, int Top, int Bottom) tileArea = TileDefaultArea;
            if (Child(entry, "tile") is { } tile)
            {
                for (int i = 0; i < 30; i++)
                {
                    WzObject? variant = Child(tile, i.ToString(CultureInfo.InvariantCulture));
                    // `if (!variant || !variant["0"]) break;` ??the variant must
                    // carry a frame 0 or the walk stops, and an ABSENT index is
                    // also a stop rather than something to skip past.
                    if (variant is null || Child(variant, "0") is null)
                        break;
                    foreach (SkillTileSprite sprite in TileSprites(entryPath, variant, i))
                        tiles.Add(sprite);
                }
                tileCount = tiles.Count;

                int dist = ReadIntNode(tile, "effectDistance", 0);
                // `SkillId::GENESIS` is 2321008 (SkillId.h:172). The old comment
                // here named 2121007, which is Genesis_212 and never reaches this
                // branch; 2121007 reads 180 from its own node anyway.
                tileStep = dist > 0 ? dist : (skillId == "2321008" ? 150 : 180);
            }

            // ---- the level this character has the skill AT, and the collision box that
            // goes with it.
            //
            // W-089 (C3): the box is read at the CHARACTER's level, not the skill's
            // ceiling. The reference takes ONE `Stats` per level and uses it for
            // everything:
            //
            //   Skill.cpp:317-319  int32_t level = user.get_skilllevel(skillid);
            //                       const SkillData::Stats stats =
            //                           SkillData::get(skillid).get_stats(level);
            //   SkillData.cpp:79-83 if (sub["lt"] && sub["rb"]) range = Rectangle<int16_t>(sub["lt"], sub["rb"]);
            //   Skill.cpp:459-461  else if (!stats.range.empty()) attack.range = stats.range;
            //
            // so a level-1 character swings with the LEVEL-1 box. Reading the top
            // level's box instead is measurably wrong: 24 of the 66 castable skills
            // carry a different `lt`/`rb` at level 1 than at the top level. The
            // widest of those is 1111006 / 2211002, whose level-1 box is
            // (-110,-82)-(110,83) and whose level-30 box is (-400,-350)-(400,250)
            // -- a hitbox four times the reach.
            //
            // `maxLevel` itself is unchanged and is NOT the reference's number:
            // SkillData.cpp:117 computes `masterlevel = stats.size()`, the COUNT of
            // level nodes. MEASURED 2026-10-18: over all 66 the count and the
            // largest level name are equal (30/30/30, 20/20/20, 1/1), so the two
            // rules cannot be told apart on this archive and the name-based one is
            // kept because it is also the clamp the damage read needs.
            int maxLevel = levelRoot is not null ? maxLevelOf(levelRoot) : 0;
            int skillLevel = maxLevel > 0 ? Math.Clamp(characterLevel, 1, maxLevel) : 0;
            WzObject? levelNode = levelRoot;
            WzObject? atLevel = levelNode is not null && skillLevel > 0
                ? Child(levelNode, skillLevel.ToString(CultureInfo.InvariantCulture))
                : null;
            (int lx, int ly, int rx, int ry)? box = Corners(atLevel);
            // The reference's own fallback for a level with no `lt`/`rb` is the
            // character's afterimage range (Skill.cpp:485-486), which this service
            // does not have. The entry ROOT is read instead so a plan still reports
            // a box for the low-version `common`-on-the-entry shape.
            // MEASURED 2026-10-18: no castable skill carries `lt`/`rb` on the entry
            // root, so this never fires for the 66.
            box ??= Corners(entry);

            // The tile AREA, read after `level` is in hand. SkillUseEffect.cpp:187-199:
            //
            //     nl::node lv = src["level"]["30"];
            //     if (!lv) lv = src["level"]["1"];
            //     if (lv && lv["lt"] && lv["rb"]) affected_area = Rectangle(lv["lt"], lv["rb"]);
            //     else                                affected_area = Rectangle(-400, 400, -350, 250);
            //
            // NOT the collision box above, and not `Corners(entry)` as its own
            // fallback is: the reference falls back to a LITERAL, and reading the
            // entry root instead would read a skill whose `lt`/`rb` happen to sit
            // beside `level`. `Corners` returns null unless BOTH corners are
            // present, which is the `lv["lt"] && lv["rb"]` test exactly.
            //
            // Level 30 then level 1, NOT the character's level -- this one really is
            // hard-coded in the reference, and it is the tile grid's area rather than
            // the hit box, which is why the two disagree.
            WzObject? tileLevel = Child(levelNode, "30") ?? Child(levelNode, "1");
            if (Corners(tileLevel) is { } tl)
                tileArea = (tl.Left, tl.Right, tl.Top, tl.Bottom);

            // ---- the damage node, at the level this character has the skill at.
            // W-061. The level is the CHARACTER's, clamped to the skill's own
            // `maxLevel` -- a character level 1 casting a 30-level skill is at
            // level 1 of it, which is also what makes the number grow as the
            // character grows rather than being fixed at the skill's ceiling.
            //
            // Declared own-design, and it is declared here rather than in a note
            // because it is the one number in this plan with no reference behind
            // it: real MapleStory is server-authoritative, the client never
            // computes skill damage, and neither reference does either. The node
            // itself is the archive's; only "which level is the character at" is
            // ours, and the clamp is the whole of that decision.
            int? damage = null;
            string damageNode = "";
            // The child names of the level this plan resolved, so "the node is not
            // there" is answerable from the plan instead of only from a debugger.
            //
            // Added because the two readers disagreed and the plan could not say
            // which was right: `/api/children?path=f8/521.img/skill/5211004/level/1`
            // lists `damage(73)` while this reader, on the same session, found no
            // `damage`, no `mad` and no `fixdamage` — so either the two are looking
            // at different trees or the value is not a number this code reads.
            string[] levelKeys = atLevel is null
                ? Array.Empty<string>()
                : Children(atLevel).Select(c => c.Name).ToArray();
            if (atLevel is not null)
            {
                // WHICH node carries the damage depends on the skill's school, and
                // reading only one of them silently deletes a third of the castable
                // list.
                //
                //   SkillData.cpp:58  float damage   = (float)sub["damage"] / 100;
                //   SkillData.cpp:65  int32_t matk    = sub["mad"];
                //   SkillData.cpp:66  int32_t fixdamage = sub["fixdamage"];
                //
                // MEASURED 2026-10-18, all three read off this archive at `level/1`:
                //
                //   1121008 轻舞飞扬 (warrior)  damage(135)  mad  fixdamage
                //   2321008 圣光普照 (bishop)     mad(430)  mastery(10)  mobCount(15)
                //
                // The bishop has NO `damage` node at all, so a reader that looked for
                // that name and nothing else left 22 of the 66 castable skills
                // dealing zero — every mage, rook and night lord skill in the list,
                // which is why the failures clustered so obviously by school.
                //
                // The MAGNITUDE stays ADR-0006's fixed test hit. What this fixes is
                // the presence test: "does this skill carry a damage number at the
                // character's level", which is what decides whether a landed cast
                // has a visible result at all.
                foreach (string candidate in DamageNodes)
                {
                    // OrdinalIgnoreCase, and ANY numeric width — see TryReadReal for
                    // why the width matters. `incLUk` is a real lower-case-k node on
                    // this archive (TRAPS T-10), and 5211004's `damage` is not an
                    // int.
                    foreach (WzObject child in Children(atLevel))
                    {
                        if (!string.Equals(child.Name, candidate, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!TryReadReal(child, out double raw))
                            break;
                        damage = (int)Math.Clamp(Math.Round(raw), 0L, int.MaxValue);
                        damageNode = child.Name;
                        break;
                    }
                    if (damage is not null)
                        break;
                }

                // `x` as the last resort, and ONLY when no damage node exists.
                //
                //   SkillData.cpp:61-64
                //     // Arrow Bomb (3101005) has no "damage" node in this WZ repack:
                //     // its damage rate lives in "x" ... so fall back to x when
                //     // damage is absent.
                //
                // The reference does this for one skill id; what it is really
                // describing is a SHAPE, and this archive has two more skills in it:
                // 4211006 金钱炸弹 carries `x(500)` and no damage node at all.
                // Gated on the absence of a damage node, so `x` can never override a
                // real one.
                if (damage is null && TryReadReal(atLevel, "x", out double rate))
                {
                    damage = (int)Math.Clamp(Math.Round(rate), 0L, int.MaxValue);
                    damageNode = "x";
                }
            }

            // ---- W-084/W-089: how many mobs, how many times, and how far.
            //
            //   SkillData.cpp:69   mobcount = sub["mobCount"].get_integer(1)
            //   SkillData.cpp:68   attackcount = sub["attackCount"].get_integer(1)
            //   SkillData.cpp:77   hrange   = (float)sub["range"].get_real(100.0) / 100
            //
            // All three live on the skill's own level node, and all three default
            // when the archive is silent. `range` here is a NUMBER, not the
            // collision box. `mobCount`/`attackCount` are read through the
            // any-width reader -- W-087's lesson -- so a `Float` or `String`
            // `mobCount` is a value rather than an absence.
            // MEASURED 2026-10-18: every `mobCount`, `attackCount` and `range` on
            // the 66 is `Long`; `damage` is the one that is sometimes a `String`.
            int mobCount = Math.Clamp(ReadIntNode(atLevel, "mobCount", 1), 0, MaxLoopedCount);
            // `get_real` on a `nl::node` parses a string node too, so this goes through
            // the ANY-WIDTH reader. It used to go through this file's second,
            // narrower numeric reader -- Int/Short/Long/Float/Double with no
            // String and no fall-through -- which is the same defect W-087 was
            // written for, on the one field whose absence moves a hit test.
            double hRange = TryReadReal(atLevel, "range", out double rangeRaw)
                ? rangeRaw / 100.0
                : 1.0;
            int hitCount = HitCountOf(atLevel, projectile, out string hitCountNode);

            // The timeline below is diagnostic only. It was built from Skill.cs:179-231,
            // which is MapleNecrocer, not this reference; it stays for the shape of
            // the output and is driven by mobCount now so it cannot claim six hits
            // for a skill that hits one.
            int wait = DamageWaitTime(skillId);
            int collisionCount = mobCount;
            int totalTime = wait + 6 * 7;

            var timeline = new List<SkillCastEvent>();
            for (int i = 0; i < collisionCount; i++)
                timeline.Add(new SkillCastEvent(wait + (i + 1) * 6, "mobTarget", i,
                    $"nearest mob #{i + 1} of {mobCount}"));
            if (hitCount > 1)
                timeline.Add(new SkillCastEvent(wait, "hitCount", 0,
                    $"{hitCount} hits per mob from `{hitCountNode}`"));
            foreach (SkillCastSprite e in effects)
                timeline.Add(new SkillCastEvent(0, "sprite", 0, $"{e.ImagePath} ({e.Frames} frames)"));
            foreach (SkillCastSprite e in affectedSprites ?? (IReadOnlyList<SkillCastSprite>)Array.Empty<SkillCastSprite>())
                timeline.Add(new SkillCastEvent(0, "affected", 0, $"{e.ImagePath} ({e.Frames} frames)"));
            foreach (SkillHitSprite h in hitEffects)
                timeline.Add(new SkillCastEvent(0, "hitSprite", h.Group * 100 + h.Index, h.ImagePath));
            if (tiles.Count > 0)
                timeline.Add(new SkillCastEvent(0, "tile", 0,
                    $"{tiles.Count} variant(s), area [{tileArea.Left},{tileArea.Right}]x[{tileArea.Top},{tileArea.Bottom}], step {tileStep}"
                    + (skillId == TileTargetSkillId ? ", per struck mob at its feet" : ", grid on the caster")));

            timeline.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick)
                : string.CompareOrdinal(a.Kind, b.Kind));

            return new SkillCastPlan(
                skillId, book, entryPath, actions, effects, hitEffects, tiles, tileCount,
                box?.lx, box?.ly, box?.rx, box?.ry,
                wait, collisionCount, totalTime, mobCount, hitCount, projectile, hRange, maxLevel,
                timeline, effectMs,
                skillLevel > 0 ? skillLevel : null, damage, damageNode, hitCountNode,
                affectedSprites, hitSelector, levelKeys,
                tileArea, tileStep, skillId == TileTargetSkillId, skillId == TileFacingSkillId);
        }
    }

    /// <summary>
    /// The largest number among a <c>level</c> node's child names, 0 when there
    /// are none. Used to clamp the character's skill level.
    /// </summary>
    /// <remarks>
    /// Not the reference's <c>masterlevel</c>: <c>SkillData.cpp:117</c> is
    /// <c>masterlevel = static_cast&lt;int32_t&gt;(stats.size())</c> ??the COUNT of
    /// level nodes. MEASURED 2026-10-18 over the 66 castable skills: the count and
    /// the largest name are equal on every one of them (levels <c>1</c>;
    /// <c>1..20</c>; <c>1..30</c>), so the two rules are indistinguishable here and
    /// this one is kept because it is also the bound the clamp needs.
    /// </remarks>
    private int maxLevelOf(WzObject levelNode)
    {
        int max = 0;
        foreach (WzObject lv in Children(levelNode))
            if (int.TryParse(lv.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && n > max)
                max = n;
        return max;
    }

    /// <summary>
    /// Which <c>level/&lt;n&gt;</c> a character of <paramref name="characterLevel"/>
    /// has this skill at: the character's own level, clamped to the skill's own
    /// ceiling.
    /// </summary>
    /// <remarks>
    /// This is the same clamp the damage read uses (<see cref="Plan"/>), so the
    /// box, the damage and the per-level action all resolve at ONE level. The
    /// reference resolves all three from a single
    /// <c>SkillData::get(skillid).get_stats(level)</c> (<c>Skill.cpp:317-319</c>).
    /// </remarks>
    private int skillLevelOf(WzObject levelNode, int characterLevel)
    {
        int max = maxLevelOf(levelNode);
        return max > 0 ? Math.Clamp(characterLevel, 1, max) : 0;
    }

    /// <summary>One entry of a <c>CharLevel/&lt;n&gt;</c> map, by the map's own key.</summary>
    private sealed record CharLevelEntry(string Key, WzObject Node);

    /// <summary>
    /// Which <c>CharLevel/&lt;n&gt;</c> a character of
    /// <paramref name="characterLevel"/> casts with, and null when the map is empty
    /// or the chosen entry has no <paramref name="child"/>.
    /// </summary>
    /// <param name="charLevel">The <c>CharLevel</c> node itself.</param>
    /// <param name="characterLevel">The caster's CHARACTER level, not their skill level.</param>
    /// <param name="child">The node to read off the chosen entry: <c>effect</c> or <c>hit</c>.</param>
    /// <remarks>
    /// <para>
    /// W-089 (C3). The reference's pick, transcribed from
    /// <c>SkillUseEffect.cpp:129-139</c> (<c>ByLevelUseEffect::apply</c>) and
    /// <c>SkillHitEffect.cpp:60-72</c> (<c>ByLevelHitEffect::apply</c>), which are
    /// the same ten lines:
    /// </para>
    /// <code>
    /// uint16_t level = target.get_level();
    /// auto iter = effects.begin();
    /// for (; iter != effects.end() &amp;&amp; level &gt; iter-&gt;first; ++iter) {}
    /// if (iter != effects.begin())
    ///     iter--;
    /// iter-&gt;second.apply(target);
    /// </code>
    /// <para>
    /// That is NOT "the nearest at or below". The walk stops on the first key
    /// <c>&gt;= level</c> and then steps BACK one, so the answer is the largest key
    /// STRICTLY BELOW the level; a level at or below the first key takes the first
    /// key. On 5001001's <c>CharLevel = {10, 15, 20, 25}</c>:
    /// </para>
    /// <list type="bullet">
    /// <item>level 1..10 &rarr; <c>10</c> (the walk never advances, so there is nothing to step back from)</item>
    /// <item>level 15 &rarr; <c>10</c> (it stops on 15, then steps back to 10)</item>
    /// <item>level 20 &rarr; <c>15</c></item>
    /// <item>level 25 &rarr; <c>20</c></item>
    /// <item>level 26+ &rarr; <c>25</c> (the walk runs off the end and steps back from <c>end()</c>)</item>
    /// </list>
    /// <para>
    /// <c>std::map</c> is ordered by the key, so the walk is numeric, not archive
    /// order. MEASURED 2026-10-18: both skills with this node use keys
    /// <c>10,15,20,25</c>, and <c>CharLevel/25</c> carries a DIFFERENT hit
    /// animation than 10/15/20 (5001001: 48x55/65x63/59x63/56x58 versus
    /// 54x61/70x69/65x69/60x64), so picking the wrong rung is visible.
    /// </para>
    /// </remarks>
    private CharLevelEntry? CharLevelPick(WzObject charLevel, int characterLevel, string child)
    {
        var keys = Children(charLevel)
            .Select(k => (Key: int.TryParse(k.Name, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int n) ? n : int.MinValue, Node: k))
            .Where(k => k.Key != int.MinValue)
            .OrderBy(k => k.Key)
            .ToList();
        if (keys.Count == 0) return null;

        // The C++ walk, written out. `while (level > iter->first) ++iter` then one
        // `--iter` unless it never moved, so: the last key STRICTLY BELOW the level,
        // and the first key when the level is at or under it. A level past the last
        // key runs off the end and steps back onto it, which the same expression
        // gives for free.
        var chosen = keys.Where(k => k.Key < characterLevel).LastOrDefault();
        if (chosen.Node is null) chosen = keys[0];

        return Child(chosen.Node, child) is null
            ? null
            : new CharLevelEntry(chosen.Key.ToString(CultureInfo.InvariantCulture), chosen.Node);
    }

    /// <summary>
    /// A node read as an integer, or <paramref name="fallback"/> when it is absent
    /// or not a number.
    /// </summary>
    /// <remarks>
    /// W-084. OrdinalIgnoreCase, because this archive carries lower-case-k names
    /// such as <c>incLUk</c> and an exact-case read silently drops a real value
    /// (TRAPS T-10).
    /// </remarks>
    private int ReadIntNode(WzObject? parent, string name, int fallback)
    {
        if (parent is null) return fallback;
        return Child(parent, name) switch
        {
            WzIntProperty i => i.Value,
            WzShortProperty sh => sh.Value,
            WzLongProperty l => ClampToInt(l.Value),
            // The other three shapes used to arrive unclamped. They are not small by
            // construction: TryReadReal parses a STRING with NumberStyles.Float, and
            // that accepts "NaN", "Infinity" and "1e999" (.NET Core 3.0+ returns
            // Infinity instead of throwing on overflow). So an archive only has to
            // contain the text "2000000000" — no corruption required — to make
            // `(int)Math.Round(r)` produce a loop bound that allocates billions of
            // objects. The Long branch above already clamped; this is the same rule.
            _ => TryReadReal(parent, name, out double r) ? ClampToInt(r) : fallback,
        };
    }

    /// <summary>
    /// A file-supplied number narrowed to <see cref="int"/>, refusing what cannot be one.
    /// </summary>
    /// <remarks>
    /// Everything this narrows comes out of an archive, so "it is a double" is not a
    /// reason to trust it as a count, a radius or a loop bound. NaN and the infinities
    /// become 0 rather than an arbitrary int: (int)double.NaN is undefined in C# and
    /// (int)double.PositiveInfinity saturates to int.MaxValue, which is precisely the
    /// billion-iteration case this exists to stop.
    /// </remarks>
    private static int ClampToInt(double value) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? 0
            : (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);

    /// <summary>
    /// Ceiling on a node the code LOOPS over, measured rather than guessed.
    /// </summary>
    /// <remarks>
    /// MEASURED 2026-10-20 over the real <c>Skill.nx</c> (124,428 nodes, 3,515 of
    /// them <c>mobCount</c>): min 1, median 6, p99 15, <b>max 30</b>. Nothing in the
    /// corpus comes close to this ceiling, and the loop it guards allocates a
    /// <c>SkillCastEvent</c> and an interpolated string per iteration — so without a
    /// bound the cost is set by a number in the file rather than by anything the
    /// caller asked for.
    /// </remarks>
    private const int MaxLoopedCount = 1024;

    /// <summary>
    /// A node read as a number of ANY width, or <c>false</c> when it is absent or
    /// is not a number at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same archive stores the same kind of quantity under different WZ types
    /// from one skill to the next, and a reader that lists only <c>Int</c>,
    /// <c>Short</c> and <c>Long</c> treats a <c>Float</c> or <c>Double</c> as an
    /// ABSENT node rather than as a value of another type — which reads as "this
    /// skill has no damage" and silently deletes it.
    /// </para>
    /// <para>
    /// MEASURED 2026-10-18: <c>5211004</c>'s <c>level/1</c> carries a child named
    /// <c>damage</c> (the endpoint renders it as 73) and the skill still dealt zero,
    /// because its type is not one of the three this reader knew.
    /// </para>
    /// </remarks>
    private bool TryReadReal(WzObject? parent, string name, out double value)
    {
        value = 0;
        if (parent is null) return false;
        WzObject? child = Child(parent, name);
        return child is not null && TryReadReal(child, out value);
    }

    /// <summary>
    /// This node itself read as a number of any width, or <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The same archive stores the same kind of quantity under different WZ types
    /// from one skill to the next, and a reader that lists only <c>Int</c>,
    /// <c>Short</c> and <c>Long</c> treats a <c>Float</c> or <c>Double</c> as an
    /// ABSENT node rather than as a value of another type — which reads as "this
    /// skill has no damage" and silently deletes it.
    /// <para>
    /// MEASURED 2026-10-18: <c>5211004</c>'s <c>level/1</c> carries a child named
    /// <c>damage</c> (the endpoint renders it as 73) and the skill still dealt zero,
    /// because its type is not one of the three this reader knew.
    /// </para>
    /// </remarks>
    private static bool TryReadReal(WzObject node, out double value)
    {
        double read = 0;
        bool ok = node switch
        {
            WzFloatProperty f => Grab(f.Value),
            WzDoubleProperty d => Grab(d.Value),
            WzIntProperty i => Grab(i.Value),
            WzShortProperty sh => Grab(sh.Value),
            WzLongProperty l => Grab(l.Value),
            WzStringProperty s when double.TryParse(s.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed) => Grab(parsed),
            _ => false,
        };
        value = read;
        return ok;

        bool Grab(double v)
        {
            read = v;
            return true;
        }
    }

    // W-089: `ReadRealNode(WzObject?, string, double)` is DELETED. It was the second,
// narrower numeric reader in this file -- Int / Short / Long / Float / Double and
// nothing else, no `String`, no fall-through -- and it was the reader for `range`,
// whose absence changes a hit test. It now goes through TryReadReal, the one
// reader W-087 was written for. Left as a note rather than as code: a second
// numeric reader in the same file is how two copies of a number drifted apart.

    /// <summary>The skills this archive actually carries, sampled per book.</summary>
    public List<SkillCastPlan> Sample(int perBook = 2)
    {
        var result = new List<SkillCastPlan>();
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Skill", null))
            {
                foreach (WzObject book in Children(_session.TryResolve(file.Id)
                    ?? throw new InvalidOperationException("skill archive root missing")))
                {
                    // A book image is `info` plus `skill`, in that order on this
                    // archive -- so "the first child is `skill`" is false for every
                    // book, and that check silently returned nothing at all.
                    // Ask for the node by name instead of inferring its position.
                    if (Child(book, "skill") is not { } skills)
                        continue;
                    int taken = 0;
                    foreach (WzObject skill in Children(skills))
                    {
                        if (taken >= perBook)
                            break;
                        if (!int.TryParse(skill.Name, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out _))
                            continue;
                        taken++;
                        if (Plan(skill.Name) is { } p)
                            result.Add(p);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The skills the reference would put in its list, with the reason the
    /// reference excludes the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SkillForm.cs:85-137</c> is the whole of it, and the filter is the
    /// reference's only notion of "which skills exist":
    /// <list type="bullet">
    /// <item>book names start with a digit and are not <c>0</c>-prefixed, and the book has a <c>skill</c> node (`:88-92`);</item>
    /// <item>skill ids do not start with <c>0</c> (`:99`, `:118`);</item>
    /// <item>it has <c>hit</c> (`:101`, `:120`);</item>
    /// <item>it has a collision box ??<c>level/1/lt</c> on the PreBB branch (`:103`), else <c>common</c> AND <c>common/lt</c> (`:122-124`);</item>
    /// <item>it has <c>effect</c> (`:105`, `:126`).</item>
    /// </list>
    /// </para>
    /// <para>
    /// The PreBB branch is chosen by a GLOBAL test ??<c>Wz.HasNode("Skill/100.img/skill/1000000/level")</c>
    /// (<c>SkillForm.cs:85</c>) ??not by anything about the skill being listed.
    /// It is reproduced verbatim in <see cref="PreBB"/>, including the fact that it
    /// probes skill 1000000 specifically.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The pose a skill puts the character in, or "" when the archive names none.
    /// </summary>
    /// <remarks>
    /// <c>Skill.cs:46-47</c> reads <c>action/0</c> and falls back to "alert".
    /// This returns the archive's own value and leaves the fallback to the caller,
    /// so "the archive says nothing" stays distinguishable from "the archive says
    /// alert".
    /// </remarks>
    private string ActionOf(WzObject entry)
    {
        if (Child(entry, "action") is not { } node)
            return "";
        foreach (WzObject frame in Children(node))
        {
            string v = AsText(frame) ?? "";
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return "";
    }

    /// <summary>
    /// How long the CHARACTER's pose runs, in ms, from the body image's own frames.
    /// </summary>
    /// <param name="action">The pose name, e.g. <c>brandish1</c>.</param>
    /// <returns>0 when the body image carries no such pose.</returns>
    /// <remarks>
    /// <para>
    /// The pose runs on the BODY image's per-frame delays, not on how long the
    /// cast SPRITE lasts:
    /// </para>
    /// <code>
    /// MapleCharacter.cs:392-397  Equip.Data["body/" + State + "/" + Frame + "/delay"] = |delay|
    /// MapleCharacter.cs:1121     BodyDelay = Equip.Data["body/" + State + "/" + Frame + "/delay"]
    /// MapleCharacter.cs:1240-1242 if (Time > BodyDelay) advance
    /// </code>
    /// <para>
    /// MEASURED over the 55 castable skills that declare a pose: the effect and
    /// the pose agree for only 11. The effect is SHORTER for 17 -- which cuts the
    /// animation off mid-play -- and LONGER for 27 -- which leaves the character
    /// frozen in a finished pose and refuses the next cast
    /// (<c>Skill.cs:321</c>, <c>PlayEnded</c>). The ratios run from 0.65x
    /// (meteor) to 1.92x (pyramid).
    /// </para>
    /// <para>
    /// <c>delay</c> is a CHILD of the frame node, not a path segment under it:
    /// <c>Character/00002000.img/stand1/0</c> lists
    /// <c>[arm, body, delay, face]</c>. Asking for <c>/0/delay</c> resolves
    /// nothing and every pose totals zero.
    /// </para>
    /// </remarks>
    public int PoseMs(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return 0;

        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Character", null))
            {
                foreach (string prefix in new[] { "", "Character/" })
                {
                    WzObject? act = null;
                    if (_session.TryResolve(Path(file.Id, prefix, LookService.BodyImage, action)) is { } a)
                        act = a;
                    if (act is null) continue;

                    int total = 0;
                    foreach (WzObject frame in Children(act))
                    {
                        if (!int.TryParse(frame.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                            continue;
                        foreach (WzObject child in Children(frame))
                        {
                            if (!child.Name.Equals("delay", StringComparison.OrdinalIgnoreCase))
                                continue;
                            // MapleCharacter.cs:395 wraps it in Math.Abs -- a negative
                            // delay must not stall the frame clock forever.
                            total += Math.Abs(AsInt(child) ?? 0);
                            break;
                        }
                    }
                    if (total > 0) return total;
                }
            }
        }
        return 0;
    }

    private static int? AsInt(WzObject node) =>
        node switch
        {
            WzIntProperty i => i.Value,
            WzShortProperty s => s.Value,
            WzLongProperty l => (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue),
            WzStringProperty str when int.TryParse(
                str.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };

    /// <summary>
    /// Every skill the reference would LIST, not every skill the archive holds.
    /// </summary>
    public List<SkillListEntry> Castable()
    {
        var result = new List<SkillListEntry>();
        Dictionary<string, string> names = SkillNames();
        bool preBB = PreBB();

        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Skill", null))
            {
                if (_session.TryResolve(file.Id) is not { } root)
                    continue;

                foreach (WzObject book in Children(root))
                {
                    if (book.Name.Length == 0 || !char.IsDigit(book.Name[0])) continue;
                    if (book.Name[0] == '0') continue;
                    if (Child(book, "skill") is not { } skills) continue;

                    foreach (WzObject skill in Children(skills))
                    {
                        if (skill.Name.Length == 0 || skill.Name[0] == '0') continue;
                        if (Child(skill, "hit") is null) continue;
                        if (Child(skill, "effect") is null) continue;

                        // The box test reads a vector off a level/common node; asking
                        // for `lt` as a named child is the whole check, and Corners()
                        // already fails on a node whose lt/rb is not a vector, which is
                        // the reference's own answer for "no box".
                        bool box = preBB
                            ? Child(skill, "level") is { } levels
                              && Child(levels, "1") is { } levelOne
                              && Corners(levelOne) is not null
                            : Child(skill, "common") is { } common && Corners(common) is not null;
                        if (!box) continue;

                        // The icon is `Skill/<book>.img/skill/<id>/icon`
                        // (SkillForm.cs:107) -- note the BOOK between the archive and
                        // `skill`, which is the same shape Plan() resolves the entry at.
                        // The reference reads it straight into a cell and lets a missing
                        // bitmap draw as nothing (:107 has no HasNode guard before it),
                        // so an absent icon yields an empty path rather than an error.
                        string icon = _session.TryResolve(
                            Path(file.Id, book.Name, "skill", skill.Name, "icon")) is null
                            ? ""
                            : $"{file.Id}/{book.Name}/skill/{skill.Name}/icon";
                        names.TryGetValue(skill.Name, out string? name);
                        // The pose the CHARACTER will wear, taken from the same
                        // node the cast reads (Skill.cs:46-47, action/0).
                        //
                        // Carried on the LIST so "does this skill load the body's
                        // own animation" is answerable from the catalogue rather
                        // than only by casting it and reading the frame back.
                        // MEASURED over 101 sampled skills: 51 carry an `action`,
                        // 27 distinct values, and the 12 most common all exist on
                        // Character/00002000.img -- which carries 158 actions.
                        string pose = ActionOf(skill);
                        result.Add(new SkillListEntry(skill.Name, BookId(skill.Name), icon, name ?? "", pose));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Whether this archive is the pre-boss-battle shape, by the reference's own
    /// global probe.
    /// </summary>
    /// <remarks>
    /// <c>SkillForm.cs:85</c> and <c>Skill.cs:212</c> both ask
    /// <c>Wz.HasNode("Skill/100.img/skill/1000000/level")</c> ??one specific skill,
    /// not "does any skill have a level". Kept exactly: probing "any skill" would
    /// answer yes for both lineages and make the two branches indistinguishable,
    /// which is the one thing the test exists to decide.
    /// <para>
    /// The <c>100.img</c> level is part of the probe and not decoration: a skill
    /// sits at <c>&lt;archive&gt;/&lt;book&gt;.img/skill/&lt;id&gt;</c>, so probing
    /// <c>&lt;archive&gt;/skill/1000000/level</c> resolves nothing and answers
    /// <c>false</c> for EVERY archive. Measured before this line was corrected:
    /// <c>preBb=false, count=0</c> ??a clean, confident zero.
    /// </para>
    /// </remarks>
    public bool PreBB()
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Skill", null))
                if (_session.TryResolve(Path(file.Id, "100.img", "skill", "1000000", "level")) is not null)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Skill id to localised name, from every open String archive.
    /// </summary>
    /// <remarks>
    /// <c>SkillForm.cs:109-110</c> and <c>:130-131</c> read
    /// <c>String/Skill.img/&lt;id&gt;/name</c> and fall back to an EMPTY string
    /// when the node is absent ??the row still appears, it just has no name.
    /// Later archives do not overwrite an earlier one, so the first String
    /// archive that knows a name wins.
    /// </remarks>
    private Dictionary<string, string> SkillNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("String", null))
            {
                if (_session.TryResolve(file.Id) is not { } root) continue;
                foreach (WzObject skillImg in Children(root))
                {
                    if (!skillImg.Name.EndsWith("Skill.img", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (WzObject entry in Children(skillImg))
                    {
                        if (names.ContainsKey(entry.Name)) continue;
                        if (Child(entry, "name") is { } n && AsText(n) is { } text)
                            names[entry.Name] = text;
                    }
                }
            }
        }
        return names;
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Build a session path from a file id and slash-separated segments.</summary>
    private static string Path(string fileId, params string[] segments)
    {
        string path = WzPath.Join(fileId);
        foreach (string segment in segments)
            foreach (string part in segment.Split('/', StringSplitOptions.RemoveEmptyEntries))
                path = WzPath.Child(path, part);
        return path;
    }

    private WzObject? Child(WzObject node, string name)
    {
        foreach (WzObject child in Children(node))
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
        return null;
    }

    private List<WzObject> Children(WzObject node)
    {
        var list = new List<WzObject>();
        foreach (WzObject child in _session.EnumerateChildren(node))
            list.Add(child);
        return list;
    }

    private static string? AsText(WzObject node) => node switch
    {
        WzStringProperty s => s.Value,
        WzIntProperty i => i.Value.ToString(CultureInfo.InvariantCulture),
        WzCanvasProperty => node.Name,
        _ => null,
    };

    /// <summary>
    /// <c>lt</c> and <c>rb</c> off a level or common node.
    /// </summary>
    /// <remarks>
    /// A vector is a <see cref="WzVectorProperty"/> whose x and y are properties
    /// on the node, not children ??walking children for them finds nothing, and
    /// that failure is silent and reads exactly like "this skill has no hitbox".
    /// <para>
    /// The elements are NAMED and ordered left, TOP, right, BOTTOM, because the
    /// WZ pair is <c>lt</c> then <c>rb</c> ??i.e. <c>(lx, ly, rx, ry)</c>, the
    /// transpose of the <c>(left, right, top, bottom)</c> a caller usually wants
    /// as a rectangle. An unnamed tuple let two callers destructure it as
    /// <c>(lx, rx, ly, ry)</c>, which compiled as soon as the names were added
    /// and silently swapped every vertical bound.
    /// </para>
    /// </remarks>
    private (int Left, int Top, int Right, int Bottom)? Corners(WzObject node)
    {
        WzObject? lt = Child(node, "lt");
        WzObject? rb = Child(node, "rb");
        if (lt is null || rb is null)
            return null;
        if (!Vector(lt, out int lx, out int ly) || !Vector(rb, out int rx, out int ry))
            return null;
        return (lx, ly, rx, ry);
    }

    /// <summary>
    /// Every frame of one <c>hit/&lt;group&gt;</c> node, read the way the reference's
    /// <c>Animation</c> reads it.
    /// </summary>
    /// <param name="hitBasePath">
    /// The path of the <c>hit</c> NODE ITSELF: <c>&lt;entry&gt;/hit</c> normally,
    /// <c>&lt;entry&gt;/CharLevel/&lt;n&gt;/hit</c> or <c>&lt;entry&gt;/level/&lt;n&gt;/hit</c>
    /// on the two branches the reference takes instead (Skill.cpp:114 and :141).
    /// </param>
    /// <param name="group">The <c>hit/&lt;group&gt;</c> node itself.</param>
    /// <remarks>
    /// <para>
    /// See the call site for the transcription of <c>Animation.cpp:166-193</c>. In
    /// short: the frames are the BITMAP children whose names parse as a
    /// non-negative integer, walked in numeric order, and everything else on the
    /// node (<c>pos</c>, <c>hitAfter</c>) is metadata rather than a frame.
    /// </para>
    /// <para>
    /// <c>Index</c> is the frame's POSITION in that walk ??<c>0,1,2,??/c> with no
    /// gaps ??because that is what the client asks the canvas route for. The
    /// archive's own name is in <c>ImagePath</c>, so the two cannot drift: a group
    /// whose frames are <c>1,2,3,5</c> is served as <c>??hit/0/0</c> ??
    /// <c>??hit/0/3</c> over the images the group really holds
    /// (<c>??hit/0/1</c>, <c>/2</c>, <c>/3</c>, <c>/5</c>), which is what a client
    /// that cannot do the numeric walk itself needs.
    /// </para>
    /// <para>
    /// <c>Pos</c> is read off the GROUP and <c>z</c> off the first FRAME that
    /// carries one, because that is where the archive puts them ??measured over 239
    /// hit groups: 53 have a group-level <c>pos</c> and none has a group-level
    /// <c>z</c>, while 110 frames carry a <c>z</c>. Two groups
    /// (<c>200.img/2001004/hit/0</c> and <c>400.img/4001334/hit/1</c>) disagree
    /// across their own frames (<c>0,0,1</c>); the reference's
    /// <c>effects[z]</c> would receive one z per call, and <c>EffectLayer</c> has
    /// no per-frame z at all, so taking the first frame's is the only shape that
    /// matches. Neither 0 nor 1 changes the layer these draw in, so nothing visible
    /// rests on that choice.
    /// </para>
    /// </remarks>
    private IEnumerable<SkillHitSprite> HitSprites(string hitBasePath, WzObject group)
    {
        string containerPath = $"{hitBasePath}/{group.Name}";

        // Animation.cpp:169-176 ??a node that is ITSELF a bitmap is a one-frame
        // animation, not a container. This archive always wraps, but the branch is
        // the reference's and costs nothing to keep.
        if (group is WzCanvasProperty single)
        {
            return
            [
                new SkillHitSprite(0, 0, single.Name == "" ? containerPath : $"{containerPath}/{single.Name}",
                    containerPath, 1,
                    [OriginOf(single)], [DelayOf(single)], 0, 0)
            ];
        }

        // Animation.cpp:177-190. Numeric names in numeric order; `pos` and
        // `hitAfter` are not frames and `or_default` drops anything unparseable.
        var frames = new List<WzObject>();
        foreach (WzObject child in Children(group))
            if (child is WzCanvasProperty
                && int.TryParse(child.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && id >= 0)
                frames.Add(child);
        frames = frames.OrderBy(f => int.TryParse(f.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ord) ? ord : int.MaxValue).ToList();

        if (frames.Count == 0)
            return Array.Empty<SkillHitSprite>();

        // `pos` is the GROUP's, per SkillHitEffect.h:43 ??`pos = src["pos"]`, where
        // `src` is the node Effect was built from. Absent is 0, which Mob.cpp:1545
        // reads as "the mob's head", and that is the reference's own default: `nl::node`
        // yields 0 for a name it does not have.
        int pos = ReadByte(group, "pos");

        // `z` is a FRAME's in this archive. Same reader, same fallback, no
        // speculation about a group-level one (there is none -- measured above).
        int z = frames.Select(f => ReadByte(f, "z")).FirstOrDefault(v => v != 0);

        var origins = new List<PointDto>(frames.Count);
        var delays = new List<int>(frames.Count);
        foreach (WzObject frameNode in frames)
        {
            // The SAME two readers the cast sprite uses, deliberately.
            origins.Add(OriginOf(frameNode));
            delays.Add(DelayOf(frameNode));
        }

        // Every frame of one animation carries the WHOLE list, the way the one
        // cast sprite carries its own: the client walks all of them on any frame it
        // draws, so a per-frame list of one would make the animation hold on its
        // first image. Built only once both lists are complete, so no sprite can
        // observe a list still being appended to.
        var sprites = new List<SkillHitSprite>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
            sprites.Add(new SkillHitSprite(0, i, $"{containerPath}/{frames[i].Name}",
                containerPath, frames.Count, origins, delays, pos, z));
        return sprites;
    }

    /// <summary>
    /// Every frame of one <c>tile/&lt;variant&gt;</c> node, read by the SAME
    /// reader <see cref="HitSprites"/> uses for <c>hit/&lt;group&gt;</c>.
    /// </summary>
    /// <param name="entryPath">The skill entry's own path.</param>
    /// <param name="variant">The <c>tile/&lt;n&gt;</c> node itself.</param>
    /// <param name="index">That node's own name as a number ??the variant index.</param>
    /// <remarks>
    /// <para>
    /// <c>SkillUseEffect.cpp:168-173</c> stores the whole node:
    /// <c>tile_animations.emplace_back(variant)</c>, and the C++ <c>Animation</c>
    /// constructor (<c>Animation.cpp:166-193</c>) then reads the bitmaps off it.
    /// A tile variant IS an <c>Animation</c>, so it is read as one ??the same
    /// numeric-order walk, the same per-frame <c>origin</c>/<c>delay</c> readers
    /// (<see cref="OriginOf"/>, <see cref="DelayOf"/>).
    /// </para>
    /// <para>
    /// Sharing those readers is not a tidiness choice. <c>W-076</c> exists
    /// because a SECOND reader produced a different number for the same node: a
    /// private animation-length reader here once reported 900ms where the
    /// archive says 1080ms.
    /// </para>
    /// <para>
    /// One row per frame, each carrying the WHOLE origin/delay list, exactly as
    /// <see cref="HitSprites"/> emits its rows: the client walks all of them on
    /// any frame it draws.
    /// </para>
    /// </remarks>
    private IEnumerable<SkillTileSprite> TileSprites(string entryPath, WzObject variant, int index)
    {
        string containerPath = $"{entryPath}/tile/{variant.Name}";

        // Animation.cpp:169-176 ??a node that is ITSELF a bitmap is a one-frame
        // animation. The constructor's `!variant["0"]` guard means that case
        // cannot reach here for a subproperty, but the branch is the reference's.
        if (variant is WzCanvasProperty single)
        {
            return
            [
                new SkillTileSprite(index, single.Name == "" ? containerPath : $"{containerPath}/{single.Name}",
                    containerPath, 1, [OriginOf(single)], [DelayOf(single)])
            ];
        }

        // Animation.cpp:177-190. Bitmaps only, numeric names only, numeric order.
        var frames = new List<WzObject>();
        foreach (WzObject child in Children(variant))
            if (child is WzCanvasProperty
                && int.TryParse(child.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && id >= 0)
                frames.Add(child);
        frames = frames.OrderBy(f => int.TryParse(f.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ord) ? ord : int.MaxValue).ToList();

        if (frames.Count == 0)
            return Array.Empty<SkillTileSprite>();

        var origins = new List<PointDto>(frames.Count);
        var delays = new List<int>(frames.Count);
        foreach (WzObject frameNode in frames)
        {
            origins.Add(OriginOf(frameNode));
            delays.Add(DelayOf(frameNode));
        }

        // ONE entry per VARIANT, not per frame.
        //
        // `tile_animations` is a list of variant NODES (`emplace_back(variant)`,
        // SkillUseEffect.cpp:173) and the roll is `rand() % tile_animations.size()`
        // (SkillUseEffect.cpp:254) — so the bound is a VARIANT count. Emitting one
        // row per frame made `size` the total frame count (MEASURED: 12111003
        // reported 224 variants for 10 variant nodes), which made every decal
        // roughly 22x more likely than the reference draws it and let the roll
        // land outside the variant list entirely.
        return
        [
            new SkillTileSprite(index, $"{containerPath}/{frames[0].Name}",
                containerPath, frames.Count, origins, delays)
        ];
    }

    /// <summary>
    /// A one-byte node as the reference reads it: <c>int8_t</c>, 0 when absent.
    /// </summary>
    /// <remarks>
    /// <c>SkillHitEffect.h:40-45</c> assigns both <c>pos</c> and <c>z</c> from an
    /// <c>nl::node</c> into an <c>int8_t</c>. An absent name yields 0, so 0 here is
    /// the reference's own answer rather than a default invented to fill a gap ??
    /// and for <c>z</c> it is also the in-front half of the layer.
    /// <c>pos == 0</c> is not "no shift": <c>Mob.cpp:1544-1547</c> makes it the
    /// mob's HEAD, which is where a hit effect belongs.
    /// </remarks>
    private int ReadByte(WzObject node, string name)
    {
        return Child(node, name) switch
        {
            WzIntProperty i => i.Value,
            WzLongProperty l => (int)Math.Clamp(l.Value, -128L, 127L),
            WzShortProperty s => s.Value,
            WzStringProperty str when int.TryParse(str.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>
    /// A canvas frame's own <c>origin</c>, (0,0) when it carries none.
    /// </summary>
    /// <remarks>
    /// W-075. Every canvas in this archive carries one ??measured on 1121008,
    /// <c>effect/0/0</c> is 66x37 at origin 30,23 and <c>effect/0/1</c> is 142x95 at
    /// origin 49,81 ??and a canvas is drawn with its origin on the anchor, not
    /// its corner. Reading it once here means the client subtracts a number the
    /// archive wrote instead of inventing one.
    /// </remarks>
    /// <summary>
    /// A canvas frame's own <c>delay</c>, in ms.
    /// </summary>
    /// <remarks>
    /// W-076. <c>Animation.cpp:64-67</c>: <c>delay = src["delay"]; if (delay == 0)
    /// delay = 100;</c> ??the 100 is the reference's own fallback and is kept here
    /// rather than being an invented number.
    /// </remarks>
    private int DelayOf(WzObject frameNode)
    {
        WzObject? delay = Child(frameNode, "delay");
        int value = delay switch
        {
            WzIntProperty i => i.Value,
            WzLongProperty l => (int)Math.Clamp(l.Value, 0L, int.MaxValue),
            WzShortProperty s => s.Value,
            WzStringProperty str when int.TryParse(str.Value, out int parsed) => parsed,
            _ => 0,
        };
        return value == 0 ? 100 : value;
    }

    private PointDto OriginOf(WzObject frameNode)
    {
        return Child(frameNode, "origin") is WzVectorProperty v
            ? new PointDto { X = v.Pos.X, Y = v.Pos.Y }
            : new PointDto();
    }

    private static bool Vector(WzObject node, out int x, out int y)
    {
        if (node is WzVectorProperty v)
        {
            System.Drawing.Point p = v.Pos;
            x = p.X;
            y = p.Y;
            return true;
        }
        x = 0;
        y = 0;
        return false;
    }
}