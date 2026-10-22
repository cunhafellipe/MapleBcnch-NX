using MapleBench.Services;
using MapleBench.Services.Simulate;

namespace MapleBench.Api;

/// <summary>
/// Animation defect reporting over the existing animation reader.
/// </summary>
/// <remarks>
/// Read-only, and it adds nothing to what the archive already says: every route here
/// calls <c>AnimationService.Describe</c> and compares the numbers it returns. That is
/// the point — an auditor that re-reads the archive on its own terms could disagree
/// with the renderer about what a frame is, and then its findings would be about the
/// auditor.
/// </remarks>
public static class AnimationAuditEndpoints
{
    public static void MapAnimationAudits(this RouteGroupBuilder api)
    {
        // One stance. `path` names the stance node itself, e.g.
        // f7/0100100.img/stand — not the image, not the frame.
        api.MapGet("/animation/audit", (string path, string? subject, AnimationAuditService audit) =>
        {
            AnimationAudit? result = audit.Audit(path, subject);
            return result is null
                ? Results.NotFound(new { error = $"no animation at '{path}'", path })
                : Results.Ok(result);
        });

        // Every stance of one image, with the per-stance verdict next to the totals.
        // The stances a caller names are the ones checked; nothing is inferred from the
        // image's children, because a stance the image lacks is absent, not broken.
        api.MapGet("/animation/audit/image", (string path, string? stances,
            AnimationAuditService audit) =>
        {
            string[] list = string.IsNullOrWhiteSpace(stances)
                ? AnimationAuditService.DefaultStances
                : stances.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            List<AnimationAudit> audits = audit.AuditImage(path, list);
            return Results.Ok(new
            {
                path,
                stances = list,
                audits,
                clean = audits.Count(a => a.Clean),
                withErrors = audits.Count(a => a.Errors > 0),
                totalAnomalies = audits.Sum(a => a.Anomalies.Count),
                missing = audit.MissingStances,
            });
        });

        api.MapGet("/animation/audit/rules", () =>
            Results.Ok(new
            {
                rules = AnimationAuditService.Rules,
                sizeJumpFactor = "3x the stance's own median frame-size change, floor 8px",
                severity = new
                {
                    error = "the stance cannot play as written (a frame the clock cannot leave)",
                    warn = "it plays, but not at the speed or alignment the art implies, or its layer set changes between frames",
                },
                note = "own-design: the benchmark has no animation auditor. Inputs are the archive's; the thresholds are not.",
            }));
    }
}