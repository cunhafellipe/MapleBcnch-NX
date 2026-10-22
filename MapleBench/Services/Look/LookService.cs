using MapleBench.Models;
using MapleBench.Services.Look;

namespace MapleBench.Services;

/// <summary>
/// Composes a character's look and finds the archive nodes it is composed from.
/// </summary>
/// <remarks>
/// The composer (<see cref="CharLookComposer"/>) does the arithmetic; this owns
/// the two questions around it: WHICH nodes make up a look, and how a composed
/// <see cref="CharLookComposer.Look"/> becomes wire shape. Both live here so the
/// composer stays a pure translation of the C++ and nothing else.
/// </remarks>
public sealed class LookService
{
    /// <summary>
    /// The sheets a look is built from, as the reference addresses them.
    /// <c>BodyDrawInfo::init</c> reads <c>00002000.img</c> and <c>00012000.img</c>
    /// by name, out of the character archive.
    /// </summary>
    public const string BodyImage = "00002000.img";

    public const string HeadImage = "00012000.img";

    private readonly WzSessionService _session;
    private readonly CharLookComposer _composer;

    public LookService(WzSessionService session)
    {
        _session = session;
        _composer = new CharLookComposer(session);
    }

    /// <summary>Session path of the body image inside the given file id, or null.</summary>
    public string? BodyPath(string fileId)
    {
        foreach (string candidate in new[] { $"{fileId}/{BodyImage}", $"{fileId}/Character/{BodyImage}" })
            if (_session.TryResolve(candidate) is not null) return candidate;
        return null;
    }

    public string? HeadPath(string fileId)
    {
        foreach (string candidate in new[] { $"{fileId}/{HeadImage}", $"{fileId}/Character/{HeadImage}" })
            if (_session.TryResolve(candidate) is not null) return candidate;
        return null;
    }

    /// <summary>
    /// The file id whose archive carries character art, or null.
    /// </summary>
    /// <remarks>
    /// Both archive shapes are probed because NX puts the bodies at the root and
    /// WZ puts them under a <c>Character/</c> folder. A file id is <c>f1</c>,
    /// <c>f2</c>, … in the order archives were opened, so it is never written
    /// down — it is looked up per request.
    /// </remarks>
    public string? CharacterFile()
    {
        foreach (OpenFile open in _session.Files)
            if (BodyPath(open.Id) is not null) return open.Id;
        return null;
    }

    /// <summary>
        /// Every stance the open character archive carries, with its frame count.
        /// </summary>
        /// <remarks>
        /// A caller that has this never has to probe a frame to find out whether it
        /// exists. Reading it from the sheet rather than from a hardcoded list
        /// follows Body.cpp:39-47, which enumerates Stance::names and then skips
        /// whatever the sheet does not have — so the sheet, not the enum, decides.
        /// </remarks>
        public IReadOnlyDictionary<string, int> StanceFrames(string? fileId)
        {
            fileId ??= CharacterFile();
            if (fileId is null || BodyPath(fileId) is not { } body) return new Dictionary<string, int>();
            return _composer.StanceFrames(body);
        }

