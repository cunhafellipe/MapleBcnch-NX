using System.Buffers.Binary;
using System.Text;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using SkiaSharp;

namespace MapleBench.Services.Nx;

/// <summary>
/// Writes a MapleLib tree back out as an NX (<c>PKG4</c>) archive.
/// </summary>
/// <remarks>
/// <para>
/// <b>What has to be rebuilt, and why nothing can be patched in place.</b> NX stores nodes
/// breadth-first, and a node's record holds the <em>index</em> of its first child rather than
/// a reference. Adding, removing or reordering a single child therefore renumbers every node
/// after it, and the four offset tables at the end of the file all move. The whole archive is
/// rewritten; there is no incremental path.
/// </para>
///
/// <para><b>Children are sorted.</b> Readers that binary-search a node's children compare raw
/// UTF-8 bytes, and the sortedness holds in every archive in the reference corpus ??so a
/// writer that did not reproduce it would produce files those readers silently mis-resolve.
/// WZ has no ordering requirement, so this is a real difference: <b>node order is not
/// preserved</b>. Everything else ??names, values, types, canvas sizes and children ??is.
/// </para>
///
/// <para><b>Numbers widen.</b> NX has one 64-bit integer and one 64-bit float where WZ had
/// Short, Int, Long, Float and Double. A writer cannot widen <em>back</em>, so an NX archive
/// written from a WZ tree loses the distinction and reports it through
/// <see cref="NxWriteReport.TypeWidened"/> rather than pretending otherwise.
/// </para>
///
/// <para><b>Canvas encoding.</b> Pixels go out as WebP. Decoding is exact; encoding is
/// pixel-exact but not byte-identical to the originals ??SkiaSharp emits a VP8X container
/// where the shipped archives use VP8L, at roughly 65% more bytes. Both are ordinary WebP and
/// both are readable; the file is larger.
/// </para>
/// </remarks>
public static unsafe class NxWriter
{
    /// <summary>
    /// Appends little-endian values to a growable buffer, and knows exactly how long it is at
    /// every moment.
    /// </summary>
    /// <remarks>
    /// This exists because <c>BinaryWriter</c> buffers, and <c>BaseStream.Position</c> does not
    /// advance until it flushes. Reading a position to record where a table started therefore
    /// returned a stale value, and every offset derived from it pointed into the middle of the
    /// node block ??which the reader rejects as an overlap. Flushing before each read papers
    /// over it; not buffering at all removes the possibility. The header is also patched by
    /// seeking backwards, which a buffered writer and a raw position cannot both do safely.
    /// </remarks>
    private sealed class NxSink
    {
        private byte[] _buffer = new byte[1 << 16];
        private int _length;

        internal int Position => _length;

        internal void Write(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        internal void Write(ushort value)
        {
            Ensure(2);
            _buffer[_length++] = (byte)value;
            _buffer[_length++] = (byte)(value >> 8);
        }

        internal void Write(uint value)
        {
            Ensure(4);
            for (int i = 0; i < 4; i++)
            {
                _buffer[_length++] = (byte)(value >> (i * 8));
            }
        }

        internal void Write(ulong value)
        {
            Ensure(8);
            for (int i = 0; i < 8; i++)
            {
                _buffer[_length++] = (byte)(value >> (i * 8));
            }
        }

        internal void Write(long value) => Write((ulong)value);

        internal void Write(int value) => Write((uint)value);

        internal void Write(double value) => Write((ulong)BitConverter.DoubleToUInt64Bits(value));

        internal void Write(float value) => Write((uint)BitConverter.SingleToUInt32Bits(value));

        internal void Write(byte[] value)
        {
            Ensure(value.Length);
            Buffer.BlockCopy(value, 0, _buffer, _length, value.Length);
            _length += value.Length;
        }

        /// <summary>Pads to a multiple of <paramref name="multiple"/>.</summary>
        internal void Align(int multiple)
        {
            while (_length % multiple != 0)
            {
                Write((byte)0);
            }
        }

        /// <summary>Overwrites four bytes already written.</summary>
        internal void PatchUInt32(int offset, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                _buffer[offset + i] = (byte)(value >> (i * 8));
            }
        }

        /// <summary>Overwrites eight bytes already written.</summary>
        internal void PatchUInt64(int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                _buffer[offset + i] = (byte)(value >> (i * 8));
            }
        }

        internal byte[] ToArray() => _buffer[.._length];

