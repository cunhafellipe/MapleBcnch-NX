using System.Runtime.CompilerServices;

namespace MapleBench.Services.Nx;

/// <summary>
/// Where a canvas node's pixels live, and what shape they arrive in.
/// </summary>
/// <remarks>
/// <para>
/// The node record itself carries only an index and the dimensions. The pixels are in the
/// archive's bitmap table, deduplicated — every node pointing at the same index shares one
/// payload, which is why <c>UI.nx</c> can afford 81,874 payloads across 545 MB with only a
/// fraction of that being distinct art.
/// </para>
/// <para><b>Two encodings exist and the reader has to accept both.</b> Older archives store
/// LZ4-compressed BGRA; newer ones store a WebP stream. Every canvas sampled from a current
/// corpus was WebP, but LZ4 remains what the published format specification documents and
/// what the available writer libraries emit, so a reader that only handles one of them will
/// fail on real archives somewhere. <see cref="Encoding"/> sniffs the payload rather than
/// trusting the archive.</para>
/// </remarks>
public readonly unsafe struct NxCanvas
{
    internal NxCanvas(NxArchive archive, uint index, ushort width, ushort height)
    {
        Archive = archive;
        Index = index;
        Width = width;
        Height = height;
    }

    /// <summary>The archive this canvas belongs to.</summary>
    public NxArchive Archive { get; }

    /// <summary>Index into the archive's bitmap table.</summary>
    public uint Index { get; }

    /// <summary>Width in pixels.</summary>
    public ushort Width { get; }

    /// <summary>Height in pixels.</summary>
    public ushort Height { get; }

    /// <summary>
    /// Size of the decoded image: four bytes per pixel, BGRA order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a property of the dimensions, not of the file. A compressed payload of any
    /// length always decodes to exactly this many bytes, which is what makes it safe to
    /// allocate the destination buffer from the node record without trusting the stored length.
    /// </para>
    /// <para>
    /// <b>Why this throws rather than returning a number.</b> The dimensions are a pair of
    /// <c>ushort</c> read straight out of the node record, and nothing in the format bounds
    /// them, so <c>width * height * 4</c> used to be evaluated in <see cref="int"/>
    /// arithmetic that wrapped silently instead of failing. 32768x32768 is exactly 2^32 and
    /// came back as <b>0</b>, which the decoder read as "this canvas is empty" and answered
    /// with a blank 1x1 picture; 23171x23171 overflowed the other way into a <b>negative</b>
    /// length that reached <c>new byte[expected]</c> as an OverflowException with no mention
    /// of the canvas that caused it.
    /// </para>
    /// <para>
    /// This is the one place where untrusted dimensions turn into an allocation size, and
    /// every path into pixels goes through it — the WebP branch, the LZ4 branch, the BGRA
    /// copy and <c>DecodeToBitmap</c>. The check therefore lives here and not in each caller,
    /// so a second decoder cannot arrive without it.
    /// </para>
    /// </remarks>
    public int DecodedLength
    {
        get
        {
            // Widened to long before the multiply: the widest pair the node record can even
            // express is 65535x65535x4, which is 17 GB and overflows int well before anything
            // else gives up.
            long bytes = (long)Width * Height * 4;
            if (bytes > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"A canvas declares {Width}x{Height}, which is {bytes:N0} bytes of BGRA — more than " +
                    "a single 2 GB buffer can hold. The node record is corrupt or hostile.");
            }

            return (int)bytes;
        }
    }

    /// <summary>Byte range of the stored payload, inside the mapped file.</summary>
    public (long Offset, uint Length) EncodedRange
    {
        get
        {
            (long offset, uint length) = Archive.GetBitmapRange((int)Index);

            // NxAudio.EncodedRange below does exactly this and has always done it; the bitmap
            // side did not, and the bitmap table is the one NxArchive.Validate does not
            // inspect -- it checks where each TABLE ends, never what the entries inside it
            // say. An offset past the end of the mapping therefore reached
            // NxCanvasEncodingProbe.Detect, which reads twelve bytes at PointerAt(offset)
            // before any check exists, and the process died with 0xC0000005.
            //
            // Reproduced on a 159-byte hand-built NX whose single bitmap entry pointed eight
            // gigabytes past its own end: the archive opened, its tree listed, and the
            // process was gone the moment that canvas was rendered.
            if (offset < 0 || (ulong)(offset + length) > (ulong)Archive.FileLength)
            {
                throw new InvalidDataException(
                    $"A canvas payload claims to run from {offset} for {length} bytes in a " +
                    $"{Archive.FileLength}-byte archive.");
            }

            return (offset, length);
        }
    }

    /// <summary>How the stored payload is compressed.</summary>
    public NxCanvasEncoding Encoding
    {
        get
        {
            var (offset, length) = EncodedRange;
            return NxCanvasEncodingProbe.Detect(Archive, offset, length);
        }
    }
}

