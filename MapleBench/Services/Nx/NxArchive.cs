using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace MapleBench.Services.Nx;

/// <summary>
/// The seven value shapes an NX node can hold.
/// </summary>
/// <para>
/// These are the whole type system. There is no <c>short</c>/<c>int</c>/<c>long</c>
/// split and no <c>float</c>/<c>double</c> split, because NX predates the WZ types it
/// was modelled on and never grew them — <see cref="Integer"/> is one 64-bit slot and
/// <see cref="Real"/> is one 64-bit float. Anything the client needs to tell apart has
/// to be told apart by its <em>name</em>.
/// </para>
/// </remarks>
public enum NxNodeType : ushort
{
    /// <summary>A container: <c>.img</c> nodes and every grouping node are this.</summary>
    None = 0,

    /// <summary>Signed 64-bit. Collapses WZ's Short, Int and Long.</summary>
    Integer = 1,

    /// <summary>IEEE-754 double. Collapses WZ's Float and Double.</summary>
    Real = 2,

    /// <summary>Index into the archive's shared string pool.</summary>
    String = 3,

    /// <summary>Two signed 32-bit integers — a point, not a direction.</summary>
    Vector = 4,

    /// <summary>Index, width and height. The pixels live in the bitmap table.</summary>
    Bitmap = 5,

    /// <summary>Index and byte length. The bytes live in the audio table.</summary>
    Audio = 6,
}

/// <summary>
/// Reads an NX archive (<c>PKG4</c>), MapleStory's post-WZ data format.
/// </summary>
/// <para>
/// <b>Why this class exists and why it looks nothing like the WZ path.</b> A <c>.wz</c>
/// archive is an encrypted, versioned, self-describing tree: MapleBench has to guess
/// which of GMS/EMS/BMS AES key fits it, derive an IV, and if the guess is wrong it
/// re-detects from scratch (<c>WzSessionService.ResolveEncryption</c>). NX has none of
/// that. The file is plaintext, unversioned and unkeyed — the first four bytes say what
/// it is and every field after them is a fixed-width little-endian integer. So the whole
/// "which encryption is this" question, which is most of the WZ open path's complexity
/// and most of its failure modes, simply does not exist here.
/// </para>
///
/// <para><b>Layout.</b> A 52-byte header points at four tables laid out in this order:</para>
/// <code>
/// Header (52B) | Node block (20B x NodeCount) | String offset table | String data
///            | Bitmap offset table | Bitmap data | Audio offset table | Audio data
/// </code>
/// <para>
/// Nodes are stored <b>breadth-first, not as a recursive tree</b>. Each record holds the
/// <em>index of its first child</em> in the node block rather than a pointer or an offset,
/// so a subtree is a contiguous index range. That is why the format has no cycles to guard
/// against and why a node costs exactly 20 bytes regardless of how deep it is.
/// </para>
///
/// <para><b>Strings are pooled across the whole archive.</b> <c>UI.nx</c> holds 548,956
/// nodes against 113,708 distinct strings — node names and string values share one
/// deduplicated table. That is the reason an NX file of 545 MB is not 545 MB of names.</para>
///
/// <para><b>Memory.</b> The whole file is mapped, not read. <c>Map.nx</c> is 679 MB with
/// 8.4 million nodes; materialising that would be fatal, and the node table is exactly the
/// shape that wants random access — every node is a fixed 20 bytes at a known index. Pages
/// fault in on demand and the operating system evicts them under pressure.</para>
///
/// <para><b>Not encrypted.</b> This is stated rather than assumed because it is the single
/// biggest difference from the WZ path, and because "the client's data is encrypted" is a
/// reasonable prior to bring from WZ that turns out to be wrong here. The only compression
/// anywhere in the format is LZ4/WebP on canvas pixels; the tree, the strings, the audio
/// and every offset table are stored as-is.</para>
///
/// <para><b>Provenance.</b> Written clean-room from the on-disk format and the public
/// specification at <c>nxformat.github.io</c> (CC BY-SA 3.0). No code was copied from any
/// existing NX library; see the project's NX technical route notes for the licence
/// reasoning that forced it.</para>
/// </summary>
public sealed unsafe class NxArchive : IDisposable
{
    /// <summary>
    /// <c>PKG4</c> read as a little-endian <c>uint</c>. The bytes on disk are
    /// <c>50 4B 47 34</c>.
    /// </summary>
    public const uint Magic = 0x34474B50;

