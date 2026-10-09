using System.Runtime.CompilerServices;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.Nx;

/// <summary>
/// Helpers for a MapleLib canvas payload that came out of an NX archive.
/// </summary>
/// <remarks>
/// <para>
/// There is no registry here. An earlier shape of this kept a
/// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/> mapping
/// each payload back to its canvas, because the payload was an ordinary
/// <see cref="WzPngProperty"/> and had nowhere to put the link. Once the payload became an
/// <see cref="NxPngProperty"/> that carries its own canvas, the table was solving a problem the
/// type had already solved ??and a side table is something a future edit can forget to consult.
/// </para>
/// <para>
/// What remains is the rule that a caller has to follow, so it is written down here: going
/// through MapleLib's own <c>GetImage(bool)</c> is <b>not</b> enough for NX. That method is not
/// virtual and reaches past this type. <c>GetBitmap()</c> is.
/// </para>
/// </remarks>
public static class NxCanvasPixels
{
    /// <summary>True when this canvas came out of an NX archive.</summary>
    public static bool IsFromNx(WzPngProperty? png) => png is NxPngProperty;

    /// <summary>
    /// Returns the NX canvas behind a MapleLib canvas property, or <see langword="null"/> if it
    /// came from a real .wz.
    /// </summary>
    public static NxCanvas? SourceOf(WzPngProperty? png) => (png as NxPngProperty)?.Canvas;

    /// <summary>
    /// Decodes the pixels behind a MapleLib canvas property.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> for a canvas that did not come from NX. That is not an
    /// error ??it means the caller should use MapleLib's own path, and treating "not ours" as
    /// a failure would make every caller special-case WZ anyway.
    /// </remarks>
    public static Bitmap? Resolve(WzPngProperty? png) => (png as NxPngProperty)?.GetBitmap();

    /// <summary>
    /// Decodes the pixels behind a MapleLib canvas property, or asks MapleLib for them.
    /// </summary>
    /// <remarks>
    /// The form to reach for at a call site that has to work for both formats. Going through
    /// <see cref="Resolve"/> first means an NX canvas never touches MapleLib's decoder, and a
    /// WZ canvas never touches the NX one.
    /// </remarks>
    public static Bitmap ResolveOrDecode(WzPngProperty? png)
    {
        if (png is NxPngProperty nx)
        {
            return nx.GetBitmap();
        }

        // MapleLib's own path, for a canvas that came out of a real .wz.
        return png?.GetBitmap() ?? throw new InvalidOperationException("This canvas has no decodable picture.");
    }
}

/// <summary>
/// How much of an NX archive gets materialised into MapleLib objects at open time.
/// </summary>
/// <remarks>
/// Only <see cref="MetadataOnly"/> and <see cref="DecodeCanvasPixels"/> exist today because
/// those are the two decisions the format forces. A client folder is two and a half gigabytes
/// of art; an open that decodes it is not an open.
/// </remarks>
public enum NxLoadMode
{
    /// <summary>
    /// Build the full tree, fill in each canvas's dimensions and format, and leave the pixels
    /// behind a reference for <see cref="NxCanvasPixels"/>. This is the default.
    /// </summary>
    MetadataOnly,

    /// <summary>
    /// Also decode every canvas immediately. Only useful for a small archive, or for a
    /// tool that knows it will touch every canvas anyway.
    /// </summary>
    DecodeCanvasPixels,
}

/// <summary>What an <see cref="NxToWzFile"/> conversion produced.</summary>
/// <param name="File">The synthetic MapleLib archive.</param>
/// <param name="Archive">The NX archive it was built from. Still owned by the caller.</param>
/// <param name="Images">How many <c>*.img</c> images were produced.</param>
/// <param name="Nodes">How many NX nodes became MapleLib properties.</param>
/// <param name="Canvases">How many canvases were wired for lazy decode.</param>
/// <param name="TypeCollapsed">
/// How many numeric nodes NX could not say anything more precise about.
/// </param>
public readonly record struct NxConversionResult(
    WzFile File,
    NxArchive Archive,
    int Images,
    long Nodes,
    int Canvases,
    long TypeCollapsed);

