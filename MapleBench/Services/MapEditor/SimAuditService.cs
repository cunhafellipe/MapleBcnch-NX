using System.Globalization;
using MapleBench.Models;
using MapleBench.Services.Simulate;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// One thing the simulation measured about itself, with the numbers behind it.
/// </summary>
/// <remarks>
/// Findings are NOT failures. A mob with no attack art is a fact about the
/// archive; a hit pose whose draw origin differs from <c>stand</c> is a fact
/// about the archive. Both were reported to the user as bugs before anything
/// measured them, because nothing in the product said which was which. An audit
/// that only reported breakage would not have helped — it had to report
/// <em>either way</em>, with the numbers.
/// </remarks>
public sealed class SimFinding
{
    /// <summary>A stable, greppable identifier. The gate matches on these.</summary>
    public string Code { get; set; } = "";

    /// <summary>"info" | "warn" | "error".</summary>
    public string Severity { get; set; } = "info";

    /// <summary>One sentence a person can act on.</summary>
    public string Text { get; set; } = "";

    /// <summary>The measurements the sentence is made of.</summary>
    public Dictionary<string, object?> Data { get; set; } = new();
}

/// <summary>
/// A standing audit of one live session: what the simulation measured, whether
/// or not anything is broken.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than more probes.</b> Three reports in one evening
/// — mobs do not attack, coordinates shift when hit, and the attack animation
/// never plays — each needed a separate hand-written harness, and two of those
/// harnesses first reported the opposite of the truth because they were unarmed
/// (see <c>TRAPS.md</c> T-10 and W-044's Result). The answer was knowable the
/// whole time; it was not <em>on the product</em>, so every question cost a new
/// script and a new chance to be wrong.
/// </para>
/// <para>
/// <b>What it measures, and the reference behind each:</b>
/// </para>
/// <list type="number">
/// <item><c>mobWithoutAttackArt</c> — <c>MobAttack.cpp:88</c> enters the attack
/// animation only when <c>available</c> is non-empty, so a mob with no
/// <c>attack*</c> node never plays one. 41% of Mob.nx carries one (measured
/// 2026-10-16, 50 of 120); on <c>f4/Map/Map1/100000004.img</c> it is 0 of 2.</item>
/// <item><c>mobHitOriginShift</c> — <c>DrawArgument.h:143</c>, <c>rlt = pos -
/// center - origin</c>: the drawn rectangle subtracts the frame's OWN origin, so a
/// pose whose origin differs from <c>stand</c> genuinely draws offset.
/// Measured: <c>2220100</c> stand <c>(27,58)</c> vs hit1 <c>(28,65)</c>.</item>
/// <item><c>mobPositionJump</c> — a mob whose position moves further in one frame
/// than the walk allows. This is the standing form of "the coordinates shifted":
/// the logical position is sampled every frame and compared.</item>
/// <item><c>playerCannotFinishFight</c> — how many swings the SHEET says it takes
/// to kill each mob, from <c>PlayerStatus.MaxDamage</c> against
/// <c>Mob.cpp:1590-1601</c>'s defence term and the mob's own <c>maxHP</c>. This
/// is the next question a person asks after "why is the mob not attacking",
/// and it is answerable without a fight.</item>
/// </list>
/// <para>
/// <b>Nothing here decides anything.</b> The audit reads the same archive the
/// simulation reads, through the same services, inside the same gate. It cannot
/// change a frame, and a finding of <c>severity: info</c> is not a complaint.
/// </para>
/// </remarks>
public sealed class SimAuditService
{
    /// <summary>
    /// The largest one-frame move a mob may make, in px.
    /// </summary>
    /// <remarks>
    /// A grounded mob walks; measured 2.5 px/frame for the character, and the
    /// mobs use the same step. 8 is generous enough to survive a slope and a
    /// knockback, and small enough to catch a teleport-sized jump. OWN DESIGN:
    /// the reference has no such constant because it has no such audit.
    /// </remarks>
    public const double MaxMobStepPx = 8;

    private readonly MapSimSessionStore _sessions;
    private readonly CombatService _combat;

    public SimAuditService(MapSimSessionStore sessions, CombatService combat)
    {
        _sessions = sessions;
        _combat = combat;
    }

    /// <summary>Audit one live session.</summary>
    public IReadOnlyList<SimFinding> Audit(string? sessionId)
    {
        MapSimSession? session = _sessions.Find(sessionId ?? string.Empty);
        if (session is null)
        {
            return [new SimFinding
            {
                Code = "noSession",
                Severity = "error",
                Text = $"no live character with id {sessionId}",
            }];
        }

        var findings = new List<SimFinding>();
        FindAttackArt(session, findings);
        FindAttackMotion(session, findings);
        FindHitOriginShift(session, findings);
        FindPositionJumps(session, findings);
        FindUnwinnableFights(session, findings);
        return findings;
    }

