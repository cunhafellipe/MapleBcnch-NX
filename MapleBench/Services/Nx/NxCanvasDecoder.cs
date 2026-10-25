using System.Runtime.InteropServices;
using LZ4;
using SkiaSharp;

namespace MapleBench.Services.Nx;

/// <summary>
/// Turns an NX canvas payload into pixels.
/// </summary>
/// <remarks>
/// <para>
/// Two encodings exist and both are accepted: a WebP stream, which is what every canvas in a
/// current corpus carries, and an LZ4 block, which is what the published format specification
/// documents and what the available writer libraries emit. Neither is detectable from the
/// archive, so the payload decides — see <see cref="NxCanvasEncodingProbe"/>.
/// </para>
/// <para>
/// <b>Why SkiaSharp.</b> Every WebP payload sampled was VP8L — lossless — so a decoder that
/// only handles lossy WebP is useless here. SkiaSharp wraps libwebp, handles VP8L, is MIT
/// licensed, and already produces BGRA8888, which is exactly the channel order NX stores.
/// Writing a VP8L decoder by hand would be a couple of thousand lines of Huffman and LZ77 for
/// no benefit; the alternative pure-managed option, ImageSharp, is under a split licence that
/// a GPL toolchain should not take on.
/// </para>
/// <para>
/// <b>Why the destination buffer is sized from the node record.</b> A canvas always decodes to
/// <c>width * height * 4</c> bytes. Taking that from the node's own dimensions rather than from
/// the compressed payload means a corrupt or hostile length field cannot talk the decoder into
/// allocating an arbitrary amount of memory, and it means a decoded size that disagrees with
/// the declared size is a detectable error rather than a silent truncation.
/// </para>
/// </remarks>
public static class NxCanvasDecoder
{
    /// <summary>
    /// Decodes a canvas to BGRA8888 pixels, four bytes per pixel, row-major, no padding.
    /// </summary>
    /// <param name="canvas">The canvas node's payload location.</param>
    /// <param name="encoded">
    /// The stored bytes. <see langword="null"/> asks for the raw compressed payload to be
    /// skipped and only the size validated.
    /// </param>
    public static byte[] Decode(NxCanvas canvas, bool validateSizeOnly = false)
    {
        int expected = canvas.DecodedLength;
        if (expected == 0)
        {
            return [];
        }

        var (offset, length) = canvas.EncodedRange;
        NxCanvasEncoding encoding = NxCanvasEncodingProbe.Detect(canvas.Archive, offset, length);

        if (encoding == NxCanvasEncoding.WebP)
        {
            return DecodeWebP(canvas, offset, length, expected, validateSizeOnly);
        }

        return DecodeLz4(canvas, offset, length, expected, validateSizeOnly);
    }

    private static byte[] DecodeWebP(NxCanvas canvas, long offset, uint length, int expected, bool validateSizeOnly)
    {
        if (validateSizeOnly)
        {
            return [];
        }

        // The payload is copied out of the mapping before being handed to a native decoder.
        // SkiaSharp takes a byte[] and does not promise to stay inside it, and pointing it at
        // mapped memory would let a native library read past the end of the file.
        byte[] encoded = CopyFromMapping(canvas.Archive, offset, length);

        using SKBitmap? bitmap = SKBitmap.Decode(encoded);
        if (bitmap is null)
        {
            throw new InvalidDataException(
                $"A {canvas.Width}x{canvas.Height} canvas in '{Describe(canvas)}' is WebP but would not decode.");
        }

        if (bitmap.Width != canvas.Width || bitmap.Height != canvas.Height)
        {
            throw new InvalidDataException(
                $"A canvas in '{Describe(canvas)}' declares {canvas.Width}x{canvas.Height} but decoded " +
                $"as {bitmap.Width}x{bitmap.Height}. The node record and the payload disagree.");
        }

        return ToBgra(bitmap, expected);
    }

    private static byte[] DecodeLz4(NxCanvas canvas, long offset, uint length, int expected, bool validateSizeOnly)
    {
        if (validateSizeOnly)
        {
            return [];
        }

        byte[] encoded = CopyFromMapping(canvas.Archive, offset, length);
        byte[] pixels = new byte[expected];

        int written = LZ4Codec.Decode(encoded, 0, encoded.Length, pixels, 0, expected);
        if (written != expected)
        {
            throw new InvalidDataException(
                written < 0
                    ? $"An LZ4 canvas in '{Describe(canvas)}' is not a valid LZ4 block."
                    : $"An LZ4 canvas in '{Describe(canvas)}' decoded to {written} bytes but its node record " +
                      $"declares {expected} ({canvas.Width}x{canvas.Height} BGRA). The payload is truncated or corrupt.");
        }

        return pixels;
    }