        private void Ensure(int extra)
        {
            if (_length + extra <= _buffer.Length)
            {
                return;
            }

            int size = _buffer.Length;
            while (size < _length + extra)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }
    }

    /// <summary>Serialises an archive to NX bytes.</summary>
    public static byte[] Write(WzFile file, out NxWriteReport report)
    {
        ArgumentNullException.ThrowIfNull(file);

        var plan = Plan(file);
        var strings = BuildStringPool(plan);

        var w = new NxSink();

        // ---- header, patched at the end once every offset is known ----
        w.Write(NxArchive.Magic);
        w.Write(0u);                  // node_count
        w.Write(0UL);                 // node_offset
        w.Write(0u);                  // string_count
        w.Write(0UL);                 // string_offset
        w.Write(0u);                  // bitmap_count
        w.Write(0UL);                 // bitmap_offset
        w.Write(0u);                  // audio_count
        w.Write(0UL);                 // audio_offset
        while (w.Position < NxArchive.HeaderSize)
        {
            w.Write((byte)0);
        }

        // ---- node block ----
        long nodeOffset = w.Position;
        // Keyed on the planned node itself, not on the MapleLib property it came from: the
        // nodes standing for the *.img images have no property to key on, and a null key is a
        // crash rather than a miss.
        var nodeIndex = new Dictionary<PlannedNode, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < plan.Nodes.Count; i++)
        {
            plan.Nodes[i].Index = i;
            nodeIndex[plan.Nodes[i]] = i;
        }

        // A second pass is needed because a child's record needs its own index and its first
        // child's index, and both are only known once every node has one.
        for (int i = 0; i < plan.Nodes.Count; i++)
        {
            PlannedNode node = plan.Nodes[i];
            uint firstChild = node.Children.Count == 0
                ? 0u
                : (uint)nodeIndex[node.Children[0]];

            w.Write(strings.IdOf(node.Name));
            w.Write(firstChild);
            w.Write((ushort)node.Children.Count);
            w.Write((ushort)node.Type);

            switch (node.Type)
            {
                case NxNodeType.Integer: w.Write(node.Integer); break;
                case NxNodeType.Real: w.Write(node.Real); break;
                // A string node's payload is a u32 index plus four zero bytes. The union in a
                // node record is always eight bytes wide whatever the type, and writing only
                // the four meaningful ones shifts every following record by four. It does not
                // show up as a wrong node count: it puts the string table 424 bytes inside
                // the node block, and the reader rejects the archive as malformed. 424 is
                // exactly 106 string nodes times four.
                case NxNodeType.String:
                    w.Write(strings.IdOf(node.StringValue ?? string.Empty));
                    w.Write(0u);
                    break;
                case NxNodeType.Vector: w.Write(node.VectorX); w.Write(node.VectorY); break;
                case NxNodeType.Bitmap: w.Write(node.BitmapIndex); w.Write(node.CanvasWidth); w.Write(node.CanvasHeight); break;
                case NxNodeType.Audio: w.Write(node.AudioIndex); w.Write(node.AudioLength); break;
                default: w.Write(0UL); break;
            }
        }

        // ---- string table, then string data ----
        long stringOffset = strings.WriteTo(w);

        // ---- audio table, then audio data ----
        long audioOffset = w.Position;
        var audioOffsets = new List<long>();
        foreach (byte[] _ in plan.Audio)
        {
            audioOffsets.Add(0);
            w.Write(0UL);
        }

        for (int i = 0; i < plan.Audio.Count; i++)
        {
            w.Align(8);
            long at = w.Position;
            w.Write(plan.Audio[i]);
            audioOffsets[i] = at;
        }

        for (int i = 0; i < plan.Audio.Count; i++)
        {
            w.PatchUInt64((int)audioOffset + i * 8, (ulong)audioOffsets[i]);
        }

        // ---- bitmap table, then bitmap data ----
        long bitmapOffset = w.Position;
        var bitmapOffsets = new List<long>();
        foreach (byte[] _ in plan.Bitmaps)
        {
            bitmapOffsets.Add(0);
            w.Write(0UL);
        }

        for (int i = 0; i < plan.Bitmaps.Count; i++)
        {
            w.Align(8);
            long at = w.Position;
            w.Write((uint)plan.Bitmaps[i].Length);
            w.Write(plan.Bitmaps[i]);
            bitmapOffsets[i] = at;
        }

        for (int i = 0; i < plan.Bitmaps.Count; i++)
        {
            w.PatchUInt64((int)bitmapOffset + i * 8, (ulong)bitmapOffsets[i]);
        }

        // ---- patch the header now that everything is known ----
        w.PatchUInt32(0x04, (uint)plan.Nodes.Count);
        w.PatchUInt64(0x08, (ulong)nodeOffset);
        w.PatchUInt32(0x10, (uint)strings.Count);
        w.PatchUInt64(0x14, (ulong)stringOffset);
        w.PatchUInt32(0x1C, (uint)plan.Bitmaps.Count);
        w.PatchUInt64(0x20, (ulong)bitmapOffset);
        w.PatchUInt32(0x28, (uint)plan.Audio.Count);
        w.PatchUInt64(0x2C, (ulong)audioOffset);

        report = new NxWriteReport(
            plan.Nodes.Count,
            plan.Images,
            strings.Count,
            plan.Bitmaps.Count,
            plan.Audio.Count,
            plan.TypeWidened,
            plan.EncodingErrors);

        return w.ToArray();
    }

    private static StringPool BuildStringPool(NxPlan plan)
    {
        var pool = new StringPool();
        pool.Add(string.Empty); // index 0 is always the empty string, as in every real archive

        foreach (PlannedNode node in plan.Nodes)
        {
            pool.Add(node.Name);
            if (node.Type == NxNodeType.String)
            {
                pool.Add(node.StringValue ?? string.Empty);
            }
        }

        return pool;
    }

    private static NxPlan Plan(WzFile file)
    {
        var plan = new NxPlan();

        var root = new PlannedNode(null, string.Empty, NxNodeType.None, -1);
        foreach (WzImage image in file.WzDirectory.WzImages)
        {
            var imageNode = new PlannedNode(null, image.Name, NxNodeType.None, -1);
            foreach (WzImageProperty property in image.WzProperties ?? Enumerable.Empty<WzImageProperty>())
            {
                imageNode.Children.Add(PlanProperty(property, plan));
            }

            SortChildren(imageNode);
            root.Children.Add(imageNode);
        }

        SortChildren(root);
        plan.Nodes.Add(root);
        plan.Images = file.WzDirectory.WzImages.Count;
        BreadthFirst(plan, root);
        return plan;
    }

    private static PlannedNode PlanProperty(WzImageProperty property, NxPlan plan)
    {
        var node = new PlannedNode(property, property.Name, TypeOf(property), plan.NextOrder++);
        switch (property)
        {
            // WzSubProperty, WzConvexProperty and WzCanvasProperty all hold children; a canvas
            // is a container as much as it is a picture, because its origin, delay and link
            // properties live underneath it.
            case WzConvexProperty:
            case WzSubProperty:
            case WzCanvasProperty:
                foreach (WzImageProperty child in property.WzProperties ?? Enumerable.Empty<WzImageProperty>())
                {
                    node.Children.Add(PlanProperty(child, plan));
                }

                SortChildren(node);
                if (property is WzCanvasProperty canvas)
                {
                    PlanCanvas(canvas, plan, node);
                }

                break;

            case WzShortProperty s: node.Integer = s.Value; plan.TypeWidened++; break;
            case WzIntProperty i: node.Integer = i.Value; plan.TypeWidened++; break;
            case WzLongProperty l: node.Integer = l.Value; break;
            case WzFloatProperty f: node.Real = f.Value; plan.TypeWidened++; break;
            case WzDoubleProperty d: node.Real = d.Value; break;
            case WzStringProperty s: node.StringValue = s.Value; break;
            case WzVectorProperty v: node.VectorX = v.X.Value; node.VectorY = v.Y.Value; break;
            case WzBinaryProperty b: PlanAudio(b, plan, node); break;
            default: plan.TypeWidened++; break;
        }

        return node;
    }

    private static NxNodeType TypeOf(WzImageProperty property) => property switch
    {
        WzCanvasProperty => NxNodeType.Bitmap,
        WzBinaryProperty => NxNodeType.Audio,
        WzVectorProperty => NxNodeType.Vector,
        WzStringProperty or WzUOLProperty => NxNodeType.String,
        WzFloatProperty or WzDoubleProperty => NxNodeType.Real,
        WzShortProperty or WzIntProperty or WzLongProperty => NxNodeType.Integer,
        _ => NxNodeType.None,
    };

    // The payload is copied straight out of the source archive rather than decoded and re-encoded.
