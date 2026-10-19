using System.Drawing;
using System.Runtime.CompilerServices;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.Nx;

/// <summary>
/// A MapleLib canvas payload that decodes from an NX archive instead of from WZ bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a subclass rather than a change at every call site.</b> MapleLib's canvas access runs
/// through <c>GetBitmap()</c>: <c>WzCanvasProperty.GetLinkedWzCanvasBitmap()</c> resolves the
/// link and then calls <c>GetLinkedWzImageProperty().GetBitmap()</c>, and
/// <c>GetBitmap()</c> is <c>virtual</c> on <c>WzImageProperty</c> and already overridden once on
/// <c>WzPngProperty</c>. So one override here reaches every caller that goes through the normal
/// accessor —the icon service, the minimap, the map editor's placement maths, the canvas link
/// repair —instead of a dozen scattered edits that would each have to be found again.
///
/// <c>GetImage(bool)</c> is the way past, and it is not virtual. The handful of call sites that
/// invoke it directly (the repair, audit and port paths) still bypass this class; the render
/// path, which is the one that decides whether a canvas is visible, is routed through
/// <see cref="NxCanvasPixels"/> explicitly.
/// </para>
///
/// <para><b>Why a fresh bitmap per call.</b> MapleLib's <c>GetImage(false)</c> hands back a new
/// bitmap each time, and callers rely on that —several of them <c>using</c> the result. Caching
/// a <see cref="Bitmap"/> here would hand the same object to two callers and turn the first
/// one's <c>Dispose</c> into a use-after-free in the second. The <em>pixels</em> are cached
/// instead, so a repeat call skips the WebP decode —which is the expensive part —and still
/// gets its own bitmap.
/// </para>
///
/// <para><b>Thread safety.</b> The cache is a plain field with no lock. Two threads racing to
/// decode the same canvas both produce the same bytes and one assignment wins; neither can
/// observe a half-written array, because the array is fully built before it is published. That
/// is a benign race, and it costs far less than a lock on a path the UI hits on every preview.
/// </para>
/// </remarks>
public sealed class NxPngProperty : WzPngProperty
{
    /// <summary>
    /// Every NX canvas that has decoded pixels, weakly.
    ///
    /// The sweep needs to find these without walking the tree: a full NX archive
    /// is millions of properties and a sweep that visited them all would cost more
    /// than the memory it reclaimed. Weak references, so a canvas that has been
    /// closed or collected simply drops out and the list is pruned as it is walked.
    ///
    /// Only canvases somebody actually looked at are here, which is the set worth
    /// reclaiming -- an NX archive's cost is the tree, not the pixels.
    /// </summary>
    private static readonly List<WeakReference<NxPngProperty>> Cached = [];

    private readonly NxCanvas _canvas;
    private byte[]? _pixels;

    internal NxPngProperty(NxCanvas canvas)
    {
        _canvas = canvas;

        // Format2 is BGRA8888, which is what NX canvas payloads decode to. Setting it here
        // rather than at the call site keeps the tag correct wherever this object is built,
        // and the canvas format repair service keys off it.
        Format = WzPngFormat.Format2;
        Width = canvas.Width;
        Height = canvas.Height;
    }

    /// <summary>The NX canvas this payload stands for.</summary>
    public NxCanvas Canvas => _canvas;

    /// <summary>Decoded pixels currently held, or 0 when none are.</summary>
    public int CachedPixelBytes => _pixels?.Length ?? 0;

    /// <summary>
    /// Drops the decoded pixels, keeping the payload. Returns how many bytes went.
    /// </summary>
    /// <remarks>
    /// Derived data, not the user's work. The bytes came out of the mapped archive
    /// and go straight back into it; a canvas nobody has edited is identical before
    /// and after, which is what makes this safe where unparsing a .wz image is not.
    /// </remarks>
    internal int ReleaseCachedPixels()
    {
        byte[]? pixels = _pixels;
        if (pixels is null) return 0;
        _pixels = null;
        return pixels.Length;
    }

