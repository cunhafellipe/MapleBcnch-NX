using System.Runtime.CompilerServices;
using MapleLib.WzLib;

namespace MapleBench.Services.Nx;

/// <summary>
/// Remembers how to populate an NX image that was created but not built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why laziness lives here and not in MapleLib.</b> <c>WzImage.ParseImage</c> is
/// <c>public bool ParseImage(bool forceReadFromData = false)</c> and is <b>not virtual</b>. That
/// matters because MapleLib itself is what calls it — from inside <c>WzImage[propertyName]</c> and
/// from a dozen other members. A subclass cannot intercept the hook the library reaches past it
/// on, so making MapleLib parse NX lazily would mean patching a vendored dependency that a
/// re-clone undoes. The hook this application does own is <c>WzSessionService.EnsureParsed</c>,
/// and that is where the build is driven from.
/// </para>
///
/// <para>
/// <b>What it saves.</b> An NX archive is a directed acyclic graph whose nodes live in a flat
/// breadth-first table, so a node reachable by several paths is materialised once per path:
/// <c>Character.nx</c> declares 3,616,825 nodes and builds about 10.9 million of them, which is
/// where its 5.6 GB of resident heap goes. Almost none of that is ever looked at — opening the
/// archive and listing its 35 images reads none of it. An image shell plus a deferred tree turns
/// "open the archive" from ten million objects into thirty-five.
/// </para>
///
/// <para>
/// <b>The invariant this table has to preserve.</b> An image that has not been built yet holds no
/// properties, so anything that reads <c>WzProperties</c> without going through
/// <c>EnsureParsed</c> sees an empty image — a wrong display, not lost data. It cannot be made
/// right here, only not made wrong here; the fix for any such path belongs at that path. Which is
/// why this is a registry of <em>closures</em> rather than a lazy wrapper type: the build runs
/// from the one place that can guarantee it, so a reader that skips that place fails visibly and
/// the same way every time, instead of silently depending on whether some unrelated code path
/// happened to touch the image first.
/// </para>
///
/// <para>
/// <b>Why an entry is a small object and not a bare delegate.</b> Because a built NX image is not
/// permanent. <c>ImageMemoryService</c> releases images by calling <c>UnparseImage()</c>, which
/// clears the property collection and sets <c>Parsed = false</c> — deliberately, and correctly:
/// an image the user is not looking at should not be holding its tree. For a WZ image that is
/// free, because <c>EnsureParsed</c> reparses it from the reader it still has. An NX shell has no
/// reader and no bytes to re-read, so the same sweep would turn a built image into a permanently
/// empty one unless the build can run again. That is what <see cref="Entry"/> is for: a build
/// runs when the image is not populated, and re-runs when a sweep has emptied it.
/// </para>
///
/// <para>
/// Weakly keyed, like <see cref="NxNodeOrigin"/> and the audio payload registry, so an entry is
/// never the reason an archive stays alive. A WZ image simply has no entry and is unaffected.
/// </para>
/// </remarks>
public static class NxImageBuilders
{
    private sealed class Builder(Action<WzImage> build, long nodeCount)
    {
        /// <summary>Nodes this image will materialise. 0 means "unknown".</summary>
        internal long NodeCount { get; } = nodeCount;

        // Held rather than spun on: a build is a whole image, runs under the
        // session gate, and is rare next to the reads that trigger it. A lock
        // is also reentrant, so a build that somehow reached its own image
        // would see the flag below and stop rather than recurse.
        private readonly object _gate = new();
        private bool _built;

        public void Run(WzImage image)
        {
            lock (_gate)
            {
                // Built and still populated: nothing to do. The Parsed test is
                // what distinguishes "already built" from "built once and then
                // swept", because UnparseImage() resets exactly that flag.
                if (_built && image.Parsed)
                    return;

                _built = false;
                try
                {
                    // The open-time guard no longer covers this, because an open
                    // no longer builds anything. This is where the memory actually
                    // goes, so it is where the check belongs: measured 2026-10-19,
                    // a full build costs 363-739 bytes per node across the client,
                    // 560 being the middle. Character.nx declares 3.6 M nodes, so
                    // building it all is a ~6 GB allocation and it now gets asked
                    // for before it starts rather than at open time.
                    RequireRoomForBuild(NodeCount);
                    build(image);
                    _built = true;
                }
                catch
                {
                    // Leave a consistent shell behind rather than a half-filled
                    // one: empty, unparsed, and free to be built again. A partial
                    // tree would be worse than none, because it looks populated
                    // to every reader that does not ask.
                    try { image.UnparseImage(); } catch { /* nothing left to clear */ }
                    throw;
                }
            }
        }
    }

    /// <summary>Refuses a build the machine has no room to finish.</summary>
    private static void RequireRoomForBuild(long nodeCount)
    {
        if (nodeCount <= 0)
            return;

        long free = SystemMemory.FreeBytes();
        if (free < 0)
            return;

        long needed = nodeCount * BytesPerMaterialisedNode;
        if (free >= needed)
            return;

        throw new InvalidOperationException(
            "Building this image needs about " + (needed / (1024 * 1024)) + " MB and only "
            + (free / (1024 * 1024)) + " MB is free. An NX image is built in full the first "
            + "time something reads it, and a build the machine cannot finish takes the session "
            + "with it. Close archives you are not using and try again.");
    }

    /// <summary>Heap cost of one materialised node, measured 2026-10-19.</summary>
    private const long BytesPerMaterialisedNode = 560;

    private static readonly ConditionalWeakTable<WzImage, Builder> Builders = new();

    /// <summary>Records how to build <paramref name="image"/>, replacing any previous builder.</summary>
    internal static void Register(WzImage image, Action<WzImage> build, long nodeCount = 0)
        => Builders.AddOrUpdate(image, new Builder(build, nodeCount));

    /// <summary>
    /// Builds <paramref name="image"/> if it is an NX shell that has not been built.
    /// </summary>
    /// <returns>
    /// True when this image came from an NX archive, whether or not the build had work to do.
    /// A false return means it is an ordinary image the caller must parse itself.
    /// </returns>
    internal static bool BuildIfPending(WzImage image)
    {
        if (!Builders.TryGetValue(image, out Builder? build))
            return false;

        build.Run(image);
        return true;
    }

    /// <summary>True when this image is waiting on a build rather than holding its properties.</summary>
    public static bool IsPending(WzImage image) => Builders.TryGetValue(image, out _);
}