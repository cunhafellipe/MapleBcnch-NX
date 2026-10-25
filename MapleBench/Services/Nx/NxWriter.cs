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
/// UTF-8 bytes, and the sortedness holds in every archive in the reference corpus —so a
/// writer that did not reproduce it would produce files those readers silently mis-resolve.
/// WZ has no ordering requirement, so this is a real difference: <b>node order is not
/// preserved</b>. Everything else —names, values, types, canvas sizes and children —is.
/// </para>
///
/// <para><b>Numbers widen.</b> NX has one 64-bit integer and one 64-bit float where WZ had
/// Short, Int, Long, Float and Double. A writer cannot widen <em>back</em>, so an NX archive
/// written from a WZ tree loses the distinction and reports it through
/// <see cref="NxWriteReport.TypeWidened"/> rather than pretending otherwise.
/// </para>
///
/// <para><b>Canvas encoding.</b> Pixels go out as WebP. Decoding is exact; encoding is
/// pixel-exact but not byte-identical to the originals —SkiaSharp emits a VP8X container
/// where the shipped archives use VP8L, at roughly 65% more bytes. Both are ordinary WebP and
/// both are readable; the file is larger.
/// </para>
/// </remarks>
public static unsafe class NxWriter
{
    /// <summary>
    /// The largest single byte[] this runtime will hand out, in bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the CLR's own ceiling for a single array, not a policy number: a byte[] tops out
    /// at 0x7FFFFFC4 because the object header shares the allocation with the length, so an
    /// <c>int.MaxValue</c>-length array cannot exist. Everything above it has to fail loudly
    /// rather than be discovered as an allocation error halfway through a write.
    /// </para>
    /// <para>
    /// <b>It is not the size of an archive.</b> It bounds ONE payload -- one canvas, one
    /// audio entry -- and nothing else. A whole archive is streamed to the destination and
    /// never held in one buffer, and the NX header's own table offsets are 64-bit, so the
    /// format has no size ceiling this writer can reach. The other two limits in this file
    /// are separate numbers under separate names:
    /// <see cref="MaxInMemoryArchiveBytes"/> bounds a whole archive, and only on the
    /// overload that builds one in memory; the streaming overload has no size limit at all.
    /// </para>
    /// </remarks>
    public const long MaxSingleBufferBytes = 0x7FFFFFC4;

    /// <summary>
    /// The largest whole archive this writer can build in memory, in bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overload that hands back a byte[] holds the archive in a MemoryStream, and a
    /// MemoryStream cannot grow past <c>int.MaxValue</c>. That is the real ceiling on that
    /// path, and it is a different quantity from <see cref="MaxSingleBufferBytes"/>: that
    /// one bounds a single payload, this one bounds the whole file.
    /// </para>
    /// <para>
    /// The streaming overload does not have this problem and does not have this limit: it
    /// writes the bytes out as it produces them.
    /// </para>
    /// </remarks>
    public const long MaxInMemoryArchiveBytes = int.MaxValue;

    /// <summary>
    /// Appends little-endian values to a seekable destination, and knows exactly how long it
    /// is at every moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to grow one in-memory byte[] to the size of the whole archive and hand it
    /// back as a single array. For Mob.nx that meant a 1.55 GB buffer, a second 1.55 GB copy
    /// made by ToArray, and a third when the caller wrote it out with File.WriteAllBytes —
    /// a 7,212 MB peak, measured. On a 9 GB machine that is an out-of-memory crash, and it
    /// happens after the gigabytes are already allocated, so there is no moment left to say
    /// anything useful.
    /// </para>
    /// <para>
    /// Now the bytes go straight to the destination and the archive is never held whole. A
    /// small staging buffer keeps the write count sane — one byte at a time to a FileStream
    /// would be 168 million calls for Map.nx's node block alone — and it is handed over and
    /// reused rather than grown.
    /// </para>
    /// <para>
    /// The two things that made the old buffer necessary are gone with it. The growth loop
    /// that could overflow an int and spin forever is gone, because there is no longer a
    /// length to double. And the patches no longer overwrite bytes in place: the destination
    /// is seekable, so a patch is a seek back, a write, and a seek forward. That is why the
    /// position is a long — the output can pass 2 GB, and a patch at an offset above 2 GB has
    /// to land in the right place.
    /// </para>
    /// </remarks>
    private sealed class NxSink
    {
        /// <summary>How much is staged in memory before it is handed to the destination.</summary>
        private const int StagingBytes = 1 << 16;