    /// <summary>
    /// Copies BGRA8888 out of an Skia bitmap.
    /// </summary>
    /// <remarks>
    /// A straight <c>GetPixels</c> + <c>Buffer.MemoryCopy</c> would be wrong whenever the
    /// bitmap's row stride differs from <c>width * 4</c>, and Skia is free to pick either
    /// depending on how the image was decoded. Row-by-row with an explicit stride is the only
    /// version of this that is correct in both cases.
    ///
    /// The copy also has to undo premultiplication. Skia's WebP decoder returns
    /// <c>SKAlphaType.Premul</c> -- verified directly, a stored pixel of straight
    /// B=76 G=129 R=218 A=113 comes back as B=34 G=57 R=97 -- while the rest of this
    /// decoder, and the format, speak straight BGRA. Nothing downstream compensates:
    /// WzPngProperty re-encodes these bytes as WZ pixels, and nlnx/bitmap.cpp hands the
    /// payload to WebPDecodeBGRAInto, which is straight too. So a premultiplied buffer
    /// copied verbatim is colour x alpha presented as colour.
    ///
    /// Every opaque canvas hides it -- alpha 255 makes the two forms identical -- which is
    /// why a 543,760-node archive read back bit-exact while a hand-fading a mob to death
    /// came back black. Verified on a 203x142 frame at alpha 113: RGB came back at 0.44 of
    /// the authored value, which is 113/255 exactly.
    /// </remarks>
    private static byte[] ToBgra(SKBitmap bitmap, int expected)
    {
        IntPtr pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            throw new InvalidDataException("A decoded canvas produced no pixel buffer.");
        }

        bool premultiplied = bitmap.AlphaType == SKAlphaType.Premul;
        var destination = new byte[expected];
        unsafe
        {
            // fixed pins the managed array so the destination can be addressed as a pointer.
            // The source is already unmanaged memory from Skia, so it needs no pinning.
            fixed (byte* dest = destination)
            {
                byte* source = (byte*)pixels;
                int rowBytes = bitmap.Width * 4;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Buffer.MemoryCopy(source + (long)y * bitmap.RowBytes, dest + (long)y * rowBytes, rowBytes, rowBytes);
                }

                if (premultiplied)
                {
                    // Un-premultiply in place: colour = premultiplied * 255 / alpha, rounded
                    // to nearest. alpha 0 has no colour to recover, so RGB is set to 0 there,
                    // which is what every other straight-alpha producer does.
                    for (int i = 0; i < destination.Length; i += 4)
                    {
                        int alpha = destination[i + 3];
                        if (alpha == 255)
                        {
                            continue;
                        }
                        if (alpha == 0)
                        {
                            destination[i] = 0;
                            destination[i + 1] = 0;
                            destination[i + 2] = 0;
                            continue;
                        }
                        // 255 * 255 + 127 is the widest this can get, so int is not at risk.
                        for (int c = 0; c < 3; c++)
                        {
                            destination[i + c] = (byte)Math.Min(
                                255, (destination[i + c] * 255 + alpha / 2) / alpha);
                        }
                    }
                }
            }
        }

        return destination;
    }

    private static unsafe byte[] CopyFromMapping(NxArchive archive, long offset, uint length)
    {
        if (length == 0)
        {
            return [];
        }

        if (offset < 0 || (ulong)(offset + length) > (ulong)archive.FileLength)
        {
            throw new InvalidDataException(
                $"A payload in '{archive.FileLength}'-byte archive claims to run from {offset} for {length} bytes.");
        }

        var buffer = new byte[length];
        Marshal.Copy(new IntPtr(archive.PointerAt(offset)), buffer, 0, (int)length);
        return buffer;
    }

    private static string Describe(NxCanvas canvas) => "this archive";

    /// <summary>
    /// Decodes a canvas straight to a <see cref="System.Drawing.Bitmap"/>.
    /// </summary>
    /// <remarks>
    /// The editor's canvas path is System.Drawing-based, so this is the shape most callers want.
    /// The <c>Format32bppArgb</c> lock is deliberate: it guarantees the stride equals
    /// <c>width * 4</c>, which is the layout NX expects and which callers decoding for export
    /// rely on.
    /// </remarks>
    public static System.Drawing.Bitmap DecodeToBitmap(NxCanvas canvas)
    {
        byte[] pixels = Decode(canvas);
        if (pixels.Length == 0)
        {
            return new System.Drawing.Bitmap(1, 1);
        }

        var bitmap = new System.Drawing.Bitmap(canvas.Width, canvas.Height, NxPngProperty.PixelFormatMode);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, canvas.Width, canvas.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            NxPngProperty.PixelFormatMode);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}