/// <summary>
/// Builds an in-memory MapleLib archive from an NX one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why synthesise MapleLib objects at all</b>, when the editor already has a neutral
/// <c>WzNode</c> layer. Because almost nothing downstream of "open a file" speaks
/// <c>WzNode</c>: the session, the save path, the port engine, the integrity audit, the donor
/// restore, the dump and export services, and a hundred and forty-odd endpoints all walk
/// <c>WzFile</c>/<c>WzImage</c>/<c>WzImageProperty</c> directly. <c>WzNode</c> is the map
/// editor's editing substrate and sits above that. Producing the MapleLib shape is therefore
/// what makes the rest of the application work on NX without knowing NX exists.
/// </para>
///
/// <para><b>The type collapse, stated rather than hidden.</b> NX has seven node types and no
/// numeric width: one 64-bit integer and one 64-bit float. WZ had Short, Int, Long, Float and
/// Double, and <c>WzNode</c> goes out of its way to preserve that distinction ??its own notes
/// record 26 shapes in a shipping client that are genuinely mixed-type. None of that survives
/// the trip through NX, and no reader can recover it. Rather than let <c>Long</c> and
/// <c>Double</c> quietly stand in for whatever was there, every conversion counts the values
/// that arrived without a declared width and reports the total, so a caller can tell the user
/// the archive cannot round-trip back to WZ at type fidelity. Widening to the 64-bit type is
/// the only choice that loses nothing on the way <i>in</i>: every WZ integer fits, and a float
/// that arrives as a double keeps every bit it had.</para>
/// </remarks>
public static class NxToWzFile
{
    /// <summary>Builds the MapleLib archive.</summary>
    public static NxConversionResult Convert(NxArchive archive, NxLoadMode mode = NxLoadMode.MetadataOnly)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var file = new WzFile(gameVersion: 0, WzMapleVersion.BMS);

        long nodes = 0;
        long collapsed = 0;
        int canvases = 0;
        int images = 0;

        foreach (NxNode imageNode in archive.Root)
        {
            // NX's root children are the *.img images themselves, exactly where WZ keeps them.
            var image = new WzImage(imageNode.Name);

            // WzPropertyCollection.Add, not WzImage.AddProperty. The latter throws on a name the
            // image already holds, and duplicate sibling names are legal in this format: the
            // corpus holds two nodes named "1029" under Quest.nx's Say.img and two named
            // "2104" under String.nx's Npc.img. WzNode.Build reaches the same conclusion for the
            // same reason.
            if (image.WzProperties is not { } properties)
            {
                throw new InvalidDataException($"'{imageNode.Name}' did not open with a property collection.");
            }

            foreach (NxNode child in imageNode)
            {
                properties.Add(BuildProperty(child, archive, mode, ref nodes, ref collapsed, ref canvases));
            }

            image.MarkWzImageAsParsed();

            // Clear the dirty flag that populating the collection just set.
            //
            // Adding a property marks its image changed ??which is exactly right when a
            // person edits an archive, and exactly wrong here, where "changed" would mean
            // the user has unsaved work. Every node's DTO reads its dirty state from this
            // one flag, so leaving it set would make a freshly opened NX file claim to hold
            // edits, fire the close prompt on exit, and put a stray "unsaved changes" mark
            // on a file nobody touched. An NX entry is read-only regardless, but the flag
            // is what the UI reads, so it has to say the truth.
            image.Changed = false;

            file.WzDirectory.AddImage(image);
            images++;
        }