        private readonly Stream _stream;
        private readonly byte[] _staging = new byte[StagingBytes];
        private int _staged;
        private long _length;
        private readonly long _maxBytes;
        private readonly List<PendingPatch> _patches = new();

        /// <param name="stream">Where the archive goes.</param>
        /// <param name="maxBytes">
        /// The most this sink will write, or <see cref="long.MaxValue"/> for no limit at all.
        /// It is the destination's limit, not the format's: a MemoryStream cannot grow past
        /// <c>int.MaxValue</c>, and a file can hold anything.
        /// </param>
        internal NxSink(Stream stream, long maxBytes)
        {
            _stream = stream;
            _maxBytes = maxBytes;
        }

        /// <summary>Bytes written so far. A long, because the output can pass 2 GB.</summary>
        internal long Position => _length;

        /// <summary>
        /// A patch to apply once the whole archive has been written.
        /// </summary>
        /// <remarks>
        /// Deferred rather than applied immediately. Every patch target is behind the current
        /// write position — the audio and bitmap tables are patched after their own data has
        /// been written, and the header is patched last of all — so applying them at the end
        /// changes nothing about where they land, and it lets the whole run of a table be
        /// written in one go instead of a seek per entry.
        /// </remarks>
        private readonly record struct PendingPatch(long Offset, ulong Value, int Width);

        internal void Write(byte value)
        {
            Reserve(1);
            if (_staged == _staging.Length)
            {
                FlushStaged();
            }

            _staging[_staged++] = value;
            _length++;
        }

        internal void Write(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            Write(b);
        }

        internal void Write(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            Write(b);
        }

        internal void Write(ulong value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            Write(b);
        }

        internal void Write(long value) => Write((ulong)value);

        internal void Write(int value) => Write((uint)value);

        internal void Write(double value) => Write((ulong)BitConverter.DoubleToUInt64Bits(value));

        internal void Write(float value) => Write((uint)BitConverter.SingleToUInt32Bits(value));

        internal void Write(ReadOnlySpan<byte> value)
        {
            Reserve(value.Length);
            if (value.Length > _staging.Length)
            {
                // A canvas payload is bigger than the staging buffer and does not fit in it.
                // Hand it straight over rather than growing a buffer to hold it.
                FlushStaged();
                _stream.Write(value);
                _length += value.Length;
                return;
            }

            if (_staged + value.Length > _staging.Length)
            {
                FlushStaged();
            }

            value.CopyTo(_staging.AsSpan(_staged));
            _staged += value.Length;
            _length += value.Length;
        }

        internal void Write(byte[] value) => Write(value.AsSpan());

        /// <summary>Pads to a multiple of <paramref name="multiple"/>.</summary>
        internal void Align(int multiple)
        {
            while (_length % multiple != 0)
            {
                Write((byte)0);
            }
        }

        /// <summary>Overwrites four bytes already written.</summary>
        internal void PatchUInt32(long offset, uint value) =>
            _patches.Add(new PendingPatch(offset, value, 4));

        /// <summary>Overwrites eight bytes already written.</summary>
        internal void PatchUInt64(long offset, ulong value) =>
            _patches.Add(new PendingPatch(offset, value, 8));

