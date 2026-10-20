namespace MapleBench.Services.Simulate;

/// <summary>
/// How much a swing takes off, given that the simulator has no character sheet.
/// </summary>
/// <remarks>
/// <para>
/// The reference's formula is <c>Mob::calculate_mindamage</c> /
/// <c>calculate_maxdamage</c> (Mob.cpp:1590-1601):
/// </para>
/// <code>
/// mindamage = damage * (1 - 0.01 * leveldelta) - defense * 0.6
/// maxdamage = damage * (1 - 0.01 * leveldelta) - defense * 0.5
/// </code>
/// <para>
/// Kept: the <c>(1 - 0.01 * leveldelta)</c> scaling, both 1.0 floors, both defence
/// coefficients, and the <c>DAMAGECAP</c> of 999999 from Mob.cpp:1770-1790.
/// </para>
/// <para>
/// Now read, because the archive turned out to carry them after all:
/// </para>
/// <list type="bullet">
/// <item><b>the mob's defence.</b> <c>info/PDDamage</c> and <c>info/MDDamage</c>,
/// which the open Mob.nx carries on 342 of 343 sampled mobs (measured 2026-10-16).
/// An earlier revision of this file claimed the node was <c>info/wdef</c> — a node
/// that does not exist on this archive at all, which is why the defence term was
/// dropped. The C++ reads the same two nodes, Mob.cpp:62-63.</item>
/// <item><b>the attacker's own damage.</b> A bare swing is now
/// <c>PlayerStatus.MinDamage/MaxDamage</c>, the reference's beginner formula
/// (CharStats.cpp:118, :177) fed by the equipped weapon's own <c>info/incPAD</c>.
/// </item>
/// </list>
/// <para>
/// Still dropped, and why — each reads a stat this character has no source for:
/// </para>
/// <list type="number">
/// <item>accuracy vs <c>eva</c> — both are in the archive
/// (<c>info/acc</c>, <c>info/eva</c>, 342/343) and the player now HAS an accuracy,
/// but the reach test that decides whether a swing reaches a mob is already
/// geometric. Adding a probability gate on top would make the swing the reference's
/// and the MISS the reference's — and the reference reports a miss as damage -1
/// through a separate path (Player.cpp:1665-1668) this simulator does not have.</item>
/// <item>critical rate and its damage bonus — reads the player's
/// <c>critical</c> and <c>critical_damage</c>. The base rate is 0.05
/// (CharStats.cpp:59) but the bonus is a dead zero (see below), and a crit that
/// lands 150% would need the on-screen crit art to be honest.</item>
/// <item>elemental attack and defence — <c>adjust_element_damage</c>, entirely
/// driven by the player's charged element.</item>
/// <item>skill level and multipliers — the reference rolls each skill's
/// <c>damage</c> property; a bare swing is skill 0 with no such property.</item>
/// </list>
/// <para>
/// <c>Attack::ignoredef</c> is not on this list because it was never really
/// missing: the C++ WRITES it (Player.cpp:1429) and reads it only in the stat
/// window (UIStatsInfo.cpp:343). <c>Mob::calculate_mindamage</c> never references
/// it, so Ignore Defense has no effect on any damage that client computes. There
/// is nothing here to port.
/// </para>
/// </remarks>
public sealed class DamageRoll
{
    /// <summary>
    /// The reference's cap, verbatim: <c>Mob.cpp:1770</c>
    /// <c>constexpr double DAMAGECAP = 999999.0;</c>.
    /// </summary>
    public const int DamageCap = 999999;

    /// <summary>
    /// Roll one swing's damage.
    /// </summary>
    /// <param name="random">Seeded from the session id, so a session replays.</param>
    /// <param name="playerLevel">
    /// The attacker's level. The simulator's character has none, so this is
    /// 1 — the level every map here starts at.
    /// </param>
    /// <param name="mobLevel">The mob's level, read from its archive.</param>
    /// <param name="mobDefence">
    /// The mob's <c>info/PDDamage</c>. 0 when the archive does not say, which
    /// makes the defence term vanish rather than become a penalty — an unknown
    /// defence must not silently over-reduce every swing.
    /// </param>
    /// <param name="minDamage">The attacker's own minimum, from the sheet.</param>
    /// <param name="maxDamage">The attacker's own maximum, from the sheet.</param>
    public static int Roll(Random random, int playerLevel, int mobLevel,
        int mobDefence, int minDamage, int maxDamage)
    {
        // Mob.cpp:1649-1652: leveldelta = max(0, mobLevel - playerLevel). A mob BELOW
        // the player is level-equal to the formula, not easier — there is no
        // over-level bonus.
        int leveldelta = Math.Max(0, mobLevel - Math.Max(1, playerLevel));
        double scale = 1.0 - 0.01 * leveldelta;

        // Mob.cpp:1590-1601. min subtracts 0.6 of the defence, max subtracts 0.5,
        // so the two lines converge as defence rises rather than crossing.
        // The 1.0 floor is inside the reference, not this project's mercy.
        double min = Math.Max(1.0, minDamage * scale - mobDefence * 0.6);
        double max = Math.Max(1.0, maxDamage * scale - mobDefence * 0.5);
        if (max < min) max = min;

        // Mob.cpp:1772 picks next_real(min, max); :1792 truncates to int32.
        double value = min + random.NextDouble() * (max - min);
        return Math.Clamp((int)value, 1, DamageCap);
    }
}