    // ------------------------------------------------------------------ (1)
    private void FindAttackArt(MapSimSession session, List<SimFinding> findings)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var withArt = new SortedSet<string>(StringComparer.Ordinal);
        foreach ((MobPatrol _, string mobId, string kind, _) in session.Mobs)
        {
            if (kind != "m") continue;
            ids.Add(mobId);
            if (_combat.MobAttacks(mobId).Count > 0) withArt.Add(mobId);
        }
        if (ids.Count == 0) return;

        var silent = ids.Except(withArt).ToList();
        if (silent.Count == 0)
        {
            findings.Add(new SimFinding
            {
                Code = "mobAttackArt",
                Severity = "info",
                Text = $"all {ids.Count} mob(s) on this map carry attack art",
                Data = { ["withAttackArt"] = withArt.Count, ["mobs"] = ids.Count },
            });
            return;
        }
        findings.Add(new SimFinding
        {
            Code = "mobWithoutAttackArt",
            Severity = "warn",
            Text = $"{silent.Count} of {ids.Count} mob(s) carry no attack* node in Mob.nx, "
                 + "so MobAttack.cpp:88 never enters the animation branch for them. "
                 + "They still deal contact damage. This is the archive, not the port.",
            Data = { ["ids"] = silent.Take(24).ToList(), ["withAttackArt"] = withArt.Count },
        });
    }

    // ------------------------------------------------------------------ (2)
    /// <summary>
    /// Did any mob move while an attack was playing?
    /// </summary>
    /// <remarks>
    /// The one finding here with a zero tolerance. <c>MobAttack.cpp:93-94</c> zeros
    /// the input AND the speed when an attack begins, and <c>:48</c> returns while
    /// <c>active_attack &gt;= 0</c>, so the move decision at <c>:119-126</c> is
    /// unreachable for the length of a swing. ANY movement during a swing is
    /// therefore a defect, not a tolerance question.
    ///
    /// This was a real one. The chase block excluded a swinging mob by making its
    /// own condition FALSE, and a false condition falls THROUGH — into the patrol,
    /// which walked the mob along its own route mid-swing. MEASURED before the
    /// fix: <c>9500302</c> played <c>attack1</c> for 285 frames and moved on 22 of
    /// them at 0.6px per frame. After it: 0 of 276.
    /// </remarks>
    private void FindAttackMotion(MapSimSession session, List<SimFinding> findings)
    {
        var movers = session.LastMobMovesWhileAttacking;
        if (movers.Count == 0)
        {
            findings.Add(new SimFinding
            {
                Code = "mobMovesWhileAttacking",
                Severity = "info",
                Text = "no mob moved on any frame where it was playing an attack "
                     + "(MobAttack.cpp:93-94 stops it dead)",
                Data = { ["frames"] = session.FramesAdvanced },
            });
            return;
        }
        findings.Add(new SimFinding
        {
            Code = "mobMovesWhileAttacking",
            Severity = "error",
            Text = $"{movers.Count} frame(s) where a mob moved WHILE playing an attack. "
                 + "The reference zeroes input and speed when an attack begins.",
            Data = { ["examples"] = movers.Take(10).ToList() },
        });
    }

    // ------------------------------------------------------------------ (3)
    private void FindHitOriginShift(MapSimSession session, List<SimFinding> findings)
    {
        var shifted = new List<string>();
        var worst = 0;
        foreach ((MobPatrol _, string mobId, string kind, _) in session.Mobs)
        {
            if (kind != "m") continue;
            if (!_combat.TryMobStandOrigin(mobId, out int standX, out int standY)) continue;
            if (!_combat.TryMobOrigin(mobId, "hit1", 0, out int hitX, out int hitY)) continue;
            int dx = hitX - standX;
            int dy = hitY - standY;
            if (Math.Abs(dx) + Math.Abs(dy) == 0) continue;
            if (Math.Abs(dx) + Math.Abs(dy) > worst) worst = Math.Abs(dx) + Math.Abs(dy);
            if (shifted.Count < 24) shifted.Add($"{mobId}:({standX},{standY})->({hitX},{hitY})");
        }
        if (shifted.Count == 0) return;

        findings.Add(new SimFinding
        {
            Code = "mobHitOriginShift",
            Severity = "info",
            Text = "hit1 frames draw at a different origin than stand, so a flinching mob "
                 + "is drawn offset while its position does not move. "
                 + "DrawArgument.h:143 subtracts the frame's own origin, so this is the "
                 + "reference's own arithmetic, not a displacement.",
            Data = { ["examples"] = shifted, ["largestDeltaPx"] = worst },
        });
    }

    // ------------------------------------------------------------------ (3)
    private void FindPositionJumps(MapSimSession session, List<SimFinding> findings)
    {
        var jumps = session.LastMobJumps;
        if (jumps.Count == 0)
        {
            findings.Add(new SimFinding
            {
                Code = "mobPositionJump",
                Severity = "info",
                Text = $"no mob moved more than {MaxMobStepPx}px in any frame this session",
                Data = { ["frames"] = session.FramesAdvanced },
            });
            return;
        }
        findings.Add(new SimFinding
        {
            Code = "mobPositionJump",
            Severity = "error",
            Text = $"{jumps.Count} one-frame mob move(s) exceeded {MaxMobStepPx}px",
            Data = { ["examples"] = jumps.Take(12).ToList(), ["frames"] = session.FramesAdvanced },
        });
    }

    // ------------------------------------------------------------------ (4)
    private void FindUnwinnableFights(MapSimSession session, List<SimFinding> findings)
    {
        PlayerStatus status = session.Status;
        // The predicate is the EQUIPPED WEAPON, not the damage window. `Swing()`
        // (MapSimSessionStore.cs:1435) puts the whole swing inside
        // `if (AttackStancesFor(session.EquippedWeapon).Length != 0)`, and with no
        // weapon that array is empty — `CharLook.cpp:594`'s early return.
        //
        // It is NOT `MaxDamage <= 0`: an unarmed level-1 character still computes
        // `((4*12+5)/100) * (1 * 1.2) = 0.636` and rounds to 1, so the damage
        // window is non-zero and testing it reported "can fight" for a character
        // that cannot swing. Caught by simhealth-verify.py's own armed/unarmed
        // calibration, which is the only reason that pair is in the suite.
        if (string.IsNullOrWhiteSpace(session.EquippedWeapon))
        {
            findings.Add(new SimFinding
            {
                Code = "playerCannotFight",
                Severity = "warn",
                Text = "the character is unarmed, so every swing is refused "
                     + "(CharLook.cpp:594: no weapon means no swing pose).",
                Data =
                {
                    ["equippedWeapon"] = "",
                    // Why this is not a damage problem: the window is NOT zero.
                    ["maxDamageIfItCould"] = status.MaxDamage,
                    ["minDamageIfItCould"] = status.MinDamage,
                },
            });
            return;
        }

        var rows = new List<Dictionary<string, object?>>();
        int unwinnable = 0;
        foreach ((MobPatrol patrol, string mobId, string kind, _) in session.Mobs)
        {
            if (kind != "m" || !patrol.Alive) continue;
            int maxHp = patrol.MaxHp > 0 ? patrol.MaxHp : _combat.MobMaxHp(mobId);

            // ADR-0006 (2026-10-17): the simulator deals a fixed SimTuning.PlayerDamage,
            // so THAT is what "one swing" is worth here. This used to be the sheet's
            // MaxDamage through Mob.cpp:1590-1601's `max = maxDamage * (1 - 0.01*leveldelta)
            // - defence*0.5`, which made the audit answer a question the simulator no
            // longer asks: measured, it reported an armed character as unable to finish
            // any fight while the same character was killing a 1000 HP mob ten hits at a
            // time. The reference's formula still exists -- DamageRoll.Roll -- and is
            // simply not what this simulator deals, so it is not what this audit predicts.
            double best = SimTuning.PlayerDamage;
            double worst = SimTuning.PlayerDamage;
            int swings = (int)Math.Ceiling(maxHp / best);
            if (swings > 400) unwinnable++;
            if (rows.Count < 12)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["mobId"] = mobId,
                    ["maxHp"] = maxHp,
                    ["defence"] = _combat.MobDefence(mobId),
                    ["worstSwing"] = Math.Round(worst, 2),
                    ["bestSwing"] = Math.Round(best, 2),
                    ["swingsToKill"] = swings,
                });
            }
        }

        findings.Add(new SimFinding
        {
            Code = unwinnable > 0 ? "playerCannotFinishFight" : "playerCanFinishFight",
            Severity = unwinnable > 0 ? "warn" : "info",
            Text = unwinnable > 0
                ? $"{unwinnable} mob(s) need more than 400 of this character's best swings to die"
                : "every living mob on this map dies within 400 of the character's best swings",
            Data =
            {
                ["minDamage"] = status.MinDamage,
                ["maxDamage"] = status.MaxDamage,
                ["rows"] = rows,
            },
        });
    }
}