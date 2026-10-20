namespace MapleBench.Services.Simulate;

/// <summary>
/// The two numbers the simulator fixes so a test result can be read at a glance.
/// </summary>
/// <remarks>
/// <para>
/// DECIDED 2026-10-17 by the user, in the spirit of ADR-0005 (the simulator is a
/// test rig, not a client clone): a hit always removes 100 HP and every mob
/// carries 1000, so ten hits kill anything and one hit is always visible on the
/// HP bar. Before this, damage went through <c>DamageRoll.Roll</c> — level
/// scaling, the mob's <c>info/PDDamage</c> defence term and two floors — which is
/// the reference's formula but produced numbers nobody could check by eye, and
/// read <c>info/maxHP</c> per mob, so a 3-hit mob and a 40-hit mob sat side by
/// side in the same scenario.
/// </para>
/// <para>
/// The reference's formula is still implemented and still reachable
/// (<c>DamageRoll.Roll</c>); it is simply not what the simulator reports.
/// </para>
/// </remarks>
public static class SimTuning
{
	/// <summary>Damage of one player hit — a swing or a skill — against any mob.</summary>
	public const int PlayerDamage = 100;

	/// <summary>Hit points every mob spawns with, whatever its archive <c>info/maxHP</c> says.</summary>
	public const int MobHp = 1000;

	/// <summary>
	/// The character's own hit points, for the same reason <see cref="MobHp"/> is
	/// what it is: so a fight is readable.
	/// </summary>
	/// <remarks>
	/// W-080a, 2026-10-17. This was the reference's 50, and at 50 the character
	/// died to one touch from every mob on the map — a summoned level-20 hit for
	/// 150 — so a mob's mere presence made the simulator unusable, because
	/// <c>CastSkill</c> refuses on <c>Status.Dead</c>. Same defect as ADR-0006 saw
	/// on the mob's side, one line of the fight away.
	/// </remarks>
	public const int PlayerHp = 1000;
}
