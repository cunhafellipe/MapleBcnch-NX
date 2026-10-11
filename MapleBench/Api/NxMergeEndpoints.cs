using MapleBench.Models;
using MapleBench.Services;

namespace MapleBench.Api;

/// <summary>
/// The HTTP surface for grafting candidate IMGs into an NX archive.
/// </summary>
/// <remarks>
/// <para>
/// Kept out of <see cref="Endpoints"/> so this owns one file rather than a
/// region in a nine-hundred-line one; wired in with <c>MapNxMerge(api)</c>
/// alongside the other <c>Map*</c> calls.
/// </para>
/// <para>
/// One route, and it is a POST that changes nothing unless <c>commit</c> is
/// true. The preview is not a client-side guess: the archive is opened and
/// inspected either way, so "added" versus "replaced" is what the tree actually
/// holds. That is the same preview-then-commit shape as every other mutating
/// route here.
/// </para>
/// </remarks>
public static class NxMergeEndpoints
{
    public static void MapNxMerge(this RouteGroupBuilder api)
    {
        // Grafts standalone .img files into an NX archive and, on commit,
        // rewrites it through the normal save transaction (scratch file,
        // read-back check, timestamped backup, atomic swap).
        api.MapPost("/nx/merge", (NxMergeRequest request, NxMergeService merge) =>
            Results.Ok(merge.Merge(request)));
    }
}