        /// <summary>
        /// Flushes what is staged and applies every deferred patch, then leaves the
        /// destination positioned at the end of the archive.
        /// </summary>
        internal void Complete()
        {
            FlushStaged();
            if (_patches.Count == 0)
            {
                return;
            }

            // Sorted so the runs inside a table are written in order. A table's entries are
            // contiguous, so this turns three long runs of seeks into three seeks.
            _patches.Sort(static (x, y) => x.Offset.CompareTo(y.Offset));

            Span<byte> buffer = stackalloc byte[8];
            long end = _length;
            long cursor = -1;
            foreach (PendingPatch patch in _patches)
            {
                if (patch.Offset != cursor)
                {
                    _stream.Seek(patch.Offset, SeekOrigin.Begin);
                }

                for (int i = 0; i < patch.Width; i++)
                {
                    buffer[i] = (byte)(patch.Value >> (i * 8));
                }

                _stream.Write(buffer[..patch.Width]);
                cursor = patch.Offset + patch.Width;
            }

            _stream.Seek(end, SeekOrigin.Begin);
            _patches.Clear();
        }

        private void FlushStaged()
        {
            if (_staged == 0)
            {
                return;
            }

            _stream.Write(_staging, 0, _staged);
            _staged = 0;
        }

        private void Reserve(int extra)
        {
            // The limit is the destination's, not a property of the format. The streaming
            // writer has none -- the archive goes out as it is produced, and the NX header's
            // table offsets are 64-bit -- and the in-memory writer has the MemoryStream's own
            // int.MaxValue. What the check buys is a sentence that names the archive instead
            // of the MemoryStream's "Stream too long.", which says nothing about it.
            //
            // The comparison subtracts rather than adds, so it cannot overflow.
            if (_length > _maxBytes - extra)
            {
                throw new InvalidDataException(
                    $"This archive needs more than {_maxBytes:N0} bytes to write, which is more than " +
                    "this writer will hold in memory. Nothing was written.");
            }
        }
    }
    /// <summary>Serialises an archive to NX bytes, in memory.</summary>
    /// <remarks>
    /// Kept for the callers that want the bytes rather than a file: the merge path builds
    /// the whole archive and then moves it into place, and it never had a second copy of a
    /// 1.5 GB array to give away. The save path uses <see cref="Write(WzFile, Stream, out
    /// NxWriteReport)"/> instead, because holding the archive in memory is what made that
    /// path cost 7,212 MB.
    /// </remarks>
    public static byte[] Write(WzFile file, out NxWriteReport report)
    {
        ArgumentNullException.ThrowIfNull(file);

        using MemoryStream buffer = new();
        Write(file, buffer, MaxInMemoryArchiveBytes, out report);

        // GetBuffer, not ToArray: ToArray copies the whole archive again, and this is
        // exactly the copy the stream overload exists to avoid. The slice is the logical
        // length even when the underlying array is larger.
        return buffer.GetBuffer()[..(int)buffer.Length];
    }