    /// <summary>Header size in bytes. The node block conventionally starts immediately after.</summary>
    public const int HeaderSize = 52;

    /// <summary>
    /// Every node is exactly this many bytes: name, first-child index and child count
    /// (8 bytes) plus a type tag and a fixed 8-byte payload (10 bytes).
    /// </summary>
    public const int NodeRecordSize = 20;

    private MemoryMappedFile _map;
    private MemoryMappedViewAccessor _view;
    private byte* _base;

    /// <summary>String offsets, read once. <c>string_count</c> is small even in huge files.</summary>
    private ulong[] _stringOffsets;

    private ulong[] _bitmapOffsets;
    private ulong[] _audioOffsets;

    private long _fileLength;

    private bool _disposed;

    /// <summary>
    /// Parameterless because the mapping is established separately by <see cref="Map"/>.
    /// </summary>
    private NxArchive()
    {
    }

    /// <summary>Number of node records in the archive.</summary>
    public int NodeCount { get; private set; }

    /// <summary>Byte offset of the node block.</summary>
    public ulong NodeOffset { get; private set; }

    /// <summary>Size of the archive-wide deduplicated string pool.</summary>
    public int StringCount { get; private set; }

    /// <summary>Byte offset of the string offset table.</summary>
    public ulong StringOffset { get; private set; }

    /// <summary>Number of distinct canvas payloads.</summary>
    public int BitmapCount { get; private set; }

    /// <summary>Byte offset of the bitmap offset table.</summary>
    public ulong BitmapOffset { get; private set; }

    /// <summary>Number of distinct audio payloads.</summary>
    public int AudioCount { get; private set; }

    /// <summary>Byte offset of the audio offset table.</summary>
    public ulong AudioOffset { get; private set; }

    /// <summary>
    /// The archive's true size on disk, in bytes.
    /// </summary>
    /// <remarks>
    /// Taken from the file itself rather than the mapping's capacity. A memory-mapped view is
    /// rounded up to an allocation granularity, so a 1,376-byte archive reports 4,096 — which
    /// would make every offset comparison in <see cref="Validate"/> compare against a number
    /// slightly larger than reality and quietly accept a truncated file.
    /// </remarks>
    public long FileLength => _fileLength;

    /// <summary>The archive's root node. In practice this is the unnamed node owning the <c>*.img</c> children.</summary>
    public NxNode Root { get; private set; }

    /// <summary>
    /// The node at a given index in the node block, independent of any path to it.
    /// </summary>
    /// <remarks>
    /// This is the format's native random-access primitive — a node really is record
    /// <c>index</c> — so it is exposed rather than hidden behind a tree walk. Returns
    /// <see cref="NxNode.None"/> for an index outside the block, so a stale index from a
    /// different archive degrades to "absent" instead of reading someone else's data.
    /// </remarks>
    public NxNode NodeAt(int index) => (uint)index < (uint)NodeCount ? new NxNode(this, index) : NxNode.None;