//
// This is not an optimisation, it is the difference between a usable writer and an unusable
// one ??and it is only correct because the tree is being copied rather than edited. An NX
// canvas is already a WebP stream that some NX reader can decode, and a writer that produces
// a different encoding of the same pixels has strictly gained nothing: it costs a full decode
// and a full encode per canvas, and it makes the output larger and in a different container
// (SkiaSharp emits VP8X where the shipped archives use VP8L, measured at about 65% more bytes).
//
// The measurement that forced this: re-encoding Mob.nx's 91,093 canvases ran for over half an
// hour without finishing. Character.nx holds 1,831,573 of them. Copying bytes takes the cost
// to nothing and keeps the archive byte-identical.
//
// An edit does change this: a canvas the user has touched no longer matches its source bytes,
// and the only way to find out is the one the caller passes through the WZ payload. So the
// condition is "this canvas is untouched", and a caller that edits one must route it here by
// other means. See the WZ route below for that case.
private static void PlanCanvas(WzCanvasProperty canvas, NxPlan plan, PlannedNode node)
    {
        if (canvas.PngProperty is not NxPngProperty nx)
        {
            // A canvas from a real .wz. It needs MapleLib's decoder, which is the same GDI+
            // path the editor already uses everywhere else.
            using var bitmap = canvas.PngProperty?.GetBitmap();
            node.CanvasWidth = (ushort)(bitmap?.Width ?? 0);
            node.CanvasHeight = (ushort)(bitmap?.Height ?? 0);
            node.BitmapIndex = (uint)plan.Bitmaps.Count;
            plan.Bitmaps.Add(bitmap is null ? [] : EncodeWebp(bitmap, plan));
            return;
        }

        node.CanvasWidth = nx.Canvas.Width;
        node.CanvasHeight = nx.Canvas.Height;
        node.BitmapIndex = (uint)plan.Bitmaps.Count;

        var (offset, length) = nx.Canvas.EncodedRange;
        byte[] encoded = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(
            new IntPtr(nx.Canvas.Archive.PointerAt(offset)), encoded, 0, (int)length);
        plan.Bitmaps.Add(encoded);
        plan.CanvasesCopied++;
    }

    private static byte[] EncodeWebp(Bitmap bitmap, NxPlan plan)
    {
        try
        {
            // System.Drawing's bitmap is converted through PNG on the way to Skia. That is a
            // needless intermediate in principle, but Skia has no direct bridge to GDI+ and
            // PNG is the one common denominator both libraries agree on losslessly.
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            using var decoded = SKBitmap.Decode(pngStream.ToArray());
            if (decoded is null)
            {
                plan.EncodingErrors++;
                return [];
            }

            using var image = SKImage.FromBitmap(decoded);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 100);
            return data is null ? [] : data.ToArray();
        }
        catch (Exception)
        {
            // Recorded rather than thrown: one un-encodable canvas should not cost the whole
            // archive, and the report is how the caller learns it happened.
            plan.EncodingErrors++;
            return [];
        }
    }

    private static void PlanAudio(WzBinaryProperty binary, NxPlan plan, PlannedNode node)
    {
        byte[] payload;
        NxAudio? source = NxAudioBytes.SourceOf(binary);

        if (source is { } nx)
        {
            // The NX form keeps an 82-byte sound header that the MapleLib property does not
            // hold, so the original payload is the only complete answer.
            var (offset, length) = nx.EncodedRange;
            payload = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(
                new IntPtr(nx.Archive.PointerAt(offset)), payload, 0, (int)length);
        }
        else
        {
            byte[] body = binary.GetBytes(false) ?? [];
            byte[] header = binary.Header ?? [];
            payload = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, payload, 0, header.Length);
            Buffer.BlockCopy(body, 0, payload, header.Length, body.Length);
        }

        int existing = plan.Audio.IndexOf(payload);
        node.AudioIndex = (uint)(existing >= 0 ? existing : plan.Audio.Count);
        node.AudioLength = (uint)payload.Length;
        if (existing < 0)
        {
            plan.Audio.Add(payload);
        }
    }

    /// <summary>
    /// Orders children the way NX readers expect: by their raw UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Duplicates keep their relative order, because the archive format permits them ??the
    /// reference corpus holds two nodes named "1029" under one image ??and an unstable sort
    /// would reorder those between runs.
    /// </remarks>
    private static void SortChildren(PlannedNode parent)
    {
        parent.Children.Sort(static (a, b) =>
        {
            int byBytes = CompareUtf8(a.Name, b.Name);
            return byBytes != 0 ? byBytes : a.OriginalOrder.CompareTo(b.OriginalOrder);
        });
    }

    private static int CompareUtf8(string a, string b) => string.CompareOrdinal(a, b);

    private static void BreadthFirst(NxPlan plan, PlannedNode root)
    {
        // The root is already at index 0; every other node is appended as its level is reached.
        var byIndex = new List<PlannedNode> { root };
        var queue = new Queue<PlannedNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            int level = queue.Count;
            for (int i = 0; i < level; i++)
            {
                PlannedNode node = queue.Dequeue();
                foreach (PlannedNode child in node.Children)
                {
                    byIndex.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        plan.Nodes.Clear();
        plan.Nodes.AddRange(byIndex);
    }

    private sealed class PlannedNode
    {
        internal PlannedNode(WzImageProperty? property, string name, NxNodeType type, int order)
        {
            Property = property;
            Name = name;
            Type = type;

            // Captured at construction, not later. The sort below breaks ties between
            // identically-named siblings by this number, and if it were still zero for
            // everything the tie-break would do nothing and List.Sort would leave
            // duplicates in an arbitrary order. That is not cosmetic: NX permits duplicate
            // child names, Quest.nx has two nodes called "0" under one Say.img branch, and a
            // reader addressing them by name gets whichever came first.
            OriginalOrder = order;
        }

        internal WzImageProperty? Property { get; }
        internal string Name { get; }
        internal NxNodeType Type { get; }
        internal List<PlannedNode> Children { get; } = [];

        internal int Index { get; set; }
        internal int OriginalOrder { get; set; }

        internal long Integer;
        internal double Real;
        internal string? StringValue;
        internal int VectorX;
        internal int VectorY;
        internal uint BitmapIndex;
        internal ushort CanvasWidth;
        internal ushort CanvasHeight;
        internal uint AudioIndex;
        internal uint AudioLength;
    }

    private sealed class NxPlan
    {
        internal List<PlannedNode> Nodes { get; } = [];
        internal List<byte[]> Bitmaps { get; } = [];
        internal List<byte[]> Audio { get; } = [];
        internal int Images { get; set; }
        internal int NextOrder;
        internal int CanvasesCopied;
        internal long TypeWidened { get; set; }
        internal int EncodingErrors { get; set; }
    }

    private sealed class StringPool
    {
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];

        internal StringPool()
        {
            Add(string.Empty); // index 0 is always the empty string, as in every real archive
        }

        internal int Count => _values.Count;

        internal uint Add(string value)
        {
            if (_ids.TryGetValue(value, out uint existing))
            {
                return existing;
            }

            uint id = (uint)_values.Count;
            _ids[value] = id;
            _values.Add(value);
            return id;
        }

        internal uint IdOf(string value) => _ids.TryGetValue(value, out uint id) ? id : Add(value);

        /// <summary>
        /// Writes the offset table and the data, returning the table's file offset.
        /// </summary>
        /// <remarks>
        /// Every offset is known before a byte is written ??the strings sit at fixed distances
        /// from the end of the table ??so this writes in one pass and patches nothing. The
        /// shape that reserved slots and patched them afterwards needed the writer's position
        /// threaded into the pool, which made two pieces depend on each other for no gain.
        /// </remarks>
        internal long WriteTo(NxSink w)
        {
byte[][] blobs = _values.Select(v => Encoding.UTF8.GetBytes(v)).ToArray();

            long tableOffset = w.Position;
            long dataStart = tableOffset + (long)blobs.Length * sizeof(ulong);

            var offsets = new ulong[blobs.Length];
            long cursor = dataStart;
            for (int i = 0; i < blobs.Length; i++)
            {
                offsets[i] = (ulong)cursor;
                cursor += 2 + blobs[i].Length;
            }

            foreach (ulong offset in offsets)
            {
                w.Write(offset);
            }

            foreach (byte[] blob in blobs)
            {
                w.Write((ushort)blob.Length);
                w.Write(blob);
            }

            return tableOffset;
        }
    }
}
/// <summary>What a write produced.</summary>
/// <param name="Nodes">Node records written.</param>
/// <param name="Images">Top-level images.</param>
/// <param name="Strings">Distinct strings in the pool.</param>
/// <param name="Canvases">Distinct canvas payloads.</param>
/// <param name="Audio">Distinct audio payloads.</param>
/// <param name="TypeWidened">Values whose WZ type could not be represented and were widened.</param>
/// <param name="EncodingErrors">Canvases that failed to encode; their payload is empty.</param>
public readonly record struct NxWriteReport(
    int Nodes,
    int Images,
    int Strings,
    int Canvases,
    int Audio,
    long TypeWidened,
    int EncodingErrors);











