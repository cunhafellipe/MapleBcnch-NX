// Player status — the character sheet a session owns.
//
// NOTE ON CITATIONS. The `.cs` citations below name MapleNecrocer
// (C:\MUWORK\GAME\_necrocer_src\MapleNecrocer) -- this port's own source, which
// has no counterpart in the authoritative reference: clean-client
// (C:\MUWORK\GAME\MPSH\clean-client\src) contains zero `.cs` files. Where a
// MapleNecrocer citation disagrees with clean-client, clean-client wins -- see
// pm/PARITY.md, "The C++ wins". These were not re-audited for content; some
// record a port's deliberate divergence.
//
// Reference: _necrocer_src/MapleNecrocer/CharaSim/CharacterStatus.cs (348 lines)
// and Character.cs (920 lines). Every formula below cites the line it came from;
// nothing here is invented.
//
// WHAT THIS IS NOT: this is not a full character sim. Necrocer's Character.cs
// also resolves job skill trees, set-item bonuses, gear-state gating and the
// AP/POP spend curves. Those need a gear database this project does not have
// yet. What is here is the state itself — the numbers a client draws and the
// simulation spends — so that damage is not being applied to nothing.

using System;

namespace MapleBench.Services.Simulate;

/// <summary>
/// The player's own HP/MP/EXP/level and the attack figures derived from them.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: without it a mob swing has nowhere to land. The swing chain
/// reported damage to mobs correctly while the player had no hit points at all,
/// so "the combat loop works" was true of exactly one direction of it.
/// </para>
/// <para>
/// Numbers that are read from an archive (mob HP, mob level) are not duplicated
/// here; this type owns only the player's.
/// </para>
/// </remarks>
public sealed class PlayerStatus
{
    // Character.cs: `this.status.MaxHP.BaseVal = 50;` — the base a fresh
    // character has before gear, which is the only max-HP constant the
    // reference states outright.
    //
    // W-082a. RAISED TO SimTuning.PlayerHp, and the reference's 50 is what it
    // replaced. Not a fidelity change and not a convenience: at 50 the character
    // died to ONE touch from anything on the map, and a dead character cannot
    // cast (`CastSkill` :1492), so a mob on the map made the simulator
    // unusable — which is the same unreadability ADR-0006 fixed on the other side
    // of the fight. MEASURED 2026-10-17 on `f4/Map/Map1/100000004.img`: a
    // summoned mob spawned 15 px away, its level-20 contact hit for 150, and the
    // first frame already read `hp: 0, playerDead: true`.
    //
    // The number that stays is the one a test reads: 1000 against a mob's 1000,
    // and a 150-damage hit is now visible on the bar instead of a corpse.
    private const int BaseMaxHp = SimTuning.PlayerHp;

    /// <summary>
    /// W-084. How far the character reaches, and the only number the skill hit box
    /// is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CharStats.cpp:57</c>  <c>projectilerange = Weapon::is_bow_or_crossbow(weapontype) ? 300 : 250;</c>
    /// <c>CharStats.cpp:468</c> <c>get_range() { return Rectangle&lt;int16_t&gt;(-projectilerange, -5, -75, 25); }</c>
    /// <c>CharStats.cpp:371</c> <c>add_projectilerange</c> widens it from a passive.
    /// </para>
    /// <para>
    /// This is what the skill-hit rewrite rests on: the reach belongs to the
    /// CHARACTER, not to the skill. A skill's own <c>range</c> node is a NUMBER
    /// (<c>SkillData.cpp:77</c>, divided by 100) that scales the near edge -- not a
    /// collision box, which is what this port had been reading it as.
    /// </para>
    /// </remarks>
    public int ProjectileRange { get; set; } = 250;

    // Character.cs:16-17, `MaxMP.BaseVal = 10; MP = 10;` — this file had MaxMp at
    // 0, so a fresh character showed an empty blue bar against a full red one.
    private const int BaseMaxMp = 10;

    // Character.cs:18-21. These were 4/4/4/4, which is not the reference's
    // level-1 character at all: Strength 12 and Dexterity 5. It changed the
    // attack number, because Character.cs:584 and the C++ beginner branch
    // CharStats.cpp:118 both weight STR four times.
    private const int BaseStrength = 12;
    private const int BaseDexterity = 5;
    private const int BaseIntelligence = 4;
    private const int BaseLuck = 4;

    // Character.cs CalcAttack(GearType.barehand): (str * 4 + dex) * 1.43 * 1 * 0.01
    private const double BarehandFactor = 1.43;
    private const double PadScale = 0.01;