        return new NxConversionResult(file, archive, images, nodes, canvases, collapsed);
    }

    /// <summary>
    /// Refuses a value node that carries children.
    /// </summary>
    /// <remarks>
    /// Only <see cref="NxNodeType.None"/> and <see cref="NxNodeType.Bitmap"/> carry children in
    /// this format. Across 9.9 million value nodes in the reference corpus ??integer, real,
    /// string, vector and audio together ??not one had a single child. A value that did would be
    /// a shape this reader does not model, and silently dropping its children would produce an
    /// archive that looks complete and is not. Saying so costs one branch on the common path.
    /// </remarks>
    private static void GuardLeaf(NxNode node)
    {
        if (node.ChildCount > 0)
        {
            throw new InvalidDataException(
                $"Node '{node.Name}' is a {node.Type} carrying {node.ChildCount} children, which no " +
                "archive in the reference corpus does. Refusing rather than dropping them silently.");
        }
    }

    private static void AddChildren(
        NxNode node,
        WzImageProperty parent,
        NxArchive archive,
        NxLoadMode mode,
        ref long nodes,
        ref long collapsed,
        ref int canvases)
    {
        // Duplicate sibling names are legal in NX and occur in the corpus ??Say.img holds two
        // "1029", Npc.img two "2104" ??and WZImage.AddProperty throws on a name it already
        // holds. WzPropertyCollection's own Add is the one that permits them, which is why
        // WzNode.Build() goes through it rather than through AddProperty.
        var collection = parent.WzProperties;
        if (collection is null)
        {
            throw new InvalidDataException(
                $"'{node.Name}' carries {node.ChildCount} children but was built as a type that holds none. " +
                "This archive has a shape this reader does not model.");
        }

        foreach (NxNode child in node)
        {
            collection.Add(BuildProperty(child, archive, mode, ref nodes, ref collapsed, ref canvases));
        }
    }

    private static WzImageProperty BuildProperty(
        NxNode node,
        NxArchive archive,
        NxLoadMode mode,
        ref long nodes,
        ref long collapsed,
        ref int canvases)
    {
        nodes++;
        string name = node.Name;

        switch (node.Type)
        {
            case NxNodeType.None:
            {
                var sub = new WzSubProperty(name);
                AddChildren(node, sub, archive, mode, ref nodes, ref collapsed, ref canvases);
                return sub;
            }

            case NxNodeType.Integer:
                GuardLeaf(node);
                collapsed++;
                return new WzLongProperty(name, node.AsInteger);

            case NxNodeType.Real:
                GuardLeaf(node);
                collapsed++;
                return new WzDoubleProperty(name, node.AsReal);

            case NxNodeType.String:
                GuardLeaf(node);
                return new WzStringProperty(name, node.AsString);

            case NxNodeType.Vector:
            {
                GuardLeaf(node);
                (int x, int y) = node.AsVector;
                return new WzVectorProperty(name, x, y);
            }

            case NxNodeType.Bitmap:
            {
                // A bitmap node is NOT a leaf. Across the whole reference corpus, 1,296,108 of
                // 1,298,868 canvas nodes carry children ??origin, delay, _inlink, _outlink and
                // whatever else the art was authored with ??and they are the reason a canvas
                // draws where it draws. Treating the type as a leaf silently threw all of them
                // away: on Morph.nx that was 8,977 properties, a quarter of the file.
                //
                // WzCanvasProperty is exactly the right home for them. That is how WZ stores
                // them too, so everything downstream ??the map editor's placement maths, the
                // origin-aware renderer, the canvas link repair ??reads them unchanged.
                NxCanvas canvas = node.AsCanvas!.Value;
                var property = new WzCanvasProperty(name)
                {
                    PngProperty = BuildPng(canvas, mode),
                };

                AddChildren(node, property, archive, mode, ref nodes, ref collapsed, ref canvases);
                canvases++;
                return property;
            }

            case NxNodeType.Audio:
            {
                GuardLeaf(node);
                NxAudio audio = node.AsAudio!.Value;
                var (header, body) = NxAudioBytes.Split(audio);
                WzBinaryProperty result = new(name, 0, header, body);
                NxAudioBytes.Register(result, audio);
                return result;
            }

            default:
                // Unreachable for any archive NxArchive.Validate accepted, because the type
                // field is two bytes and the seven defined values are the only ones any writer
                // emits. A future PKG5 revision could add more, and refusing to silently
                // mis-type them is the whole point of naming the case.
                throw new InvalidDataException(
                    $"Node '{name}' in '{archive.FileLength}'-byte archive has unknown type {(int)node.Type}. " +
                    "This may be a newer NX revision than this reader understands.");
        }
    }

    private static WzPngProperty BuildPng(NxCanvas canvas, NxLoadMode mode)
    {
        // NxPngProperty, not a bare WzPngProperty: it is what makes the pixels reachable
        // through the ordinary GetBitmap() accessor without every call site learning about NX.
        var png = new NxPngProperty(canvas);

        if (mode == NxLoadMode.DecodeCanvasPixels)
        {
            // Touch the pixels once so a caller who asked for eager decoding pays for it here
            // rather than discovering a cold cache later.
            using Bitmap warm = png.GetBitmap();
        }

        return png;
    }
}