    /// <summary>
    /// True if the first four bytes are <c>PKG4</c>.
    /// </summary>
    /// <remarks>
    /// This reads four bytes and nothing else, deliberately. It is the guard the open path
    /// runs <em>before</em> deciding anything else, so it has to be cheap enough to call on
    /// every file a folder scan touches, including files that turn out not to be NX.
    /// </remarks>
    public static bool IsNxFile(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 && BitConverter.ToUInt32(magic) == Magic;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Maps an NX archive read-only.</summary>
    public static NxArchive Open(string path)
    {
        var archive = new NxArchive();
        archive.Map(path);
        return archive;
    }

    /// <summary>
    /// The node count from the header, or 0 when it cannot be read.
    ///
    /// Costs the same one small read as <see cref="IsNxFile"/> and no mapping,
    /// because it exists to be called <em>before</em> the conversion it is
    /// sizing: an NX open builds the whole tree as MapleLib objects, so the node
    /// count is the number that predicts what that will cost. Sizing on file
    /// size instead was wrong by 7.4x on Character.nx -- a 186 MB archive that
    /// needs 5.6 GB to open, against a 2x-size estimate of 757 MB. The guard
    /// that estimate feeds is the only thing between an NX open and an
    /// out-of-memory kill, and it was under-predicting on precisely the
    /// archives that blow up.
    /// </summary>
    public static int HeaderNodeCount(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) < 8 || BitConverter.ToUInt32(header) != Magic) return 0;
            return (int)BitConverter.ToUInt32(header[4..]);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Points this instance at the archive at <paramref name="path"/>, replacing any mapping
    /// it already holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <see cref="NxCanvas"/> captured at conversion time holds a reference to the
    /// <see cref="NxArchive"/> it was built from, and a tree full of them is not reachable from
    /// here — <c>NxToWzFile.Convert</c> hands the caller both and never keeps a list. That is
    /// why an in-place save cannot simply swap a new archive into the session: every canvas
    /// would still be pointing at the old one, and the next decode would read through a
    /// released pointer. Remapping in place keeps every canvas valid because every canvas
    /// points at <em>this</em> object.
    /// </para>
    /// <para>
    /// The old mapping is released before the new one is opened, because Windows will not
    /// rename or replace a file that is still mapped. That ordering is the whole reason this
    /// method exists.
    /// </para>
    /// </remarks>
    private void Map(string path)
    {
        ReleaseMapping();

        FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long fileLength = file.Length;

        MemoryMappedFile map;
        MemoryMappedViewAccessor view;
        byte* basePtr = null;
        try
        {
            // leaveOpen: false — the FileStream exists only to open the mapping, and closing it
            // here means no path handle stays resident for the life of the session. The earlier
            // WZ path had to be careful about exactly this (holding a 139 MB pack open for the
            // life of a session), so it is closed at the source rather than left to a Dispose
            // someone has to remember.
            map = MemoryMappedFile.CreateFromFile(
                file,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.Read,
                inheritability: HandleInheritability.None,
                leaveOpen: false);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        bool acquired = false;
        try
        {
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            acquired = true;

            // Capacity is checked before the first byte is touched. A zero- or three-byte file
            // is a real thing to meet — a truncated download, a file someone renamed — and
            // reading the magic out of it would be an access violation rather than a message.
            if (view.Capacity < HeaderSize)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' is {view.Capacity} bytes, too short to hold an NX header.");
            }

            uint magic = Unsafe.ReadUnaligned<uint>(basePtr);
            if (magic != Magic)
            {
                throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' is not an NX archive: expected PKG4 magic, found 0x{magic:X8}.");
            }

            _map = map;
            _view = view;
            _base = basePtr;
            _fileLength = fileLength;
            _disposed = false;

            ReadTables(path);
            Validate(path);
        }
        catch
        {
            if (acquired)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            view.Dispose();
            map.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Re-reads this archive's four tables from the mapping just established.
    /// </summary>
    /// <remarks>
    /// Pulled out of the constructor so a remap repopulates them. A written archive keeps the
    /// same node names and payload references only because the writer reproduces the source's
    /// layout, but nothing guarantees a future writer would, and reading a table that described
    /// the previous file would turn a save into a corruption.
    /// </remarks>
    private void ReadTables(string path)
    {
        NodeCount = ReadCount(path, 0x04, "node", NodeRecordSize);
        NodeOffset = ReadOffset(path, 0x08, "node block");
        StringCount = ReadCount(path, 0x10, "string", sizeof(ulong));
        StringOffset = ReadOffset(path, 0x14, "string offset table");
        BitmapCount = ReadCount(path, 0x1C, "bitmap", sizeof(ulong));
        BitmapOffset = ReadOffset(path, 0x20, "bitmap offset table");
        AudioCount = ReadCount(path, 0x28, "audio", sizeof(ulong));
        AudioOffset = ReadOffset(path, 0x2C, "audio offset table");

        _stringOffsets = ReadOffsetTable(StringOffset, StringCount);
        _bitmapOffsets = ReadOffsetTable(BitmapOffset, BitmapCount);
        _audioOffsets = ReadOffsetTable(AudioOffset, AudioCount);

        Root = new NxNode(this, 0);
    }

    /// <summary>
    /// Reads a header COUNT field and refuses anything the file cannot physically hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These four fields are stored as <c>uint</c> and were read straight into an
    /// <see cref="int"/>. The project builds unchecked — no
    /// <c>CheckForOverflowUnderflow</c>, no <c>Directory.Build.props</c> — so
    /// <c>0xFFFFFFFF</c> arrived as <c>-1</c>, silently.
    /// </para>
    /// <para>
    /// That is not a wrong number, it is a wrong <em>invariant</em>, and the arithmetic
    /// downstream turned it back into something that looked fine.
    /// <c>Validate</c> computes <c>NodeOffset + (ulong)NodeCount * NodeRecordSize</c>,
    /// and C#'s <c>(ulong)someInt</c> sign-extends rather than masking: <c>(ulong)(-1)</c>
    /// is 18,446,744,073,709,551,615, which times 20 wraps modulo 2^64 to a small number.
    /// Measured, with the arithmetic reproduced in BigInteger:
    /// </para>
    /// <code>
    /// NodeCount = -1        -> nodeBlockEnd = 32      -> RequireFit PASSES
    /// NodeCount = -2        -> nodeBlockEnd = 12      -> RequireFit PASSES
    /// NodeCount = -3        -> nodeBlockEnd = 18446744073709551608 -> rejected
    /// NodeCount = int.MinValue -> nodeBlockEnd = 18446744030759878708 -> rejected
    /// </code>
    /// <para>
    /// So exactly two values slipped through, and the archive opened as valid. From there
    /// <c>NxNode.IsValid</c> is <c>(uint)_index &lt; (uint)_archive.NodeCount</c>, and
    /// <c>(uint)(-1)</c> is 0xFFFFFFFF, so <em>every</em> index passed. The first child index
    /// in the node block then reached <c>NodeNameId</c>, which reads twenty bytes per record
    /// straight off the mapping. Measured on a 132-byte file with
    /// <c>nodeCount = 0xFFFFFFFF</c> and one root child index of 0x7FFFFFFF:
    /// </para>
    /// <code>
    /// System.AccessViolationException
    ///    at MapleBench.Services.Nx.NxArchive.ReadUInt32(Int64)
    ///    at MapleBench.Services.Nx.NxArchive.NodeNameId(Int32)
    ///    at MapleBench.Services.Nx.NxNode.get_Name()
    ///    at MapleBench.Services.Nx.NxToWzFile.Convert(NxArchive, NxLoadMode)
    /// </code>
    /// <para>
    /// <b>Why here and not in <see cref="Validate"/>.</b> <c>ReadTables</c> runs first, and
    /// <c>ReadOffsetTable</c> allocates <c>new ulong[count]</c> from the very number being
    /// validated — a negative count, or one the file cannot hold, has to be refused before
    /// anything allocates from it.
    /// </para>
    /// <para>
    /// The bound is the file's own size rather than a constant: every entry of every one of
    /// these four tables occupies at least <paramref name="bytesPerEntry"/> bytes on disk, so
    /// a count larger than the file can hold is corrupt by definition, whatever the number.
    /// </para>
    /// </remarks>
    private int ReadCount(string path, int headerOffset, string what, int bytesPerEntry)
    {
        uint raw = ReadUInt32(headerOffset);

        // Reject the wrap before the cast, not after it. 0xFFFFFFFF is above
        // int.MaxValue, so this one comparison kills every negative the unchecked
        // cast could produce. The second check is what stops a plausible-looking
        // 200,000,000 from allocating a gigabyte of offset table.
        if (raw > int.MaxValue)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' claims {raw} {what} entries, which does not fit in the " +
                "header's own counter. The file is truncated or corrupt.");
        }

        int count = (int)raw;
        long available = _fileLength - NxArchive.HeaderSize;
        if ((long)count * bytesPerEntry > available)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' claims {count:N0} {what} entries, which need " +
                $"{count * (long)bytesPerEntry:N0} bytes in a {available:N0}-byte file. " +
                "The file is truncated or corrupt.");
        }

        return count;
    }

    /// <summary>
    /// Reads a header OFFSET field and refuses one that is not inside the file.
    /// </summary>
    /// <remarks>
    /// Validate compares these arithmetically against the counts, and all four of those
    /// expressions can wrap, so the field has to be known-good before it is used as an
    /// operand. An offset past the end of the mapping is a wild pointer waiting for the
    /// first read through it.
    /// </remarks>
    private ulong ReadOffset(string path, int headerOffset, string what)
    {
        ulong offset = ReadUInt64(headerOffset);
        if (offset > (ulong)_fileLength)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' puts its {what} at 0x{offset:X}, past the end of a " +
                $"{_fileLength}-byte file. The file is truncated or corrupt.");
        }

        return offset;
    }

    /// <summary>
    /// Drops the mapping without disposing this instance, so a caller can point the same
    /// object at a different file.
    /// </summary>
    private void ReleaseMapping()
    {
        if (_view is null)
        {
            return;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
        _base = null;
    }

    /// <summary>
    /// Re-points this archive at <paramref name="path"/>, which is expected to hold an
    /// equivalent archive written since this one was mapped.
    /// </summary>
    /// <remarks>
    /// Used by an in-place save. Every canvas built from this archive holds a reference to
    /// <em>this</em> instance, so refreshing it in place leaves the whole tree able to decode
    /// against the new bytes, which replacing the object could not do.
    /// </remarks>
    public void Remap(string path) => Map(path);

    /// <summary>
    /// Drops the mapping but leaves this instance usable, so <see cref="Remap"/> can map the
    /// same file again.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Dispose"/>, which is permanent: an NX canvas captured at
    /// conversion time points at this instance, and a disposed one would turn the next decode
    /// into an access violation instead of an error. An in-place save has to release the
    /// mapping because Windows refuses to replace a mapped file, and then put it back.
    /// </remarks>
    public void Release() => ReleaseMapping();

    /// <summary>
    /// Checks that the four tables do not overlap and that the counts fit the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method checks where each table <em>ends</em>, that the four do not overlap, and
    /// that the counts are consistent with the file's length. It does not look inside a
    /// table, and it does not check the header's own fields — those are checked where they
    /// are read, at the point of use:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the header counts and offsets, in <see cref="ReadTables"/>;</description></item>
    /// <item><description>a string offset-table entry, in <see cref="GetString"/>;</description></item>
    /// <item><description>a bitmap offset-table entry, in <see cref="GetBitmapRange"/>;</description></item>
    /// <item><description>a payload's byte range, in <see cref="NxCanvas.EncodedRange"/>;</description></item>
    /// <item><description>canvas dimensions, in <see cref="NxCanvas.DecodedLength"/>.</description></item>
    /// </list>
    /// <para>
    /// That split is deliberate rather than tidiness: each of those checks sits at the
    /// dereference it guards, so a caller that reaches the value by any path gets the check.
    /// This is not a claim that the checks above are the only invariants the format has —
    /// it is where each of them lives.
    /// </para>
    /// </remarks>
    private void Validate(string path)
    {
        string name = Path.GetFileName(path);
        ulong length = (ulong)FileLength;

        void RequireFit(ulong end, string what)
        {
            if (end > length)
            {
                throw new InvalidDataException(
                    $"'{name}' is truncated: its {what} would end at 0x{end:X}, past the end of a {length}-byte file.");
            }
        }

        void RequireOrder(ulong end, ulong nextStart, string what, string next)
        {
            RequireFit(end, what);
            if (end > nextStart)
            {
                throw new InvalidDataException(
                    $"'{name}' is malformed: its {what} would end at 0x{end:X}, overlapping {next} at 0x{nextStart:X}.");
            }
        }

        ulong nodeBlockEnd = NodeOffset + (ulong)NodeCount * NodeRecordSize;
        ulong stringTableEnd = StringOffset + (ulong)StringCount * sizeof(ulong);
        ulong audioTableEnd = AudioOffset + (ulong)AudioCount * sizeof(ulong);
        ulong bitmapTableEnd = BitmapOffset + (ulong)BitmapCount * sizeof(ulong);

        // Order is node block, string offset table, audio offset table, bitmap offset table,
        // with each table's payload sitting in the gap before the next. Audio comes *before*
        // bitmap — which is the opposite of the order the header's field list suggests, and it
        // is worth stating because getting it wrong rejects three archives in a sixteen-file
        // corpus as malformed. The offsets are populated even when a table is empty, so the
        // chain still holds for files with no audio or no bitmaps.
        RequireOrder(nodeBlockEnd, StringOffset, "node block", "the string offset table");
        RequireOrder(stringTableEnd, AudioOffset, "string offset table", "the audio offset table");
        RequireOrder(audioTableEnd, BitmapOffset, "audio offset table", "the bitmap offset table");
        RequireFit(bitmapTableEnd, "bitmap offset table");
    }

    private ulong[] ReadOffsetTable(ulong tableOffset, int count)
    {
        if (count == 0)
        {
            return [];
        }

        long required = (long)tableOffset + (long)count * sizeof(ulong);
        if (required > FileLength)
        {
            throw new InvalidDataException(
                $"NX header claims {count} offset-table entries at 0x{tableOffset:X}, past the end of a {FileLength}-byte file.");
        }

        var table = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = ReadUInt64((long)tableOffset + (long)i * sizeof(ulong));
        }

        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadUInt32(long offset) => Unsafe.ReadUnaligned<uint>(_base + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ReadUInt64(long offset) => Unsafe.ReadUnaligned<ulong>(_base + offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadUInt16(long offset) => Unsafe.ReadUnaligned<ushort>(_base + offset);

    /// <summary>
    /// Reads entry <paramref name="index"/> of the string pool.
    /// </summary>
    /// <remarks>
    /// A pool entry is a little-endian <c>uint16</c> byte length followed by that many UTF-8
    /// bytes with no terminator. Length is in bytes, not characters, so multi-byte strings are
    /// not truncated.
    /// </remarks>
    public string GetString(int index)
    {
        if ((uint)index >= (uint)_stringOffsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "String index is outside this archive's pool.");
        }

        long offset = (long)_stringOffsets[index];

        // Same reason as GetBitmapRange below, and the same blind spot in Validate: it checks
        // where each TABLE ends, never what the entries inside it say, so this is the first
        // place the offset is dereferenced. The check above only proves the entry is in range
        // as an INDEX, which says nothing about where inside the file it points.
        //
        // Measured on a 211-byte NX whose second string entry held 0x7473655400080000 — the
        // bytes of the string data read back as an offset — where the archive opened, its
        // node block listed, and reading node 1's name killed the process with 0xC0000005.
        if (offset < 0 || (ulong)offset + sizeof(ushort) > (ulong)FileLength)
        {
            throw new InvalidDataException(
                $"A string offset-table entry in a {FileLength}-byte archive points to 0x{offset:X}, " +
                "which is outside it. The file is truncated or corrupt.");
        }

        ushort byteLength = ReadUInt16(offset);

        // A second boundary, and it is a real one: the entry above can point at the last two
        // bytes of the file and still pass, leaving the string body itself running off the
        // end of the mapping. ReadUInt16 would survive that and the span below would not.
        if ((ulong)offset + sizeof(ushort) + byteLength > (ulong)FileLength)
        {
            throw new InvalidDataException(
                $"A string in a {FileLength}-byte archive starts at 0x{offset:X} and claims {byteLength} " +
                "bytes, which runs past the end of the file. The file is truncated or corrupt.");
        }

        return Utf8.GetString(new ReadOnlySpan<byte>(_base + offset + 2, byteLength));
    }

    /// <summary>
    /// Returns the byte range of a canvas payload: its stored length and where it starts.
    /// </summary>
    /// <remarks>
    /// The length is the <em>stored</em> size, which is compressed. Decoded size is always
    /// <c>width * height * 4</c> bytes of BGRA — see <see cref="NxCanvas"/>.
    /// </remarks>
    public (long Offset, uint Length) GetBitmapRange(int index)
    {
        if ((uint)index >= (uint)_bitmapOffsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Bitmap index is outside this archive's table.");
        }

        long offset = (long)_bitmapOffsets[index];

        // The four-byte length sits AT the table entry's offset, so the entry has to be
        // in range before that read -- checking afterwards is checking after the fault.
        // Nothing else guards this: Validate checks where each TABLE ends, never what the
        // entries inside it say, and this is the first place an entry is dereferenced.
        // Measured on a 159-byte NX whose single bitmap entry pointed eight gigabytes past
        // its own end: the archive opened, its tree listed, and reading this entry killed
        // the process with 0xC0000005.
        if (offset < 0 || (ulong)offset + sizeof(uint) > (ulong)FileLength)
        {
            throw new InvalidDataException(
                $"A bitmap offset-table entry in a {FileLength}-byte archive points to 0x{offset:X}, " +
                $"which is outside it. The file is truncated or corrupt.");
        }
        return (offset + 4, ReadUInt32(offset));
    }

    /// <summary>Returns the file offset an audio payload starts at.</summary>
    /// <remarks>
    /// There is deliberately no length here. A bitmap payload is length-prefixed — a
    /// <c>uint32</c> sits at its table offset — but audio is not: the offset table points
    /// straight at the bytes and the length comes from the node record that referenced them.
    /// Reading a length that is not there does not fail loudly; it returns the first four bytes
    /// of an MP3 frame as if they were a size, and the result is a wild offset rather than an
    /// error.
    /// </remarks>
    public long GetAudioOffset(int index)
    {
        if ((uint)index >= (uint)_audioOffsets.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Audio index is outside this archive's table.");
        }

        return (long)_audioOffsets[index];
    }

    /// <summary>
    /// A raw pointer into the mapped file.
    /// </summary>
    /// <remarks>
    /// For payload codecs that want to decode in place rather than through a copy. The caller
    /// owns no lifetime here: the pointer is only valid while the archive is open and not
    /// disposed.
    /// </remarks>
    internal byte* PointerAt(long offset) => _base + offset;

    /// <summary>
    /// Returns a pointer to a node record's 8-byte payload.
    /// </summary>
    /// <remarks>
    /// The payload is reinterpreted per <see cref="NxNodeType"/>; it is always eight bytes
    /// wide regardless of which of the seven shapes it is being read as.
    /// </remarks>
    internal byte* PayloadOf(int nodeIndex)
    {
        long record = (long)NodeOffset + (long)nodeIndex * NodeRecordSize;
        return _base + record + 12;
    }

    internal uint NodeNameId(int nodeIndex) => ReadUInt32((long)NodeOffset + (long)nodeIndex * NodeRecordSize);

    internal uint NodeFirstChild(int nodeIndex) => ReadUInt32((long)NodeOffset + (long)nodeIndex * NodeRecordSize + 4);

    internal ushort NodeChildCount(int nodeIndex) => ReadUInt16((long)NodeOffset + (long)nodeIndex * NodeRecordSize + 8);

    internal NxNodeType NodeType(int nodeIndex) => (NxNodeType)ReadUInt16((long)NodeOffset + (long)nodeIndex * NodeRecordSize + 10);

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The pointer has to be released before the view goes away; the reverse order
        // would leave a live Acquire with nothing to release against.
        ReleaseMapping();
    }
}