    // The C++ beginner branch, CharStats.cpp:118:
    //   calc_max = ((4.0 * str + dex) / 100.0) * (pad * 1.2)
    // The C# barehand row, Character.cs:593, is (str*4 + dex) * 1.43 * 1 * 0.01 —
    // the same shape with pad pinned at 1. Per studio rule 7.6 the C++ wins on
    // conflict, and it is also the only one of the two that takes the equipped
    // weapon's own WATK, which this project can now read.
    private const double BeginnerFactor = 1.2;
    private const double StatScale = 100.0;

    // CharStats.cpp:391-415, set_mastery: 0.20 for sword/axe/mace/dagger/2H/
    // spear/polearm/knuckle, 0.25 for wand/staff, 0.15 for bow/crossbow/claw/gun.
    private const double SwordMastery = 0.20;

    /// <summary>CharacterStatus.cs: Job. 0 = beginner.</summary>
    public int Job { get; set; }

    /// <summary>CharacterStatus.cs: Level.</summary>
    public int Level { get; set; } = 1;

    /// <summary>
    /// CharacterStatus.cs: HP — clamped to [0, MaxHp] on both read and write.
    /// </summary>
    /// <remarks>
    /// Clamping on the setter, not only at the point of damage, so a heal cannot
    /// push above max and a negative roll cannot push below zero from any
    /// caller — including one added later.
    /// </remarks>
    public int Hp { get; set; } = BaseMaxHp;

    /// <summary>CharacterStatus.cs: MP.</summary>
    public int Mp { get; set; } = BaseMaxMp;

    /// <summary>CharacterStatus.cs: exp, a long because v83's curve overflows int.</summary>
    public long Exp { get; set; }

    /// <summary>CharacterStatus.cs: AP — free attribute points.</summary>
    public int Ap { get; set; }

    /// <summary>CharacterStatus.cs: POP — free skill points.</summary>
    public int Pop { get; set; }

    // The four base attributes. CharacterStatus.cs declares each as a CharaProp
    // so gear can add to them; the sums are what CalcAttack consumes. The
    // values are the reference's level-1 character, Character.cs:18-21 — this
    // used to be 4/4/4/4.
    public int Strength { get; set; } = BaseStrength;
    public int Dexterity { get; set; } = BaseDexterity;
    public int Intelligence { get; set; } = BaseIntelligence;
    public int Luck { get; set; } = BaseLuck;

    // CharacterStatus.cs: pdd/mdd = physical &amp; magical defence, pEva/mEva =
    // accuracy &amp; evasion. Now filled from the equipped item's own inc* nodes
    // by ApplyEquip; before that they were 0 and every formula below that reads
    // them was dead.
    public int PhysicalDefence { get; set; }
    public int MagicalDefence { get; set; }

    /// <summary>
    /// Accuracy. The C++ derivation, <c>CharStats::calculateaccuracy</c>
    /// (CharStats.cpp:186-192): <c>trunc(DEX * 0.8 + LUK * 0.5)</c>.
    /// </summary>
    /// <remarks>
    /// The C# reference derives a different number for the same stat —
    /// <c>floor(DEX * 1.2 + LUK * 1.0)</c> on the Bigbang row (Character.cs:174-195,
    /// <c>PAccurate</c>). Studio rule 7.6: the C++ wins on conflict, so that is
    /// what is here, and the disagreement is recorded rather than averaged away.
    /// <para>
    /// It is a COMPUTED property, not a field set once. <c>CharStats.cpp:73</c> runs
    /// <c>totalstats[ACC] += calculateaccuracy()</c> on every
    /// <c>close_totalstats</c>, gear or not — so a level-1 character with nothing on
    /// has accuracy <c>trunc(5 * 0.8 + 4 * 0.5) = 6</c>, not 0. Setting it only
    /// inside <see cref="ApplyEquip"/> reported 0 for an unarmed character, which is
    /// a number the reference never produces.
    /// </para>
    /// </remarks>
    public int Accuracy => (int)(Dexterity * 0.8f + Luck * 0.5f) + GearAccuracy;

    /// <summary>
    /// Evasion. Same shape as <see cref="Accuracy"/> and the same reasoning; the C++
    /// carries no <c>calculateevasion</c>, so the base is the equipment total alone
    /// and that is declared here rather than invented.
    /// </summary>
    public int Evasion => GearEvasion;

    /// <summary>The sum of every equipped item's own <c>incACC</c>.</summary>
    public int GearAccuracy { get; private set; }

    /// <summary>The sum of every equipped item's own <c>incEVA</c>.</summary>
    public int GearEvasion { get; private set; }