/// <summary>How a canvas payload is compressed.</summary>
public enum NxCanvasEncoding
{
    /// <summary>Raw LZ4 block that decompresses to BGRA. What the format specification documents.</summary>
    Lz4,

    /// <summary>A WebP stream, which in practice has been lossless (VP8L) in every corpus sampled.</summary>
    WebP,
}

/// <summary>Where an audio node's bytes live.</summary>
/// <remarks>
/// NX stores audio as raw bytes at a table offset. Unlike a canvas there is <b>no length
/// prefix</b>: the offset points straight at the first byte and the length travels in the node
/// record that referenced it. The codec is whatever the client expects — MP3 in this corpus —
/// which is why nothing here decodes it.
/// </remarks>
public readonly struct NxAudio
{
    internal NxAudio(NxArchive archive, uint index, uint length)
    {
        Archive = archive;
        Index = index;
        Length = length;
    }

    /// <summary>The archive this audio belongs to.</summary>
    public NxArchive Archive { get; }

    /// <summary>Index into the archive's audio table.</summary>
    public uint Index { get; }

    /// <summary>Length of the payload in bytes, as recorded in the node.</summary>
    public uint Length { get; }

    /// <summary>File offset where the payload starts.</summary>
    public long EncodedOffset => Archive.GetAudioOffset((int)Index);

    /// <summary>The payload's byte range, validated against the file length.</summary>
    public (long Offset, uint Length) EncodedRange
    {
        get
        {
            long offset = EncodedOffset;
            if (offset < 0 || (ulong)(offset + Length) > (ulong)Archive.FileLength)
            {
                throw new InvalidDataException(
                    $"An audio payload claims to run from {offset} for {Length} bytes in a " +
                    $"{Archive.FileLength}-byte archive.");
            }

            return (offset, Length);
        }
    }
}

/// <summary>
/// Identifies how a canvas payload is compressed, by sniffing its first bytes.
/// </summary>
/// <remarks>
/// <para>
/// WebP's container is self-identifying: <c>"RIFF"</c> at offset 0 and <c>"WEBP"</c> at
/// offset 8, with the payload length in between. That check is unambiguous and costs twelve
/// comparisons.
/// </para>
/// <para>
/// LZ4 has no such marker and is not magic-detectable in the general case, so it is the
/// fallback: anything that is not a WebP container is treated as LZ4. That is the same
/// decision the existing readers make, and it also means an unrecognised payload is never
/// silently mistaken for an image.
/// </para>
/// </remarks>
public static unsafe class NxCanvasEncodingProbe
{
    /// <summary>Classifies the payload at <paramref name="offset"/>.</summary>
    public static NxCanvasEncoding Detect(NxArchive archive, long offset, uint length)
    {
        if (length < 12)
        {
            return NxCanvasEncoding.Lz4;
        }

        byte* p = archive.PointerAt(offset);
        bool riff = p[0] == (byte)'R' && p[1] == (byte)'I' && p[2] == (byte)'F' && p[3] == (byte)'F';
        bool webp = p[8] == (byte)'W' && p[9] == (byte)'E' && p[10] == (byte)'B' && p[11] == (byte)'P';
        return riff && webp ? NxCanvasEncoding.WebP : NxCanvasEncoding.Lz4;
    }

    /// <summary>
    /// The WebP payload's chunk type, e.g. <c>VP8L</c> for lossless or <c>VP8 </c> for lossy.
    /// </summary>
    /// <remarks>
    /// Recorded because the distinction decides which decoders are even usable. Every canvas
    /// sampled from a current corpus was <c>VP8L</c> (lossless), which rules out a decoder
    /// that only handles lossy WebP.
    /// </remarks>
    public static string? WebPChunkType(NxArchive archive, long offset, uint length)
    {
        if (length < 16 || Detect(archive, offset, length) != NxCanvasEncoding.WebP)
        {
            return null;
        }

        byte* p = archive.PointerAt(offset);
        return System.Text.Encoding.ASCII.GetString(p + 12, 4);
    }
}