    /// <summary>
    /// Serialises an archive straight into a seekable stream, and reports what it wrote.
    /// </summary>
    /// <remarks>
    /// The destination must be seekable: the header's offsets and the two offset tables are
    /// patched after the fact, and a patch on a stream that cannot seek backwards is not a
    /// patch. The archive is never held whole — the bytes go to the destination as they are
    /// produced — so the peak memory of a save is the plan's canvas copies and not a second
    /// copy of the output.
    /// <para>
    /// There is no size limit here. The bytes go out as they are produced, the header's
    /// table offsets are 64-bit, and the reader reads them as 64-bit, so there is no
    /// archive size this writer cannot write. The limit on the in-memory overload is that
    /// overload's own business, and it is a different number.
    /// </para>
    /// </remarks>
    public static void Write(WzFile file, Stream destination, out NxWriteReport report)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanSeek)
        {
            throw new ArgumentException(
                "The destination must be seekable: the archive's header and offset tables are patched " +
                "after they are written.",
                nameof(destination));
        }

        Write(file, destination, long.MaxValue, out report);
    }

    /// <summary>
    /// The writer itself, with the destination's own size limit.
    /// </summary>
    /// <remarks>
    /// <paramref name="maxBytes"/> is the most the destination can hold: a MemoryStream's
    /// <c>int.MaxValue</c>, or <see cref="long.MaxValue"/> for a stream that has no limit of
    /// its own. The refusal is made here rather than left to the destination, because the
    /// destination's own exception says nothing about the archive that caused it.
    /// </remarks>
    private static void Write(WzFile file, Stream destination, long maxBytes, out NxWriteReport report)
    {
        var plan = Plan(file);
        var strings = BuildStringPool(plan);

        var w = new NxSink(destination, maxBytes);

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
            w.PatchUInt64(audioOffset + i * 8, (ulong)audioOffsets[i]);
        }

        // ---- bitmap table, then bitmap data ----
        long bitmapOffset = w.Position;
        var bitmapOffsets = new List<long>();
        foreach (BitmapPayload _ in plan.Bitmaps)
        {
            bitmapOffsets.Add(0);
            w.Write(0UL);
        }

        for (int i = 0; i < plan.Bitmaps.Count; i++)
        {
            w.Align(8);
            long at = w.Position;
            w.Write(plan.Bitmaps[i].Length);
            plan.Bitmaps[i].WriteTo(w);
            bitmapOffsets[i] = at;
        }

        for (int i = 0; i < plan.Bitmaps.Count; i++)
        {
            w.PatchUInt64(bitmapOffset + i * 8, (ulong)bitmapOffsets[i]);
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

        w.Complete();
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
            // An NX image opened lazily is a shell: the name is there and the
            // properties are not. Reading WzProperties off one without asking
            // for it first yields nothing, and writing that nothing is how a
            // 186 MB archive becomes a 282 KB one -- every image that was never
            // browsed simply missing from the file, with a success message and
            // a backup nobody thought to need. The writer is the one place that
            // must never take an image on trust.
            //
            // Building here costs what the old eager open cost, which is the
            // point: the archive has to be laid out in full to be written in
            // full. What laziness buys is not paying that until the user saves.
            NxImageBuilders.BuildIfPending(image);

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
        // A property standing for an NX node may already have been planned, because two parents
        // in the source shared it. Reuse the node rather than writing a second copy: a child is
        // stored as an index into the node block, so emitting one record and pointing both
        // parents at it is not a compression trick, it is exactly what the source does. Every
        // multi-parent node in the corpus shares an identical child range, so this reproduces
        // the source structure rather than approximating it.
        int origin = NxNodeOrigin.IndexOf(property);
        if (origin >= 0 && plan.ByOrigin.TryGetValue(origin, out var existing))
        {
            return existing;
        }

        var node = new PlannedNode(property, property.Name, TypeOf(property), plan.NextOrder++);
        if (origin >= 0)
        {
            plan.ByOrigin[origin] = node;
        }
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
// one —and it is only correct because the tree is being copied rather than edited. An NX
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
            plan.Bitmaps.Add(BitmapPayload.Owned(bitmap is null ? [] : EncodeWebp(bitmap, plan)));
            return;
        }

        node.CanvasWidth = nx.Canvas.Width;
        node.CanvasHeight = nx.Canvas.Height;

        // Dedupe on the NX canvas index, not on the payload bytes. The archive's bitmap table is
        // already shared — many nodes point at one entry — and re-appending the same payload for
        // each of them is what left a written UI.nx at 127% with every node count already
        // matching. One entry per source index, pointed at by all of them, is the source's own
        // arrangement.
        int key = (int)nx.Canvas.Index;
        if (plan.BitmapByOrigin.TryGetValue(key, out uint reused))
        {
            node.BitmapIndex = reused;
            return;
        }

        node.BitmapIndex = (uint)plan.Bitmaps.Count;
        plan.BitmapByOrigin[key] = node.BitmapIndex;

        var (offset, length) = nx.Canvas.EncodedRange;
        RequireFitsOneBuffer(length, "canvas");

        // The payload is NOT copied here.  The archive it lives in is still mapped, and
        // the bytes are written straight out of it when the bitmap block runs
        // (BitmapPayload.WriteTo).  What this plan used to hold was a byte[] copy of
        // every canvas in the archive: Mob.nx's canvases are 1,532,315,924 bytes, and
        // MEASURED 2026-10-22 the save of Mob.nx peaked at 2,346.9 MB of PRIVATE memory
        // because of them (task-71).  The copy bought nothing: the bytes are the same
        // bytes the mapping holds, and they are written in the same order.
        //
        // The cost of not copying is a lifetime requirement, and it is stated here
        // rather than left to be discovered: the source archive must stay MAPPED until
        // the write has finished.  Every caller that reaches here holds the session
        // gate for the whole write (WzSaveService.Save, NxMergeService), and closing
        // an archive takes the same gate, so the mapping cannot be torn down underneath
        // the writer.
        plan.Bitmaps.Add(BitmapPayload.Mapped(nx.Canvas.Archive, offset, length));
        plan.CanvasesPlanned++;
    }

    /// <summary>
    /// Refuses a payload that no single buffer can hold, before the allocation that
    /// would throw something meaningless.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The payload length is a <c>uint</c> read out of the archive, and both callers
    /// of this check are bounded by <see cref="int.MaxValue"/> -- for two different
    /// reasons. The AUDIO path copies the payload into a <c>byte[]</c>, so it is
    /// bounded twice: by int.MaxValue for the conversion and by the CLR's array
    /// ceiling (0x7FFFFFC4) for the allocation. The CANVAS path does not copy any
    /// more -- it writes the payload as one span, and a span's length is an int -- so
    /// the same ceiling applies to it for one reason instead of two. Both limits fail
    /// with a message that says nothing about the archive:
    /// </para>
    /// <list type="bullet">
    /// <item><description>over int.MaxValue: "Arithmetic operation resulted in an overflow."</description></item>
    /// <item><description>over the array ceiling: "Array dimensions exceeded supported range."</description></item>
    /// </list>
    /// <para>
    /// MEASURED 2026-10-22 on a hand-built NX whose single audio payload was
    /// 2,147,483,648 bytes: the save answered HTTP 500 with
    /// <c>message: "Something went wrong."</c> and that overflow text hidden in
    /// <c>detail</c>. A tester would read that as their own mistake. The check is here
    /// instead so the sentence names the archive, the payload and the limit.
    /// </para>
    /// <para>
    /// The ceiling is <see cref="MaxSingleBufferBytes"/>: the largest byte[] this runtime
    /// will hand out, and the largest span it can address. It bounds ONE payload. It is
    /// not the size of the archive -- a whole archive is streamed out and the format's
    /// offsets are 64-bit -- and the archive-wide limit is the separate
    /// <see cref="MaxInMemoryArchiveBytes"/>, which applies only to the overload that
    /// builds one in memory.
    /// </para>
    /// <para>
    /// MEASURED 2026-10-22 on <c>C:\mbtest2\payload-over-2gb.nx</c>: that archive's
    /// oversized payload is an AUDIO payload (read out of the file: one bitmap entry of
    /// 16 bytes and one audio entry), so the canvas path is not what refused it and
    /// the audio path still refuses it. Dropping the canvas copy did not change what
    /// this check refuses.
    /// </para>
    /// </remarks>
    private static void RequireFitsOneBuffer(uint length, string what)
    {
        if (length > MaxSingleBufferBytes)
        {
            throw new InvalidDataException(
                $"This archive holds a {length:N0}-byte {what} payload, which is larger than the " +
                $"{MaxSingleBufferBytes:N0}-byte limit of a single buffer. It cannot be written, and nothing " +
                "was written: the file on disk is unchanged.");
        }
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
            RequireFitsOneBuffer(length, "audio");
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
    /// Duplicates keep their relative order, because the archive format permits them —the
    /// reference corpus holds two nodes named "1029" under one image —and an unstable sort
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
        //
        // A node already in the list is NOT appended again. NX stores a child as an index into
        // this block, so one record referenced from two parents is a shared subtree, not a
        // duplicate one, and the corpus is kind about it: every one of Character.nx's 227,783
        // shared nodes has both parents at the same depth, so first-visit-wins lays the shared
        // block down once and both parents point at it. Appending it twice is exactly what made
        // a written Character.nx three times the size of the source.
        var byIndex = new List<PlannedNode> { root };
        var queued = new HashSet<PlannedNode>(ReferenceEqualityComparer.Instance) { root };
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
                    if (!queued.Add(child))
                    {
                        continue;
                    }

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

    /// <summary>
    /// One canvas payload on its way into the archive: either bytes this writer
    /// produced, or a range inside the source archive's mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There are two shapes because there are two sources. A canvas decoded from a
    /// classic .wz is re-encoded to WebP and the result is ours, so it is kept as
    /// bytes. A canvas that came out of an NX archive is already an encoded stream,
    /// and the archive it lives in is still mapped -- so the payload is a
    /// <b>(offset, length)</b> pair and the bytes are written straight out of the
    /// mapping when the bitmap block runs.
    /// </para>
    /// <para>
    /// <b>Why the mapped shape exists.</b> The plan used to hold a <c>byte[]</c> copy
    /// of every canvas in the archive, and every one of those copies was alive at
    /// the same time, because the plan is filled before anything is written. For
    /// Mob.nx that is 1,532,315,924 bytes of canvas payload, and MEASURED
    /// 2026-10-22 the save peaked at 2,346.9 MB of private memory with the copy
    /// (task-71). The copy bought nothing: the bytes are the same bytes the
    /// mapping holds, in the same order, and the output is byte-identical either
    /// way -- verified with an independent NX reader, not with the writer's own
    /// report.
    /// </para>
    /// <para>
    /// <b>What the mapped shape requires.</b> The source archive has to stay mapped
    /// for as long as the write runs. Every caller holds the session gate for the
    /// whole write, and taking an archive out of the session takes the same gate,
    /// so the mapping cannot be released underneath the writer. The archive
    /// reference is kept here for the same reason: it keeps the object alive, so
    /// the mapping cannot be collected while the pointer into it is still held.
    /// </para>
    /// </remarks>
    private sealed class BitmapPayload
    {
        private readonly byte[]? _owned;
        private readonly NxArchive? _archive;
        private readonly long _offset;
        private readonly uint _length;

        private BitmapPayload(byte[]? owned, NxArchive? archive, long offset, uint length)
        {
            _owned = owned;
            _archive = archive;
            _offset = offset;
            _length = length;
        }

        /// <summary>Bytes this writer produced (a classic .wz canvas, re-encoded).</summary>
        internal static BitmapPayload Owned(byte[] bytes) => new(bytes, null, 0, 0);

        /// <summary>A range inside a mapped archive, written without a copy.</summary>
        internal static BitmapPayload Mapped(NxArchive archive, long offset, uint length) =>
            new(null, archive, offset, length);

        /// <summary>How many bytes go into the archive for this payload.</summary>
        internal uint Length => _owned is not null ? (uint)_owned.Length : _length;

        /// <summary>Writes the payload into the archive.</summary>
        internal void WriteTo(NxSink w)
        {
            if (_owned is not null)
            {
                w.Write(_owned);
                return;
            }

            if (_archive is null)
            {
                throw new InvalidOperationException(
                    "A canvas payload has neither bytes nor a source archive, so it cannot be written.");
            }

            // Straight out of the mapping.  The span's length is an int, which is why
            // RequireFitsOneBuffer still refuses a canvas payload larger than one
            // buffer before it gets here.
            byte* pointer = _archive.PointerAt(_offset);
            w.Write(new ReadOnlySpan<byte>(pointer, (int)_length));
        }
    }

    private sealed class NxPlan
    {
        internal List<PlannedNode> Nodes { get; } = [];
        internal List<BitmapPayload> Bitmaps { get; } = [];
        internal List<byte[]> Audio { get; } = [];
        internal int Images { get; set; }
        internal int NextOrder;
        internal Dictionary<int, PlannedNode> ByOrigin { get; } = new();
        internal Dictionary<int, uint> BitmapByOrigin { get; } = new();
        internal int CanvasesPlanned;
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
        /// Every offset is known before a byte is written —the strings sit at fixed distances
        /// from the end of the table —so this writes in one pass and patches nothing. The
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