    /// <inheritdoc/>
    public override Bitmap GetBitmap()
    {
        // Read the field ONCE and keep the array in a local for the rest of this
        // call. It used to assign _pixels and then read it back, and
        // ReleaseAllCachedPixels runs outside the session gate (ImageMemoryService
        // :351 -- the gate covers 191-206 and 210-248 only) while a render holds it,
        // so a sweep landing between the assign and the re-read made `pixels` null
        // and pixels.Length threw. A local removes the window without putting a lock
        // on the path every preview goes through, and the decode-vs-decode race the
        // class notes already call benign is unchanged: both sides decode the same
        // immutable payload out of the mapping and produce identical bytes.
        byte[]? pixels = _pixels;
        if (pixels is null)
        {
            pixels = NxCanvasDecoder.Decode(_canvas);
            _pixels = pixels;
            lock (Cached) Cached.Add(new WeakReference<NxPngProperty>(this));
        }

        if (pixels.Length == 0)
        {
            return new Bitmap(1, 1);
        }

        return MakeBitmap(pixels, _canvas.Width, _canvas.Height);
    }

    /// <summary>
    /// Copies BGRA bytes into a new 32bpp ARGB bitmap.
    /// </summary>
    /// <remarks>
    /// The pixel format is pinned to <c>Format32bppArgb</c> so the stride is exactly
    /// <c>width * 4</c>, which is the layout the NX payload is in and what callers exporting
    /// for download rely on.
    /// </remarks>
    internal static Bitmap MakeBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormatMode);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            PixelFormatMode);
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

    /// <inheritdoc/>
    public override WzImageProperty DeepClone()
    {
        // A clone has to keep the link, or a copied canvas would lose its pixels and render as
        // an empty frame. Sharing the decoded buffer is safe precisely because it is immutable
        // after the first decode and every caller gets its own bitmap out of it.
        return new NxPngProperty(_canvas)
        {
            Name = Name,
        };
    }

    /// <summary>Drops the cached pixels. Called when the archive is released.</summary>
    internal void ReleaseCache() => _pixels = null;

    /// <summary>
    /// True once this payload has a WZ-compressed representation, which is what a WZ writer
    /// needs and an NX archive does not have.
    /// </summary>
    internal bool HasWzPayload { get; private set; }

    /// <summary>
    /// Compresses <paramref name="pixels"/> into the WZ representation this payload is missing.
    /// </summary>
    /// <remarks>
    /// An NX canvas arrives with a WebP payload in a mapped file and no <c>WzBinaryReader</c>.
    /// MapleLib's writers ask for the compressed bytes, and with no reader and no cache they
    /// dereference null —from inside a lock statement, which is why the export endpoint
    /// answered "Something went wrong" with nothing useful in it. Assigning
    /// <see cref="WzPngProperty.PNG"/> is what populates the cache: it compresses the bitmap and
    /// keeps the result, so every later read is satisfied without the reader. Nothing else in
    /// MapleLib offers that.
    /// </remarks>
    internal void PrepareForWzWrite()
    {
        // Does NOT go through MapleLib's PNG setter, and that is the whole point.
        //
        // Assigning the bitmap makes MapleLib run its own format detection and then compress
        // in whatever it chose. Measured on real archives, that choice is BGRA4444: the payload
        // inflates to exactly half the expected byte count, every time. That is not a mistake
        // inside MapleLib —it deliberately picks the narrowest format that fits, and for
        // MapleStory's own sprites, which are authored 16-bit, that is the right call.
        //
        // It is the wrong call here. NX canvases are 32-bit BGRA8888 with arbitrary 8-bit
        // channel values, and the detector's test is a count of unique colours, not a check
        // that every channel value is 4-bit aligned. An image can have 400 unique colours and
        // still carry channel values no 4-bit format can hold exactly, so the stored picture is
        // quantised on the way out. Declaring Format2 afterwards changes nothing: the bytes
        // have already been produced.
        //
        // So the pixels are compressed here instead, as plain zlib over the raw BGRA, and
        // handed over with SetCompressedBytes —which assigns the compressed bytes verbatim
        // and never consults the detector. The result is byte-for-byte what NX held.
        byte[] raw = NxCanvasDecoder.Decode(Canvas);

        byte[] compressed;
        using (var buffer = new MemoryStream(raw.Length / 4))
        {
            using (var zlib = new System.IO.Compression.ZLibStream(
                buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = buffer.ToArray();
        }

        SetCompressedBytes(compressed, Canvas.Width, Canvas.Height, WzPngFormat.Format2);
        HasWzPayload = true;
    }

    /// <summary>
    /// The pixel format every bitmap this class produces uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Known limitation, measured rather than assumed.</b> WZ stores canvas pixels as
    /// straight BGRA8888 and NX stores them as straight BGRA8888. The only 32-bit alpha
    /// bitmap System.Drawing offers is <c>Format32bppArgb</c>, which GDI+ <em>defines</em> as
    /// premultiplied. Feeding it straight BGRA therefore declares values to be premultiplied
    /// that are not.
    /// </para>
    /// <para>
    /// Round-tripping an NX canvas through WZ and back shows the consequence precisely: the
    /// alpha channel comes back byte-identical while individual RGB bytes drift by a few units
    /// (measured: at most 15, typically 7) on roughly 85% of canvases, the remainder exact.
    /// Size, structure, names and every value node survive untouched.
    /// </para>
    /// <para>
    /// This is MapleLib's behaviour on its own path, not something introduced here —it
    /// renders a WZ canvas through the same bitmap type. <c>Format32bppNonArgb</c> would be
    /// the correct format and does not exist in .NET; it is a Win32 GDI concept. Closing the
    /// remaining gap means carrying pixels as a byte array and bypassing GDI+, which is a
    /// change to MapleLib rather than to this layer. Until then, an export is faithful to what
    /// MapleLib itself would produce, and that is the honest ceiling.
    /// </para>
    /// </remarks>
    internal const System.Drawing.Imaging.PixelFormat PixelFormatMode =
        System.Drawing.Imaging.PixelFormat.Format32bppArgb;

    /// <summary>
    /// Drops every cached NX canvas's pixels. Returns the bytes reclaimed.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="ImageMemoryService"/> releasing .wz images,
    /// which it cannot do for NX at all: an NX image has no block on disk to be
    /// re-read from, so the sweep's whole-image release is refused by design and
    /// an NX archive reported "swept 0" forever. What it can release is the decoded
    /// pixels, and on a canvas-heavy archive that is the part a user notices.
    /// </remarks>
    public static long ReleaseAllCachedPixels()
    {
        long freed = 0;
        lock (Cached)
        {
            for (int i = Cached.Count - 1; i >= 0; i--)
            {
                if (Cached[i].TryGetTarget(out NxPngProperty? canvas))
                {
                    freed += canvas.ReleaseCachedPixels();
                }
                else
                {
                    // Collected: the archive it belonged to is gone.
                    Cached.RemoveAt(i);
                }
            }
        }
        return freed;
    }

    /// <summary>Bytes currently held by every cached NX canvas.</summary>
    public static long TotalCachedPixelBytes
    {
        get
        {
            long total = 0;
            lock (Cached)
            {
                foreach (WeakReference<NxPngProperty> entry in Cached)
                {
                    if (entry.TryGetTarget(out NxPngProperty? canvas))
                        total += canvas.CachedPixelBytes;
                }
            }
            return total;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryResolve(object? payload, out Bitmap bitmap)
    {
        if (payload is NxPngProperty nx)
        {
            bitmap = nx.GetBitmap();
            return true;
        }

        bitmap = null!;
        return false;
    }
}