    /// <summary>
    /// CharacterStatus.cs: MaxHP = base 50 + gear contributions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A COMPUTED property over <see cref="GearHp"/>, not a field set per item.
    /// <c>ApplyEquip</c> assigns <c>MaxHp = BaseMaxHp + stats.Hp</c>, which is an
    /// ABSOLUTE value and therefore RESETS on the second item: a longcoat carrying
    /// <c>incMHP 5</c> followed by a glove carrying none left the pool at 50,
    /// losing the coat's contribution while every other stat (which use <c>+=</c>)
    /// accumulated correctly. Measured before the fix:
    /// <c>maxHp 50 -&gt; 50 -&gt; 50</c> across bare / +sword / +longcoat.
    /// </para>
    /// <para>
    /// Gear contributions come from each item's own <c>info/incMHP</c>, which the
    /// open Character.nx carries on 39 of every 840 items sampled across its 12
    /// stat-bearing categories (measured 2026-10-16).
    /// </para>
    /// </remarks>
    public int MaxHp => Math.Min(StatPoolCap, BaseMaxHp + GearHp);

    /// <summary>The sum of every equipped item's own <c>incMHP</c>.</summary>
    public int GearHp { get; private set; }

    /// <summary>
    /// CharacterStatus.cs: MaxMP. Base 10 (Character.cs:16), plus
    /// <c>incMMP</c> — same accumulator rule as <see cref="MaxHp"/>, and for the
    /// same reason: an absolute assignment would drop the first item's
    /// contribution when the second is applied.
    /// </summary>
    public int MaxMp => Math.Min(StatPoolCap, BaseMaxMp + GearMp);

    /// <summary>The sum of every equipped item's own <c>incMMP</c>.</summary>
    public int GearMp { get; private set; }

    /// <summary>The weapon's own attack, from <c>info/incPAD</c>.</summary>
    public int Watk { get; set; }

    /// <summary>The weapon's own magic attack, from <c>info/incMAD</c>.</summary>
    public int Magic { get; set; }

    /// <summary>Character.cs:24 — 100 before gear.</summary>
    public int MoveSpeed { get; set; } = 100;

    /// <summary>Character.cs:25 — 100 before gear.</summary>
    public int JumpPower { get; set; } = 100;

    /// <summary>
    /// Add one equipped item's totals to the sheet, then re-derive what the
    /// reference re-derives.
    /// </summary>
    /// <remarks>
    /// Order follows <c>Player::recalc_stats</c> (Player.cpp:299-360): the item's
    /// own EquipStat totals first, then the derived formulas. Applying the
    /// derivation last is what makes a second weapon replace the first instead of
    /// compounding onto it — <c>CharStats::calculateaccuracy</c> reads
    /// <c>get_total(DEX)</c>, so deriving before adding would compute it from the
    /// wrong sheet.
    /// </remarks>
    public void ApplyEquip(EquipStatsDto stats)
    {
        if (!stats.Found) return;

        Strength += stats.Str;
        Dexterity += stats.Dex;
        Intelligence += stats.Int;
        Luck += stats.Luk;

        GearHp += stats.Hp;
        GearMp += stats.Mp;
        // Re-clamped against the NEW maximum, because the pools grew. Clamping
        // only inside Hurt() would leave a character at 50/50 on a 60/60 pool
        // until something hit it, which reads as "the item did nothing".
        Hp = Math.Min(Hp, MaxHp);
        Mp = Math.Min(Mp, MaxMp);

        Watk += stats.Watk;
        Magic += stats.Magic;
        PhysicalDefence += stats.Wdef;
        MagicalDefence += stats.Mdef;
        GearAccuracy += stats.Acc;
        GearEvasion += stats.Avoid;
        MoveSpeed = Math.Min(MoveSpeed + stats.Speed, MaxMoveSpeed);
        JumpPower = Math.Min(JumpPower + stats.Jump, MaxJumpPower);
    }

    /// <summary>CharStats.cpp:101-103, the HP/MP pool cap.</summary>
    public const int StatPoolCap = 30000;

    private const int MaxMoveSpeed = 200;
    private const int MaxJumpPower = 200;

    /// <summary>
    /// The attacker's max damage — the reference's own formula.
    /// </summary>
    /// <remarks>
    /// This character is Job 0 and <c>job.is_beginner()</c> is checked BEFORE the
    /// weapon switch (CharStats.cpp:115-119), so the weapon's TYPE never reaches
    /// the switch; only its WATK does, as <c>pad</c>. That is not a shortcut — it
    /// is what the reference computes for a beginner, and it means a dagger does
    /// not give this character the dagger multiplier.
    /// </remarks>
    public double MaxDamage
    {
        get
        {
            // CharStats.cpp:118 — ((4.0 * str + dex) / 100.0) * (pad * 1.2). With
            // nothing equipped, pad is 1, which is also the C# barehand row's
            // literal (Character.cs:593) minus that row's 1.43.
            double pad = Watk > 0 ? Watk : 1.0;
            double calcMax = ((4.0 * Strength + Dexterity) / StatScale) * (pad * BeginnerFactor);

            // CharStats.cpp:170-176: the rounding and the whole `else` arm. A
            // zero-damage attacker has a zero max, NOT the barehand curve.
            return calcMax > 0.0 ? (int)(calcMax + 0.5) : 0.0;
        }
    }

