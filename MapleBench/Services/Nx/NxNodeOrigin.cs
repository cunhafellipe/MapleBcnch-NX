using System.Runtime.CompilerServices;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.Nx;

/// <summary>
/// Remembers which NX node each converted property came from.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the writer can reproduce a shared subtree once instead of once per
/// reference. NX is a directed acyclic graph: across the reference corpus 394,041 nodes are
/// claimed by more than one parent, and Character.nx holds 227,783 of them. MapleLib cannot
/// express that — a property has exactly one <c>Parent</c> — so the conversion necessarily
/// materialises the shared subtree once per reference. Writing that straight back out is
/// correct but expands Character.nx from 186 MB to 758 MB, and it is the only thing standing
/// between this format and being edited in place without consequence.
/// </para>
/// <para>
/// The corpus settles how to undo it. Every multi-parent node shares an <em>identical</em> child
/// range with its other parents — 312 of 312 in Morph.nx, 5,550 of 5,550 in Item.nx, 227,783 of
/// 227,783 in Character.nx, and not one partial share anywhere. So two parents pointing at the
/// same first-child index with the same count are describing one subtree, and a writer that
/// emits it once has reproduced the source exactly rather than approximated it.
/// </para>
/// <para>
/// Weakly keyed, like the audio and canvas payload registries, so a table entry is never the
/// reason a property stays alive.
/// </para>
/// </remarks>
public static class NxNodeOrigin
{
    private sealed class Origin(int index)
    {
        public int Index { get; } = index;
    }

    private static readonly ConditionalWeakTable<WzImageProperty, Origin> Origins = new();

    internal static void Register(WzImageProperty property, int nxNodeIndex) => Origins.AddOrUpdate(property, new Origin(nxNodeIndex));

    /// <summary>The NX node index a property came from, or -1 if it did not come from NX.</summary>
    internal static int IndexOf(WzImageProperty? property)
        => property is not null && Origins.TryGetValue(property, out var origin) ? origin.Index : -1;

    /// <summary>True when this property stands for an NX node rather than something built here.</summary>
    public static bool IsFromNx(WzImageProperty? property) => IndexOf(property) >= 0;
}