using MapleBench.Services.Simulate;

namespace MapleBench.Api;

/// <summary>
/// The cast simulator's HTTP surface.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="SkillEndpoints"/> on purpose. That one edits the
/// archive — browses books, rewrites formulas, bakes <c>common</c> into literal
/// levels. This one answers "what happens when this skill is cast", which reads
/// the same archive through a different lens and must never be able to write.
/// </para>
/// <para>
/// Every route here is a GET that reports, or a POST that resolves. None of them
/// mutate: a cast plan is derived, and re-deriving it must be free.
/// </para>
/// </remarks>
public static class SkillCastEndpoints
{
    public static void MapSkillCasts(this RouteGroupBuilder api)
    {
        // What this mode can do, in the same shape /api/skill/capabilities uses so
        // one client can ask both and not have to special-case either.
        api.MapGet("/skill-cast/capabilities", (SkillCastService casts) =>
            Results.Ok(new
            {
                available = true,
                bookRule = "id/10000, or id/100 for a 9-digit id starting 800",
                effectNodes = SkillCastService.EffectNodes,
                collisionStrikes = 6,
                strikeSpacing = 7,
                collisionSpacing = 6,
                defaultDamageWaitTime = 15,
            }));

        // The plan for one skill: what the character plays, what sprites the cast
        // spawns, the hitbox, and the tick-by-tick timeline.
        api.MapGet("/skill-cast/plan", (string skillId, SkillCastService casts) =>
        {
            if (!int.TryParse(skillId, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                return Results.BadRequest(new { error = "skillId must be numeric", skillId });

            SkillCastPlan? plan = casts.Plan(skillId);
            return plan is null
                ? Results.NotFound(new { error = $"no skill {skillId} in the open archives", skillId })
                : Results.Ok(plan);
        });

        // Which skills this archive can actually cast, sampled per book. A v83
        // client carries thousands and listing all of them is a screen, not an
        // answer; this is for finding a representative cast per book quickly.
        api.MapGet("/skill-cast/sample", (int? perBook, SkillCastService casts) =>
            Results.Ok(new
            {
                perBook = perBook ?? 2,
                plans = casts.Sample(perBook ?? 2),
            }));

        // Every skill the reference would LIST, not every skill the archive holds.
        //
        // This is the one place the filter matters, because "which skills exist"
        // is the reference's own definition (SkillForm.cs:99-106 / :118-127): it has
        // a hit node, an effect node and a collision box, and its id is not
        // 0-prefixed. A skill failing any of those is invisible in the reference
        // and invisible here, so this is the list a settings panel must be built on
        // — /sample cannot serve it, because sampling per book deliberately reaches
        // past the filter to show one representative cast.
        //
        // `preBb` is reported alongside because it decides WHICH of the two collision
        // box tests ran, and it is a global property of the archive rather than a
        // per-skill one (SkillForm.cs:85).
        api.MapGet("/skill-cast/castable", (SkillCastService casts) =>
        {
            // Called ONCE and reused. `Castable()` walks every book in every open
            // Skill archive and rebuilds the whole String name table, so calling it
            // twice would double that cost and — worse — `count` and `skills` would
            // come from two different walks, which is exactly the shape of a lie.
            List<SkillListEntry> skills = casts.Castable();
            return Results.Ok(new
            {
                preBb = casts.PreBB(),
                count = skills.Count,
                skills,
            });
        });
    }
}