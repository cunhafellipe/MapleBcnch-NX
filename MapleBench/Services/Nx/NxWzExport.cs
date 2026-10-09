using MapleLib.WzLib;
using MapleLib.WzLib.Serializer;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.Nx;

/// <summary>
/// Lets an NX-backed tree be written out in WZ's own formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is needed at all.</b> An NX canvas holds a WebP (or LZ4) payload in a mapped
/// file; a WZ canvas holds zlib-deflated bytes reachable through a <c>WzBinaryReader</c>. Those
/// are different storage, and the writers assume the second one: serialising a canvas calls
/// <c>WzPngProperty.GetCompressedBytes</c>, which on a cache miss does
/// <c>lock (wzReader)</c> and reads the bytes out of the archive ??and <c>wzReader</c> is null
/// for anything that did not come from a parsed .wz. It does not throw a message about the
/// format; it throws a <see cref="NullReferenceException"/> from inside a lock statement, which
/// is why the export endpoint answered "Something went wrong" with no clue.
/// </para>
///
/// <para><b>The fix is to give the payload a WZ representation before anything reads it.</b>
/// Assigning <see cref="WzPngProperty.PNG"/> makes MapleLib compress the bitmap and cache the
/// result, after which every reader takes the cached path and never touches the null reader.
/// Doing that for the whole subtree up front is also the only honest way to do it: the cost is
/// a decode and a deflate per canvas, which is exactly the cost a write was always going to
/// pay, and paying it eagerly means a failure names the image rather than arriving halfway
/// through a file that is already partly on disk.
/// </para>
///
/// <para><b>What this costs.</b> It is not free and it is not subtle: an image with 3,000
/// canvases decodes and deflates all 3,000. That is why this is a preparation step for an
/// export rather than something done at open time, and why <see cref="NxPngProperty"/> stays
/// lazy everywhere else.
/// </para>
/// </remarks>
public static class NxWzExport
{
    /// <summary>
    /// Gives every NX canvas below <paramref name="node"/> its WZ-compressed representation.
    /// </summary>
    /// <returns>How many canvases were prepared, so a caller can say so in the UI.</returns>
    /// <remarks>
    /// Idempotent. A canvas already prepared is skipped, so calling this twice ??once on a
    /// subtree and again on the whole image, say ??costs nothing the second time.
    /// </remarks>
    public static int MaterializeCanvasPayloads(WzObject? node)
    {
        switch (node)
        {
            case WzCanvasProperty canvas:
                return Prepare(canvas) + MaterializeProperties(canvas.WzProperties);

            case WzImage image:
                return MaterializeProperties(image.WzProperties);

            case WzImageProperty property:
                return MaterializeProperties(property.WzProperties);

            case WzDirectory directory:
            {
                int total = 0;
                foreach (WzImage image in directory.WzImages)
                {
                    total += MaterializeCanvasPayloads(image);
                }

                foreach (WzDirectory child in directory.WzDirectories)
                {
                    total += MaterializeCanvasPayloads(child);
                }

                return total;
            }

            default:
                return 0;
        }
    }

    /// <summary>
    /// Walks a child collection, which MapleLib returns as null on a leaf ??only
    /// <c>WzImage</c>, <c>WzSubProperty</c>, <c>WzConvexProperty</c> and
    /// <c>WzCanvasProperty</c> actually hold children.
    /// </summary>
    private static int MaterializeProperties(WzPropertyCollection? properties)
    {
        if (properties is null)
        {
            return 0;
        }

        int total = 0;
        foreach (WzImageProperty child in properties)
        {
            total += MaterializeCanvasPayloads(child);
        }

        return total;
    }

    private static int Prepare(WzCanvasProperty canvas)
    {
        if (canvas.PngProperty is not NxPngProperty nx)
        {
            return 0;
        }

        if (nx.HasWzPayload)
        {
            return 0;
        }

        // Assigning PNG makes MapleLib compress and cache. The bitmap handed over is ours to
        // dispose: the setter compresses from it and does not keep a reference.
        nx.PrepareForWzWrite();
        return 1;
    }

    /// <summary>
    /// Serialises an NX-backed image to WZ <c>.img</c> bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things have to be arranged that MapleLib will not arrange for itself.
    /// </para>
    /// <para>
    /// <b>The canvases need WZ payloads</b> ??see <see cref="MaterializeCanvasPayloads"/>,
    /// called here so no caller can forget.
    /// </para>
    /// <para>
    /// <b>The image's changed flag has to be set, and then put back.</b>
    /// <c>WzImage.SaveImage</c> has two branches: when the image is marked changed it
    /// re-serialises the properties it holds, and when it is not it copies the image's bytes
    /// straight out of its <c>WzBinaryReader</c>. An NX image holds no reader and its WZ bytes
    /// do not exist, so the second branch is not a slow path here ??it is a null dereference.
    /// The flag is the only switch that selects the right branch.
    /// </para>
    /// <para>
    /// It is restored in a finally rather than left set, because the flag is also what the
    /// files panel reads: an image left marked dirty would claim unsaved work nobody did.
    /// Setting it for the duration of the write is also the honest reading ??the WZ bytes being
    /// produced do not exist anywhere on disk yet.
    /// </para>
    /// </remarks>
    public static byte[] SerializeImageAsWz(WzImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        MaterializeCanvasPayloads(image);

        bool wasChanged = image.Changed;
        try
        {
            image.Changed = true;
            return new WzImgSerializer().SerializeImage(image);
        }
        finally
        {
            image.Changed = wasChanged;
        }
    }
}