/// <summary>
/// Splits an NX audio payload into the WZ sound header and the MP3 that follows it.
/// </summary>
/// <remarks>
/// <para>
/// NX audio is not an opaque blob and it is not encrypted. Every payload in the corpus begins
/// with the identical 82 bytes, and those 82 bytes are the WZ sound-property header verbatim:
/// the standard sound GUID, one unknown byte, and a 30-byte <c>Mp3WaveFormat</c>. The first
/// byte after them is <c>FF F2</c> ??an MPEG frame sync ??which is the MP3 starting exactly
/// where the header ends.
/// </para>
/// <para>
/// That matters because the header carries what MapleLib needs to label the sound as MP3
/// rather than WAV. Dropping it entirely and handing MapleLib an empty header does not degrade
/// quietly: the header parse subtracts a fixed length from it and throws
/// <see cref="OverflowException"/> on the underflow.
/// </para>
///
/// <para><b>Stated limitation.</b> Only the GUID and the length byte are handed to MapleLib.
/// Its parser then expects a WAV-shaped <c>WaveFormat</c> and, when the sizes do not line up,
/// falls back to XOR-decrypting with the enclosing archive's WZ key ??a key an NX-sourced
/// property does not have, so the fallback dereferences null. Passing the whole 82 bytes
/// therefore cannot work through the public constructor, and making it work would mean patching
/// a vendored dependency that a re-clone would undo. The consequence is bounded and named:
/// <c>SoundType</c> reads as <c>Raw</c> rather than <c>MP3</c>, which still yields the right
/// <c>.mp3</c> extension, and a caller that needs the format tag should read the payload's own
/// header rather than MapleLib's opinion of it. Audio is 7,379 of 14.2 million nodes in the
/// reference corpus; this is worth naming precisely and not worth a dependency patch.
/// </para>
/// </remarks>
internal static unsafe class NxAudioBytes
{
    /// <summary>
    /// The WZ sound GUID plus the one-byte format length that opens every NX audio payload.
    /// </summary>
    private static readonly ConditionalWeakTable<WzBinaryProperty, BoxedAudio> Sources = new();

    /// <summary>Records where an audio property's bytes really live, so a writer can put them back whole.</summary>
    /// <remarks>
    /// Only the 52-byte prefix reaches the MapleLib property; an NX writer needs the full
    /// 82-byte header, and the remainder is not reconstructible from anything the property
    /// holds. Weakly keyed for the same reason as the canvas payloads — the table must not be
    /// what keeps an archive's audio alive.
    /// </remarks>
    /// <summary>Boxed so the weak table has a reference type to hold.</summary>
    private sealed class BoxedAudio(NxAudio audio) { internal NxAudio Audio { get; } = audio; }

    internal static void Register(WzBinaryProperty property, NxAudio audio) => Sources.AddOrUpdate(property, new BoxedAudio(audio));

    /// <summary>Returns the NX audio a property came from, or <see langword="null"/> if it is not ours.</summary>
    internal static NxAudio? SourceOf(WzBinaryProperty? property)
        => property is not null && Sources.TryGetValue(property, out var box) ? box.Audio : null;

    internal const int GuidPrefixLength = 52;

    /// <summary>Splits the payload into the header prefix MapleLib can accept and the MP3 body.</summary>
    internal static (byte[] Header, byte[] Body) Split(NxAudio audio)
    {
        (long offset, uint length) = audio.EncodedRange;

        if (length <= GuidPrefixLength)
        {
            // Too short to hold a header and any audio. Handing MapleLib a truncated header
            // would make its fixed-length subtraction underflow, so say what is actually wrong.
            throw new InvalidDataException(
                $"Audio entry {audio.Index} is {length} bytes, which cannot hold a " +
                $"{GuidPrefixLength}-byte header and any audio.");
        }

        byte[] all = Copy(audio);
        var header = new byte[GuidPrefixLength];
        var body = new byte[all.Length - GuidPrefixLength];
        Array.Copy(all, 0, header, 0, GuidPrefixLength);
        Array.Copy(all, GuidPrefixLength, body, 0, body.Length);
        return (header, body);
    }

    /// <summary>Copies an NX audio payload out of the mapping.</summary>
    internal static byte[] Copy(NxAudio audio)
    {
        var (offset, length) = audio.EncodedRange;
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        System.Runtime.InteropServices.Marshal.Copy(
            new IntPtr(audio.Archive.PointerAt(offset)), buffer, 0, (int)length);
        return buffer;
    }

    private static string Describe(NxAudio audio)
        => $"archive entry {audio.Index} (offset {audio.EncodedOffset}, {audio.Length} bytes)";
}