        /// <summary>
        /// Compose a look, or return null when the archive has no character art at
        /// all — a different statement from "it has one but no such pose".
        /// </summary>
    public LookDto? Compose(
        string? fileId, string? hairImage, string? faceImage, string stance, int frame)
    {
        Dictionary<string, string> slots = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(hairImage)) slots["Hair-1"] = hairImage;
        if (!string.IsNullOrWhiteSpace(faceImage)) slots["Face-1"] = faceImage;
        return Compose(fileId, slots, stance, frame);
    }

    /// <summary>
    /// Compose with a slot map in: slot name to image name, exactly the 19 the
    /// reference offers. Each one is resolved against its own folder, and a slot
    /// whose item is not in that folder is dropped and named.
    /// </summary>
    /// <remarks>
    /// This is the general form. The three-argument overload above is the case of
    /// it with a hair and a face and nothing else, and it still behaves exactly as
    /// it did, because it now reaches the composer by the same door.
    /// </remarks>
    public LookDto? Compose(
        string? fileId, IReadOnlyDictionary<string, string> slots, string stance, int frame)
    {
        fileId ??= CharacterFile();
        if (fileId is null) return null;

        Dictionary<string, string> equipped = new(StringComparer.Ordinal);
        List<string> unresolved = new();
        string? body = null;
        string? head = null;
        // Slots the caller explicitly emptied. A name here means "compose without it",
        // which is a different statement from a name that is simply absent.
        HashSet<string> strippedSlots = new(StringComparer.Ordinal);
        foreach ((string slot, string image) in slots)
        {
            EquipSlotDef? def = EquipSlots.Find(slot);

            // An empty image is a STRIP, not a malformed id. The picker's Strip button
            // sends one (walkview.js chooseEquip / avatar.js slotMap), and it has to
            // arrive here as a fact rather than as an absence: a slot the caller never
            // NAMED gets the default sheet below, which is right for a partial map and
            // wrong for a strip. Before this, stripping Head or Body put 00012000.img /
            // 00002000.img straight back on every compose.
            if (string.IsNullOrWhiteSpace(image))
            {
                strippedSlots.Add(def?.Name ?? slot);
                continue;
            }

            if (def is null)
            {
                unresolved.Add($"{slot}: not an equip slot");
                continue;
            }
            if (string.IsNullOrWhiteSpace(image)) continue;

            // The slot's own id rule, enforced here rather than only in the picker.
            // `SlotImages` uses it to decide what to OFFER, which is why a slot's
            // filter looked airtight from the UI -- but Compose resolved whatever
            // it was handed, so `{"Head":"00002004.img"}` (a BODY sheet) and
            // `{"Cap-1":"01302000.img"}` (a SWORD) both composed a figure instead
            // of reporting the mismatch. The picker filtering is a convenience;
            // this is the contract.
            if (!ImageIdOf(image, out int imageId) || !def.Accepts(imageId))
            {
                unresolved.Add($"{slot}: {image} is not a {slot} id");
                continue;
            }

            // Head and Body are not layers over the character -- they ARE it.
            // Everything else is composed on top of these two sheets, so choosing
            // one REPLACES the base rather than adding to `equipped`. Passing them
            // through as equipment produced a look with an ordinary body under a
            // different head sheet, which is not a thing the client can draw.
            if (def.Folder.Length == 0)
            {
                if (ResolveImage(fileId, null, image) is not { } sheet)
                {
                    unresolved.Add($"{slot}: {image} not at {fileId}/");
                    continue;
                }
                if (def.Name == "Head") head = sheet;
                else if (def.Name == "Body") body = sheet;
                continue;
            }

            if (ResolveImage(fileId, def.Folder, image) is not { } path)
            {
                unresolved.Add($"{slot}: {image} not in {def.Folder}/");
                continue;
            }
            equipped[def.Name] = path;
        }

        // A slot the caller did not name keeps the sheet the composer reads by
        // name (BodyDrawInfo::init reads 00002000.img / 00012000.img), so the
        // defaults land AFTER the loop rather than before it.
        if (!strippedSlots.Contains("Body")) body ??= BodyPath(fileId);
        if (body is null) return null;
        if (!strippedSlots.Contains("Head")) head ??= HeadPath(fileId);

        // Every frame of the pose, because the anchors are PER FRAME
                // (BodyDrawInfo.cpp:59-100 indexes them by stance AND frame). Composing
                // only the requested one and drawing it for the whole animation is a
                // character that breathes without moving its own arms.
                CharLookComposer.Look probe = _composer.ComposeEquipped(body, head!, equipped, stance, frame);
                LookDto dto = Project(fileId, probe);
                dto.Frames.Clear();
                dto.Frames.Add(Frame(probe));

                // Stance.cpp:98-135 is the reference's own list of the names a character
                // may be in; a name outside it is `Stance::Id::NONE` there and simply
                // draws nothing. Two DIFFERENT failures used to look identical here —
                // both produced an empty look — and they are reported apart from here
                // on:
                //   stanceExists=false : the archive has no such pose at all
                //                          ("sitted", "move", "ladder-rope" — not names
                //                          any sheet in Character.nx carries)
                //   frameExists=false  : a real pose, asked past its end
                //                          (stand1 has 3 frames; frame 5 is not one)
                // The 58-empty-out-of-80 measurement was these two added together.
                dto.StanceExists = probe.FrameCount > 0;
                dto.FrameExists = probe.FrameCount > 0 && frame < probe.FrameCount;

                // A frame past the end has no parts to speak of, but the FACE marker
                                // is collected from a pose-independent node, so it came back alone:
                                // one part, HTTP 200, a face where a character should be — which is
                                // what 56 of the 80 swept pairs actually measured. Clear the parts
                                // as well as the frames, and say why, instead of shipping a
                                // fragment that reads as a mostly-successful compose.
                                if (!dto.FrameExists)
                                {
                                    dto.Parts.Clear();
                                    dto.Frames.Clear();
                                    dto.Bounds = new BoxDto();
                                    dto.Hidden.Add(
                                        $"frame {frame} does not exist: {stance} carries " +
                                        $"{probe.FrameCount} frame(s) (0..{probe.FrameCount - 1})");
                                }

                                int frames = Math.Clamp(probe.FrameCount, 1, 32);
        for (int f = 0; f < frames; f++)
        {
            if (f == frame) continue;
            CharLookComposer.Look other = _composer.ComposeEquipped(body, head!, equipped, stance, f);
            if (other.Parts.Count == 0) continue;
            dto.Frames.Add(Frame(other));
        }
        dto.Frames.Sort((a, b) => a.Frame.CompareTo(b.Frame));

        dto.Equipped = equipped.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        dto.Unresolved = unresolved;
        return dto;
    }

    /// <summary>Compose and project in one step.</summary>
    public LookDto? Compose(string? hairImage, string? faceImage, string stance, int frame)
        => Compose(null, hairImage, faceImage, stance, frame);

    /// <summary>
    /// The images a slot may be given, from the archive itself.
    /// </summary>
    /// <remarks>
    /// A picker needs this and the composer does not, so it is here rather than in
    /// the composer: answering "which caps are there" must not mean composing one.
    /// </remarks>
    public IReadOnlyList<string> SlotImages(string? fileId, string slotName, int limit = 500)
    {
        EquipSlotDef? def = EquipSlots.Find(slotName);
        if (def is null) return [];
        // A slot with no folder is not a slot with nothing in it. Head and Body
        // live at the archive root (AvatarForm.cs:496-499) and are told apart by
        // their leading digits (AvatarForm.cs:559-566), so they are enumerated
        // there -- see EquipSlotDef.RootPrefix.
        bool root = def.Folder.Length == 0;
        if (root && def.RootPrefix is null) return [];

        fileId ??= CharacterFile();
        if (fileId is null) return [];

        List<string> found = new();
        foreach (string candidate in root ? RootImages(fileId) : FolderImages(fileId, def.Folder))
        {
            string name = candidate[(candidate.LastIndexOf('/') + 1)..];
            // GetChildren tags a repeated name with `#n`; an id never carries one.
            int hash = name.IndexOf('#');
            if (hash > 0) name = name[..hash];
            if (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (!int.TryParse(name, out int id) || !def.Accepts(id)) continue;
            found.Add(name);
            if (found.Count >= limit) break;
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Every image directly under the archive root, as session paths.
    ///
    /// <para>
    /// This is where the two body sheets live: <c>AvatarForm.cs:496-499</c> gives
    /// Head and Body an empty <c>CharacterDir</c>, so the reference enumerates the
    /// root's own <c>.img</c> children rather than a folder's. The equipment
    /// folders (Cap, Coat, ...) are siblings of those images, and the caller's
    /// id filter rejects them because a folder name never parses as an id.
    /// </para>
    /// </summary>
    private List<string> RootImages(string fileId)
    {
        foreach (string path in new[] { fileId, $"{fileId}/Character" })
        {
            if (_session.TryResolve(path) is null) continue;
            List<string> names = new();
            bool sawImage = false;
            foreach (NodeDto child in _session.GetChildren(path))
            {
                names.Add(child.Path);
                if (child.Path.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) sawImage = true;
            }
            // Short-circuit on IMAGES, not on "has any child". The root also holds the
            // equipment folders (Cap, Coat, ...), so an archive whose root has folders
            // but no sheets resolved on the FIRST probe and never reached
            // `{fileId}/Character`, which is where a WZ build keeps them. The folder
            // names stay in the list because the caller discards anything that does
            // not parse as an id; concatenating both probes instead would double every
            // sheet on an NX archive that carries them at both levels.
            if (sawImage) return names;
        }
        return [];
    }

    /// <summary>Every child image of a folder, as session paths. Empty when the folder has none.</summary>
    private List<string> FolderImages(string fileId, string folder)
    {
        foreach (string path in new[] { $"{fileId}/{folder}", $"{fileId}/Character/{folder}" })
        {
            // GetChildren throws on a path that does not resolve, and "this archive
            // has no Cap folder" is an ordinary answer here rather than an error.
            if (_session.TryResolve(path) is null) continue;
            List<string> names = new();
            foreach (NodeDto child in _session.GetChildren(path))
                names.Add(child.Path);
            if (names.Count > 0) return names;
        }
        return [];
    }

    /// <summary>
    /// The id inside an image reference, or false when the name is not an id.
    ///
    /// <para>
    /// Accepts both spellings the archive and the callers use: <c>01000000</c> and
    /// <c>01000000.img</c>. It must, because `SlotImages` hands out the bare id
    /// while the picker sends the name with its extension.
    /// </para>
    /// </summary>
    private static bool ImageIdOf(string image, out int id)
    {
        ReadOnlySpan<char> span = image.AsSpan();
        if (span.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) span = span[..^4];
        return int.TryParse(span, out id);
    }

    /// <summary>
    /// The session path of an image named by id, or null when the archive has no
    /// such image. A null <paramref name="folder"/> means the archive root, which
    /// is where the body sheets are (AvatarForm.cs:496-499).
    /// </summary>
    private string? ResolveImage(string fileId, string? folder, string image)
    {
        string under = folder is null
            ? $"{fileId}/{image}"
            : $"{fileId}/{folder}/{image}";
        foreach (string candidate in new[] { under, $"{under}.img", $"{under.TrimStart('0')}.img" })
            if (_session.TryResolve(candidate) is not null) return candidate;

        // The direct path covers a caller that spells the image the way the file
        // does. This covers one that does not — a bare id with no leading zeroes,
        // or a name with the extension on — by asking the folder what is in it.
        foreach (string path in folder is null ? RootImages(fileId) : FolderImages(fileId, folder))
        {
            string name = path[(path.LastIndexOf('/') + 1)..];
            int hash = name.IndexOf('#');
            if (hash > 0) name = name[..hash];
            if (name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (string.Equals(name, image, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }

    private static LookDto Project(string fileId, CharLookComposer.Look look)
    {
        LookFrameDto frame = Frame(look);
        return new LookDto
        {
            Stance = look.Stance,
            Frame = look.Frame,
            Delay = look.Delay,
            FrameCount = look.FrameCount,
            BodyAnchor = frame.BodyAnchor,
            HeadAnchor = frame.HeadAnchor,
            FaceAnchor = frame.FaceAnchor,
            HairAnchor = frame.HairAnchor,
            ArmAnchor = frame.ArmAnchor,
            HandAnchor = frame.HandAnchor,
            ShowsFace = frame.ShowsFace,
            Hidden = look.Hidden,
            Source = fileId,
            Bounds = frame.Bounds,
            Parts = frame.Parts,
            // Reported from the composer, which is where the tables live. Hard-
            // coded 0 here is what made the client show `zmap: 0` and the HUD read
            // "no Base.nx open" while the ranks were demonstrably being read.
            ZmapCount = look.ZmapCount,
            SmapCount = look.SmapCount,
            TableScan = look.TableScan,
        };
    }

    private static LookFrameDto Frame(CharLookComposer.Look look)
    {
        LookFrameDto dto = new()
        {
            Frame = look.Frame,
            Delay = look.Delay,
            BodyAnchor = Point(look.BodyAnchor),
            HeadAnchor = Point(look.HeadAnchor),
            FaceAnchor = Point(look.FaceAnchor),
            HairAnchor = Point(look.HairAnchor),
            ArmAnchor = Point(look.ArmAnchor),
            HandAnchor = Point(look.HandAnchor),
            ShowsFace = look.ShowsFace,
            Bounds = Box(look.Bounds),
        };

        foreach (CharLookComposer.LookPart part in look.Parts)
        {
            dto.Parts.Add(new LookPartDto
            {
                Path = part.Path,
                Width = part.Width,
                Height = part.Height,
                OriginX = part.OriginX,
                OriginY = part.OriginY,
                Shift = new PointDto { X = part.Shift.X, Y = part.Shift.Y },
                Screen = new PointDto { X = part.ScreenX, Y = part.ScreenY },
                Z = part.Z,
                ZName = part.ZName,
                SlotBits = part.Slots,
                Slots = part.SlotCodes,
                Priority = part.Priority,
                Owner = part.Owner,
                Part = part.Part,
            });
        }
        return dto;
    }

    private static PointDto Point(System.Drawing.Point p) => new() { X = p.X, Y = p.Y };

    private static BoxDto Box((int MinX, int MinY, int MaxX, int MaxY) b)
        => new() { MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY };
}