using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace MapleBench.Services.Nx;

/// <summary>
/// A read-only view of one node in an NX archive.
/// </summary>
/// <remarks>
/// <para>
/// This is a handle, not a copy: a struct holding an archive reference and a 20-byte-record
/// index. Constructing one faults in at most one page, so walking a whole archive allocates
/// nothing per node. That is deliberate — <c>Map.nx</c> has 8.4 million nodes, and a tree of
/// materialised objects would be both slower and much larger than the file it came from.
/// </para>
/// <para>
/// The default value is an <em>invalid</em> node, not an error. Every accessor on it returns a
/// neutral result rather than throwing, which means a missing lookup collapses into the
/// "not present" case instead of having to be guarded at every step of a walk.
/// </para>
/// </remarks>
public readonly unsafe struct NxNode : IEnumerable<NxNode>
{
    private readonly NxArchive _archive;
    private readonly int _index;

    internal NxNode(NxArchive archive, int index)
    {
        _archive = archive;
        _index = index;
    }

    /// <summary>A node that refers to nothing. All accessors on it are safe and return defaults.</summary>
    public static NxNode None => default;

    /// <summary>False for the node returned by a failed lookup.</summary>
    public bool IsValid => _archive is not null && (uint)_index < (uint)_archive.NodeCount;

    /// <summary>This node's position in the archive's node block.</summary>
    public int Index => _index;

    /// <summary>Which of the seven value shapes this node holds.</summary>
    public NxNodeType Type => IsValid ? _archive.NodeType(_index) : NxNodeType.None;

    /// <summary>
    /// This node's name, from the archive-wide string pool.
    /// </summary>
    /// <remarks>
    /// Nodes are looked up by these names, and they are compared as raw UTF-8 byte sequences.
    /// That is not the same as an ordinal string comparison in one edge case — embedded NULs
    /// sort differently as bytes than some culture-aware comparators would place them — so
    /// lookups use <see cref="FindChild(string)"/> rather than a dictionary keyed on the
    /// decoded string.
    /// </remarks>
    public string Name => IsValid ? _archive.GetString((int)_archive.NodeNameId(_index)) : string.Empty;

    /// <summary>How many direct children this node has.</summary>
    public int ChildCount => IsValid ? _archive.NodeChildCount(_index) : 0;

    /// <summary>The archive this node came from.</summary>
    public NxArchive? Archive => _archive;

    /// <summary>
    /// The child at <paramref name="ordinal"/>, in file order.
    /// </summary>
    /// <remarks>
    /// Children are stored contiguously in breadth-first order, so this is a bounds check and
    /// an add. Do not confuse it with <see cref="FindChild(string)"/>: indexing by number gets
    /// the nth child regardless of what it is called.
    /// </remarks>
    public NxNode this[int ordinal]
    {
        get
        {
            if (!IsValid || (uint)ordinal >= (uint)ChildCount)
            {
                return None;
            }

            return new NxNode(_archive, (int)(_archive.NodeFirstChild(_index) + (uint)ordinal));
        }
    }

    /// <summary>The child with the given name, or <see cref="None"/>.</summary>
    public NxNode this[string name] => FindChild(name);

    /// <summary>
    /// Finds a direct child by exact name.
    /// </summary>
    /// <remarks>
    /// A linear scan. NX stores children sorted by name, so a binary search would be valid and
    /// would turn a lookup on a wide node — <c>smap.img</c>-scale nodes carry hundreds of
    /// children — from hundreds of comparisons into about nine. It is deliberately not done
    /// here: the scan is cache-friendly because the children are contiguous, and the call
    /// sites that would benefit are dominated by walking every child anyway. Revisit if a
    /// lookup-heavy caller appears; the sortedness invariant is asserted by the archive audit.
    /// </remarks>
    public NxNode FindChild(string name)
    {
        if (!IsValid || string.IsNullOrEmpty(name))
        {
            return None;
        }

        int count = ChildCount;
        uint first = _archive.NodeFirstChild(_index);
        for (int i = 0; i < count; i++)
        {
            var child = new NxNode(_archive, (int)(first + (uint)i));
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return None;
    }

    /// <summary>
    /// Walks a <c>/</c>-separated path from this node, e.g. <c>Smap.img/100/info/name</c>.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="None"/> at the first segment that does not resolve, rather than
    /// throwing, so a caller probing for optional data does not need a try/catch per level.
    /// </remarks>
    public NxNode Resolve(string path)
    {
        NxNode current = this;
        if (!IsValid || string.IsNullOrEmpty(path))
        {
            return None;
        }

        int start = 0;
        while (start <= path.Length)
        {
            int slash = path.IndexOf('/', start);
            int length = (slash < 0 ? path.Length : slash) - start;
            if (length > 0)
            {
                current = current.FindChild(path.Substring(start, length));
                if (!current.IsValid)
                {
                    return None;
                }
            }

            if (slash < 0)
            {
                break;
            }

            start = slash + 1;
        }

        return current;
    }

    /// <summary>The 64-bit integer value, or 0 if this node is not an integer.</summary>
    public long AsInteger => Type == NxNodeType.Integer ? Unsafe.ReadUnaligned<long>(_archive.PayloadOf(_index)) : 0L;

    /// <summary>The double value, or 0 if this node is not a real.</summary>
    public double AsReal => Type == NxNodeType.Real ? Unsafe.ReadUnaligned<double>(_archive.PayloadOf(_index)) : 0d;

    /// <summary>The string value, or an empty string if this node is not a string.</summary>
    public string AsString => Type == NxNodeType.String
        ? _archive.GetString((int)Unsafe.ReadUnaligned<uint>(_archive.PayloadOf(_index)))
        : string.Empty;

    /// <summary>The vector's two components, or <c>(0, 0)</c> if this node is not a vector.</summary>
    public (int X, int Y) AsVector => Type == NxNodeType.Vector
        ? (Unsafe.ReadUnaligned<int>(_archive.PayloadOf(_index)), Unsafe.ReadUnaligned<int>(_archive.PayloadOf(_index) + 4))
        : (0, 0);

    /// <summary>Where this node's canvas payload lives, or <c>null</c> if it is not a bitmap.</summary>
    public NxCanvas? AsCanvas
    {
        get
        {
            if (Type != NxNodeType.Bitmap)
            {
                return null;
            }

            byte* payload = _archive.PayloadOf(_index);
            return new NxCanvas(
                _archive,
                Unsafe.ReadUnaligned<uint>(payload),
                Unsafe.ReadUnaligned<ushort>(payload + 4),
                Unsafe.ReadUnaligned<ushort>(payload + 6));
        }
    }

    /// <summary>Where this node's audio payload lives, or <c>null</c> if it is not an audio node.</summary>
    public NxAudio? AsAudio
    {
        get
        {
            if (Type != NxNodeType.Audio)
            {
                return null;
            }

            byte* payload = _archive.PayloadOf(_index);
            return new NxAudio(_archive, Unsafe.ReadUnaligned<uint>(payload), Unsafe.ReadUnaligned<uint>(payload + 4));
        }
    }

    /// <summary>
    /// A best-effort scalar view of any node, for callers that only want a number or a string
    /// and do not care which of the numeric shapes it was stored as.
    /// </summary>
    public object? AsScalar => Type switch
    {
        NxNodeType.Integer => AsInteger,
        NxNodeType.Real => AsReal,
        NxNodeType.String => AsString,
        NxNodeType.Vector => AsVector,
        _ => null,
    };

    /// <summary>Enumerates direct children in file order.</summary>
    public IEnumerator<NxNode> GetEnumerator()
    {
        int count = ChildCount;
        for (int i = 0; i < count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Enumerates every node in this subtree, parents before children.</summary>
    public IEnumerable<NxNode> DescendantsAndSelf()
    {
        var stack = new Stack<NxNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            NxNode node = stack.Pop();
            if (!node.IsValid)
            {
                continue;
            }

            yield return node;

            // Reversed, because the stack pops last-in first and children must stay in
            // file order to make the walk reproducible.
            for (int i = node.ChildCount - 1; i >= 0; i--)
            {
                stack.Push(node[i]);
            }
        }
    }

    public override string ToString()
    {
        if (!IsValid)
        {
            return "<none>";
        }

        return Type switch
        {
            NxNodeType.Integer => $"{Name} = {AsInteger}",
            NxNodeType.Real => $"{Name} = {AsReal}",
            NxNodeType.String => $"{Name} = \"{AsString}\"",
            NxNodeType.Vector => $"{Name} = ({AsVector.X}, {AsVector.Y})",
            NxNodeType.Bitmap => $"{Name} = <canvas {AsCanvas!.Value.Width}x{AsCanvas!.Value.Height}>",
            NxNodeType.Audio => $"{Name} = <audio {AsAudio!.Value.Length} bytes>",
            _ => $"{Name} ({ChildCount} children)",
        };
    }

    /// <summary>Equality is node identity within one archive, not name equality.</summary>
    public bool Equals(NxNode other) => ReferenceEquals(_archive, other._archive) && _index == other._index;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is NxNode other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_archive, _index);
}