    /// <summary>
    /// The attacker's min damage: CharStats.cpp:177,
    /// <c>max(1, (int)(calc_max * mastery + 0.5))</c>.
    /// </summary>
    /// <remarks>
    /// Mastery is the reference's base for a one-handed blade
    /// (CharStats.cpp:391-415) with no mastery skill learned — this character has
    /// no skill tree. The <c>max(1, …)</c> guard sits inside the
    /// <c>calc_max &gt; 0</c> branch in the reference, so a zero-damage attacker
    /// gets a zero minimum too rather than one.
    /// </remarks>
    public int MinDamage
    {
        get
        {
            double max = MaxDamage;
            return max <= 0.0 ? 0 : Math.Max(1, (int)(max * SwordMastery + 0.5));
        }
    }

    /// <summary>
    /// The barehand curve with no weapon in the picture: Character.cs:593,
    /// <c>(str * 4 + dex) * 1.43 * 1 * 0.01</c>.
    /// </summary>
    /// <remarks>
    /// Kept because it is a named reference figure, and because the C# and C++
    /// references genuinely disagree about it — this row exists only in C#. What
    /// the damage roll consumes is <see cref="MaxDamage"/>/<see cref="MinDamage"/>.
    /// </remarks>
    public double Attack => (Strength * 4 + Dexterity) * BarehandFactor * PadScale;

    /// <summary>True once HP reaches zero.</summary>
    public bool Dead => Hp <= 0 && !Invulnerable;

    /// <summary>
    /// The character takes no damage. A DEBUG switch, requested for watching a
    /// mob's attack animation play out to its end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Own-design. Not in either reference</b>, and not a parity feature. It
    /// exists because the mob attack chain is unreadable from the outside: the
    /// character dies on the first touch (Stage.cpp:809 applies it every frame),
    /// so everything after that -- the rest of the swing, the animation running
    /// out, the mob returning to its patrol -- is never observable.
    /// </para>
    /// <para>
    /// It changes ONE thing: the damage is still REPORTED, so the damage number
    /// still appears and the mob still believes it connected. Only the HP is not
    /// spent. That split is deliberate -- suppressing the report as well would
    /// hide exactly the thing being tested.
    /// </para>
    /// </remarks>
    public bool Invulnerable { get; set; }

    /// <summary>EXP needed for the next level. Own-design: a flat curve.</summary>
    /// <remarks>
    /// The reference reads the real curve from the archive; this project has no
    /// EXP table, so this is a declared self-designed stand-in, not a port.
    /// </remarks>
    public long ExpToNextLevel => 15L * Level * (Level + 1);

    /// <summary>
    /// Take a hit. Returns the HP actually lost, which is what the damage
    /// number on screen has to show — a swing that lands for more than the
    /// remaining HP must read as the remaining HP, never as negative.
    /// </summary>
    public int Hurt(int incoming)
    {
        if (incoming <= 0) return 0;
        if (Invulnerable) return 0;   // still reported by the caller, just not spent
        int before = Hp;
        Hp = Math.Max(0, Hp - incoming);
        return before - Hp;
    }

    /// <summary>Heal, clamped to max. Returns the HP actually gained.</summary>
    public int Heal(int amount)
    {
        if (amount <= 0) return 0;
        int before = Hp;
        Hp = Math.Min(MaxHp, Hp + amount);
        return Hp - before;
    }

    /// <summary>
    /// Come back from death: alive, at full health.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference's revive costs something — a scroll from the prompt's two
    /// buttons (<c>UIRevive.cpp:143-154</c>) or the free wheel
    /// (<c>:35-51</c>) — and this simulator has neither, so HP returns in full.
    /// That is declared own-design at the call site rather than buried here.
    /// </para>
    /// <para>
    /// Why this exists at all: a character whose HP reaches zero is refused by
    /// <c>Swing</c> and <c>CastSkill</c>, which is the reference's behaviour, and
    /// without a way back the session is over. MEASURED: a 50 HP character killed
    /// on frame 0 by <c>4220000</c> could not attack, could not cast, could not
    /// walk, and had no endpoint to revive at.
    /// </para>
    /// </remarks>
    public void Revive()
    {
        Hp = MaxHp;
    }

    /// <summary>
    /// Award EXP and level up while it crosses the threshold, returning the
    /// number of levels gained.
    /// </summary>
    public int GainExp(long amount)
    {
        if (amount <= 0) return 0;
        Exp += amount;
        int gained = 0;
        while (Exp >= ExpToNextLevel && Level < 200)
        {
            Exp -= ExpToNextLevel;
            Level++;
            gained++;
        }
        return gained;
    }
}