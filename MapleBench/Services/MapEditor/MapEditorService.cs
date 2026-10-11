using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MapleBench.Models;
using MapleBench.Services.MapModel;
using MapleBench.Services.Nx;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// The map editor's server half: open a map into the round-trip model, serve it
/// shaped for a renderer, apply small safe edits, and save it back through the
/// same model.
///
/// <para><b>Every map goes through <see cref="MapRoundTrip.LoadVerified"/> and
/// nothing else.</b> A map the model cannot write back unchanged is refused with
/// the node that differs, never opened partially — a partial open plus a save is
/// a damaged map. All 17,442 v232 images pass today, so the refusal arm is
/// expected to fire never; it exists for the first map nobody predicted.</para>
///
/// <para><b>Edits are edits to the model, not to the archive.</b> The document
/// lives here between open and save; the session's tree is untouched until
/// <see cref="Save"/> rebuilds the image from the model and writes it through
/// <see cref="WzSaveService"/>. The write is then verified against the saved and
/// reopened archive — the model's own bytes against the archive's — because a
/// counter is not a verification.</para>
///
/// <para><b>The rules this file inherits from the measurements</b> (see
/// <c>docs/map-data-model.md</c>): layers are read from the image, never 0-7
/// (<c>749080500.img</c> has an 8); values are written through the
/// type-preserving setters (26 shapes are genuinely mixed-type); names are
/// ordinal and never trimmed (<c>speedMaxOver&#160;</c> is a different key);
/// foothold prev/next asymmetry is legal on 6,744 footholds and is never
/// "repaired" — the invariant worth keeping is coincident endpoints, and only
/// when a vertex is dragged.</para>
/// </summary>
public sealed partial class MapEditorService
{
    /// <summary>Open documents are few and heavy; past this the least recently
    /// used is dropped (its unsaved edits with it, so the cap is generous).</summary>
    private const int MaxOpenDocs = 6;

    private const int MaxUndoDepth = 200;

    private readonly WzSessionService _session;
    private readonly WzSaveService _save;
    private readonly StringPoolService _strings;
    private readonly MapAssetService _assets;
    private readonly WzRenderService _render;
    private readonly ILogger<MapEditorService> _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, EditorDoc> _docs = new(StringComparer.Ordinal);

    public MapEditorService(
        WzSessionService session, WzSaveService save, StringPoolService strings,
        MapAssetService assets, WzRenderService render,
        ILogger<MapEditorService> log)
    {
        _session = session;
        _save = save;
        _strings = strings;
        _assets = assets;
        _render = render;
        _log = log;
    }

    #region Capabilities and the picker

    public MapEditCapabilitiesDto Capabilities()
    {
        MapEditCapabilitiesDto dto = new();
        lock (_session.Gate)
        {
            foreach ((OpenFile file, WzObject _, string _) in MapRoots())
            {
                if (!dto.MapArchives.Contains(file.Name))
                    dto.MapArchives.Add(file.Name);
            }
            foreach (OpenFile file in _session.SelectRoleSources("Map"))
            {
                if (file.LooseImage != null && !dto.MapArchives.Contains(file.Name))
                    dto.MapArchives.Add(file.Name);
            }
            dto.MapCount = ListMapsUnderGate(null, int.MaxValue).Total;
            dto.Available = dto.MapCount > 0;
            dto.PortalIcons = FindHelperImage() != null;
            dto.NpcSprites = FindRootImageArchives("Npc").Count > 0;
            dto.MobSprites = FindRootImageArchives("Mob").Count > 0;
        }
        // Deliberately after the gate: IsAvailable may wait for the string pool
        // build, and holding the session gate over that wait would stall every
        // other request behind a name lookup.
        dto.Names = _strings.IsAvailable;
        return dto;
    }

    /// <summary>
    /// The picker list: map id + name from String.wz. Names come from String.wz
    /// because it is the measured source — <c>info/mapName</c> exists on 20 maps
    /// and is wrong on all of them.
    /// </summary>
    public MapListDto ListMaps(string? query, int limit)
    {
        // Names come from the string pool, whose blocking build must happen
        // outside the session gate (its own documented rule). Warmed here so the
        // picker's very first listing already has names rather than ids.
        _strings.Warm();
        lock (_session.Gate)
            return ListMapsUnderGate(query, limit);
    }

    private MapListDto ListMapsUnderGate(string? query, int limit)
    {
        MapListDto dto = new();
        HashSet<int> seen = new();
        string? q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        foreach ((OpenFile file, WzObject mapRoot, string rootPath) in MapRoots())
        {
            foreach (WzObject sub in ChildrenOf(mapRoot))
            {
                // Map0..Map9; anything else at this level is not a map folder.
                if (sub.Name is not { Length: 4 } subName
                    || !subName.StartsWith("Map", StringComparison.Ordinal)
                    || subName[3] is < '0' or > '9')
                    continue;

                string subPath = WzPath.Child(rootPath, subName);
                foreach (WzObject image in ChildrenOf(sub))
                {
                    if (image.Name is not { } imageName)
                        continue;
                    string stem = imageName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                        ? imageName[..^4] : imageName;
                    if (!int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                        continue;
                    if (!seen.Add(id))
                        continue;   // mount-order winner already listed

                    string? name = _strings.GetMapName(id);
                    if (q != null
                        && !stem.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && (name == null || !name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    dto.Total++;
                    dto.Maps.Add(new MapListRowDto
                    {
                        Id = id,
                        Name = name,
                        Path = WzPath.Child(subPath, imageName),
                        Source = file.Name,
                    });
                }
            }
        }

        // A map .img opened on its own has no directory wrapper, but it is the
        // same editable image the tree and round-trip loader already consume.
        // SelectRoleSources ensures these are used only when a WZ/IMG-folder
        // representation of Map is not already the aggregate source.
        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            if (file.LooseImage == null)
                continue;
            string stem = file.LooseImage.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                ? file.LooseImage.Name[..^4]
                : file.LooseImage.Name;
            if (!int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                || !seen.Add(id))
                continue;

            string? name = _strings.GetMapName(id);
            if (q != null
                && !stem.Contains(q, StringComparison.OrdinalIgnoreCase)
                && (name == null || !name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                continue;

            dto.Total++;
            dto.Maps.Add(new MapListRowDto
            {
                Id = id,
                Name = name,
                Path = file.Id,
                Source = file.Name,
            });
        }

        // A numeric query usually means "take me to this map-id range". Keep
        // fuzzy contains matching for names and partially remembered ids, but
        // never let those looser matches bury ids that begin with the query.
        // Sort before applying the limit so a large client cannot fill all 200
        // visible rows with traversal-order contains matches first.
        dto.Maps.Sort((a, b) =>
        {
            int rank = MatchRank(a).CompareTo(MatchRank(b));
            return rank != 0 ? rank : a.Id.CompareTo(b.Id);
        });
        if (dto.Maps.Count > limit)
        {
            dto.Truncated = true;
            dto.Maps = dto.Maps.Take(limit).ToList();
        }
        return dto;

        int MatchRank(MapListRowDto row)
        {
            if (q == null)
                return 0;
            // The picker presents map ids as nine digits. Rank the same text the
            // user sees, so searching 10000 puts 10000xxxx first rather than a
            // short id such as 10000 that is displayed as 000010000.
            string id = row.Id.ToString("D9", CultureInfo.InvariantCulture);
            if (id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (row.Name?.StartsWith(q, StringComparison.OrdinalIgnoreCase) == true)
                return 1;
            if (id.Contains(q, StringComparison.OrdinalIgnoreCase))
                return 2;
            return 3; // remaining matches contain the query in the name
        }
    }

    /// <summary>
    /// Every <c>Map/</c> directory in every open archive, in the client's mount
    /// order — the same union-not-pick rule <see cref="MapAssetService"/> was
    /// fixed to follow, because half a library looks exactly like a small client.
    /// </summary>
    private IEnumerable<(OpenFile File, WzObject Root, string Path)> MapRoots()
    {
        List<OpenFile> selected = Ordered(_session.SelectRoleSources("Map")).ToList();
        bool found = false;
        foreach (OpenFile file in selected)
        {
            foreach ((WzObject root, string path) in MapRoots(file))
            {
                found = true;
                yield return (file, root, path);
            }
        }
        if (found)
            yield break;

        // Map.wz can be assets-only; this client's geometry lives in Map002.wz.
        // If that sibling has not been opened yet, an already mounted extracted
        // Map folder is still a complete geometry source and must not disappear
        // merely because the asset archive won the general WZ-vs-IMG tie-break.
        // Take one fallback representation, never merge several clients.
        OpenFile? fallback = _session.Files.FirstOrDefault(file =>
            file.Kind == "img-folder"
            && selected.All(chosen => chosen.Id != file.Id)
            && _session.RoleRoot(file, "Map") != null);
        if (fallback == null)
            yield break;

        foreach ((WzObject root, string path) in MapRoots(fallback))
            yield return (fallback, root, path);
    }

    private IEnumerable<(WzObject Root, string Path)> MapRoots(OpenFile file)
    {
        WzDirectory? root = _session.RoleRoot(file, "Map");
        if (root == null)
            yield break;

        // Some extracted clients mount the map-id directory itself (Map0..
        // Map9 directly); others preserve the Map.wz wrapper (Map/Map0..).
        // Detect the shape from the children instead of guessing from the
        // selected folder's name.
        if (ChildrenOf(root).Any(IsMapIdDirectory))
        {
            yield return (root, _session.RoleRootPath(file, "Map"));
            yield break;
        }
        string rolePath = _session.RoleRootPath(file, "Map");
        foreach (WzObject match in ChildrenOf(root))
        {
            if (string.Equals(match.Name, "Map", StringComparison.OrdinalIgnoreCase))
            {
                yield return (match, WzPath.Child(rolePath, match.Name));
            }
        }
    }

    private static bool IsMapIdDirectory(WzObject child) =>
        child.Name is { Length: 4 } name
        && name.StartsWith("Map", StringComparison.Ordinal)
        && name[3] is >= '0' and <= '9';

    /// <summary>
    /// The logical children of any container, whatever MapleLib class holds them.
    ///
    /// <para>A .wz puts map buckets in directories and map images beside them:
    /// <c>Map/Map0/000000000.img</c> is <c>WzDirectory</c> → <c>WzDirectory</c> →
    /// <c>WzImage</c>. An NX archive has no directory type at all — every node is a
    /// property — so the same map is <c>WzImage</c> → <c>WzImageProperty</c> →
    /// <c>WzImageProperty</c>, and both <c>WzDirectory.WzDirectories</c> and
    /// <c>WzDirectory.WzImages</c> are empty at every level. A caller that reaches
    /// for those two collections sees an NX archive with no maps in it at all.</para>
    ///
    /// <para>It goes through <see cref="WzSessionService.EnumerateChildren"/>,
    /// which already carries this rule for the tree endpoints, rather than
    /// restating the kind switch here. That is deliberate: a second copy would be
    /// free to drift from the first, and the drift would show up as a picker that
    /// lists a map and a tree that cannot open it.</para>
    /// </summary>
    private IEnumerable<WzObject> ChildrenOf(WzObject container)
        => _session.EnumerateChildren(container);

    private static IEnumerable<OpenFile> Ordered(IEnumerable<OpenFile> files)
        => files
            .Select((f, index) => (File: f, Index: index))
            .OrderBy(x => MountRank(x.File))
            .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .Select(x => x.File);

    private static int MountRank(OpenFile file)
    {
        string stem = file.Name.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)
            ? file.Name[..^3] : file.Name;
        string family = WzSessionService.StripArchiveSuffix(stem);
        if (stem.Equals(family, StringComparison.OrdinalIgnoreCase))
            return -1;
        string digits = stem[family.Length..];
        return digits.Length == 3 && int.TryParse(digits, out int number) ? number : int.MaxValue;
    }

    #endregion

    #region Open / close

    /// <summary>
    /// <summary>
    /// Marks the <see cref="WzImage"/> that encloses <paramref name="node"/> as changed.
    ///
    /// MapleLib marks an image dirty from inside the image: <c>WzImage</c> sets
    /// <c>Changed</c> on AddProperty, RemoveProperty and Properties.Clear. An NX
    /// map is a WzSubProperty whose ancestors are another property and then the
    /// image, and clearing a property's collection never goes through WzImage —
    /// so the image stays clean, CountDirtyImages() returns 0, and the
    /// deliberate no-op guard in WzSaveService declines the save with "already
    /// up to date". The user edits a map, presses save, is told it worked, and
    /// the file on disk does not change. Walking up is what stops that.
    /// </summary>
    private static void MarkEnclosingImageDirty(WzObject node)
    {
        if (node is WzImage already) already.Changed = true;

        for (WzObject? current = node.Parent; current != null; current = current.Parent)
        {
            if (current is WzImage image)
            {
                image.Changed = true;
                return;
            }
        }
    }

    /// <summary>
    /// Opens a map through <see cref="MapRoundTrip.LoadVerified"/>, or throws
    /// with the model's refusal — the endpoint turns that into the banner. There
    /// is no partial open.
    ///
    /// <para><b>An NX map is opened editable.</b> NX models a map one level
    /// shallower than .wz — the image is <c>Map</c>, and the thing addressed as
    /// <c>Map0/000010000.img</c> is a property — so the address resolves to a node
    /// that is not an image at all. It is lifted into a synthetic image and read
    /// by the same model; saving writes the model back into the property the map
    /// really is, and verifies against the reopened archive.</para>
    ///
    /// <para>Measured rather than assumed: on a 679 MB <c>Map.nx</c> an edit
    /// saved in 9.5s with <c>verified: true</c>, and reading the value back from
    /// a freshly opened copy of the same file returned it.</para>
    /// </summary>
    public MapDocDto Open(string path)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(path, out EditorDoc? existing))
            {
                existing.Touched = DateTime.UtcNow;
                return BuildDto(existing);
            }

            MapLoadResult result;
            byte[] iv;
            int generation;
            string? readOnlyReason = null;
            lock (_session.Gate)
            {
                OpenFile file = _session.GetFileForPath(path);
                WzObject node = _session.Resolve(path);

                if (node is WzImage resolved)
                {
                    iv = file.CustomIv ?? WzTool.GetIvByMapleVersion(file.MapleVersion);
                    result = MapRoundTrip.LoadVerified(resolved, iv);
                }
                else if (IsNx(file))
                {
                    // A map in an NX archive is a WzSubProperty, not a WzImage:
                    // no block on disk to verify a rebuild against and no IV to
                    // serialise it with, so the pre-save round trip is skipped
                    // rather than faked.
                    //
                    // It is still editable. Save writes the rebuilt tree back
                    // into the property the map actually is, marks the
                    // enclosing image dirty so the archive writer sees the
                    // change, and then verifies by reopening the saved archive
                    // and comparing the tree -- which reads what is really on
                    // disk rather than a serialisation of what was about to be
                    // written. That is the stronger of the two checks, and it is
                    // the one that can run here.
                    WzImage lifted = NxMapImage(node);
                    iv = file.CustomIv ?? WzTool.GetIvByMapleVersion(file.MapleVersion);
                    result = MapDocument.Load(lifted);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"'{path}' is not a map image. The map editor opens whole .img map images.");
                }
                generation = _session.Generation;
            }

            if (!result.Ok)
            {
                // The refusal reason is the model's own sentence; it names the
                // node that differs. Surfaced as-is.
                throw new InvalidOperationException(result.Reason ?? "The map could not be opened.");
            }

            EditorDoc doc = new(path, result.Document!, iv, generation)
            {
                ReadOnlyReason = readOnlyReason,
            };
            EvictIfNeeded();
            _docs[path] = doc;
            return BuildDto(doc);
        }
    }

    /// <summary>Whether this entry is an NX archive. Its tree is a synthetic
    /// MapleLib build of the mapped file, so it is NX in every respect that
    /// matters here even though the objects look like ordinary WZ ones.</summary>
    private static bool IsNx(OpenFile file) =>
        string.Equals(file.Kind, "nx", StringComparison.Ordinal);

    /// <summary>
    /// Lifts an NX map node into the image the .wz reader would have produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NX puts a map one level shallower than .wz: in <c>Map.wz</c> the chain is
    /// Directory → Directory → WzImage, and in <c>Map.nx</c> it is WzImage →
    /// property → property. <see cref="WzNode.ReadImage"/> reads an image by
    /// iterating its top-level properties and nothing else, so an image whose
    /// properties are the NX node's own children is read by the unchanged model.
    /// </para>
    /// <para>
    /// The properties are moved, not shared. Re-parenting them into the
    /// synthetic image would detach them from the live session tree, so every
    /// other view of that archive would silently lose a map. A copy is the only
    /// honest thing to hand a document that cannot be written back — which is
    /// exactly why such a document is read-only.
    /// </para>
    /// <para>
    /// They are added through <see cref="WzPropertyCollection.Add"/> and not
    /// through <see cref="WzImage.AddProperty"/>, which throws on a duplicate
    /// name. Duplicate sibling names are legal here: 69 maps carry repeated
    /// foothold ids, and WZ has no uniqueness rule, so the index tracks a count
    /// per name precisely because the list may hold several.
    /// </para>
    /// </remarks>
    private static WzImage NxMapImage(WzObject node)
    {
        WzPropertyCollection? children = node switch
        {
            WzImageProperty property => property.WzProperties,
            WzImage image => image.WzProperties,
            _ => null,
        };

        if (children == null)
        {
            throw new InvalidOperationException(
                $"'{node.Name}' in an NX archive has no properties, so it holds no map to open.");
        }

        // The name the model derives the map id from, so it must be the leaf's:
        // "000010000.img", not "Map0".
        string leaf = node.Name is { Length: > 0 } name ? name : "map.img";
        WzImage lifted = new(leaf);
        foreach (WzImageProperty property in children)
            lifted.WzProperties.Add(property.DeepClone());

        // Parsed: these properties are already in hand and there is no reader
        // behind this image to parse from. Without it, MapDocument.Load's parse
        // attempt would fail on a null reader and refuse a map that reads fine.
        lifted.MarkWzImageAsParsed();
        return lifted;
    }

    public MapDocDto GetDoc(string path)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            doc.Touched = DateTime.UtcNow;
            return BuildDto(doc);
        }
    }

    public void Close(string path)
    {
        lock (_gate)
            _docs.Remove(path);
    }

    /// <summary>
    /// The editor's unsaved work, one row per open document. The topbar dirty
    /// chip reads this because map-editor edits live in these documents, not in
    /// the session tree — the session's own change summary cannot see them, and
    /// a chip that misses them says "nothing unsaved" over real work.
    /// </summary>
    public MapEditChangesDto Changes()
    {
        lock (_gate)
        {
            MapEditChangesDto dto = new();
            foreach (EditorDoc doc in _docs.Values)
            {
                dto.Docs.Add(new MapEditChangeRowDto
                {
                    Path = doc.Path,
                    ImageName = doc.Doc.ImageName,
                    Name = doc.Doc.MapId is int id ? _strings.GetMapName(id) : null,
                    UndoDepth = doc.Undo.Count,
                    Dirty = doc.Dirty,
                });
                if (doc.Dirty)
                {
                    dto.DirtyDocs++;
                    // Depth counts edits since load; after a save the clean
                    // point moves, so the distance FROM it is what is unsaved.
                    dto.EditCount += doc.CleanDepth >= 0
                        ? Math.Abs(doc.Undo.Count - doc.CleanDepth)
                        : doc.Undo.Count;
                }
            }
            return dto;
        }
    }

    private EditorDoc Require(string path)
    {
        if (!_docs.TryGetValue(path, out EditorDoc? doc))
        {
            throw new KeyNotFoundException(
                $"'{path}' is not open in the map editor. Open it first — documents do not survive " +
                "a restart or an eviction.");
        }
        return doc;
    }

    /// <summary>
    /// The gate every write to a document passes through, returning the
    /// document when it may be written and refusing it when it may not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A document that is read-only is read-only because its content is a copy:
    /// an NX map is read out of a tree that has no image to write back into, so
    /// the model holds a snapshot rather than a handle. An edit to it would
    /// succeed, look right, and be gone at save time — or worse, be "saved" into
    /// an image the archive does not have. A refusal that costs one sentence is
    /// far cheaper than work that disappears.
    /// </para>
    /// <para>
    /// Placed at <see cref="Require"/> rather than inside each op, so a new edit
    /// kind cannot forget it. Reading — the document, the inspector, the history
    /// — never comes through here, because a read-only map is readable.
    /// </para>
    /// </remarks>
    private EditorDoc RequireWritable(string path)
    {
        EditorDoc doc = Require(path);
        if (doc.ReadOnlyReason is { Length: > 0 } reason)
        {
            throw new InvalidOperationException(
                reason + " Nothing was changed.");
        }
        return doc;
    }

    private void EvictIfNeeded()
    {
        while (_docs.Count >= MaxOpenDocs)
        {
            // Never evict a document holding unsaved edits while a clean one is
            // available; losing edits to a cache policy would be the exact kind
            // of silent loss this tool exists to end.
            EditorDoc? victim = _docs.Values.Where(d => !d.Dirty).OrderBy(d => d.Touched).FirstOrDefault()
                ?? _docs.Values.OrderBy(d => d.Touched).First();
            if (victim.Dirty)
            {
                _log.LogWarning("Map editor evicting DIRTY document {Path}; its unsaved edits are lost.",
                    victim.Path);
            }
            _docs.Remove(victim.Path);
        }
    }

    #endregion

    #region Edits

    /// <summary>
    /// Applies many operations as ONE undoable step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: the undo stack holds 200 entries. A generated floor of
    /// 300 tiles submitted one call at a time blows past that, and the oldest
    /// history is silently discarded — the caller then cannot undo what it did,
    /// and cannot tell that it cannot. Measured: 20 <c>/place</c> calls take
    /// 0.17s, so the round trips were never the cost; the lost history was.
    /// </para>
    /// <para>
    /// The whole batch collapses into a single history entry by taking the stack
    /// depth before and after and grouping everything pushed in between. One
    /// undo then reverts the batch as a unit, which is what "I laid a floor and
    /// it was wrong" means to a caller.
    /// </para>
    /// <para>
    /// Stop on first failure, and say which operation failed. A half-applied
    /// batch is worse than a rejected one: the caller would have to work out
    /// what landed before it could undo, and <see cref="StopOnError"/> plus the
    /// reported index is what makes the outcome unambiguous.
    /// </para>
    /// </remarks>
    public MapBatchResultDto Batch(MapBatchRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = RequireWritable(request.Path);
            doc.Touched = DateTime.UtcNow;

            MapBatchResultDto result = new()
            {
                Path = request.Path,
                Requested = request.Ops.Count,
            };

            if (request.Ops.Count == 0)
            {
                result.Notes.Add("Nothing to do: the batch was empty.");
                result.Ok = true;
                return result;
            }

            // Snapshot the history so everything this batch pushes can be
            // regrouped afterwards, whatever the individual ops pushed.
            int depthBefore = doc.Undo.Count;
            List<Change> pushed = new();

            for (int i = 0; i < request.Ops.Count; i++)
            {
                MapEditOp op = request.Ops[i];
                try
                {
                    MapEditResultDto step = op.Kind switch
                    {
                        "setValue" => SetValue(doc, op),
                        "move" => Move(doc, op),
                        "moveFoothold" => MoveFoothold(doc, op),
                        "moveLife" => MoveLife(doc, op),
                        "delete" => Delete(doc, op),
                        "setField" => SetField(doc, op),
                        "moveMany" => MoveMany(doc, op),
                        "deleteMany" => DeleteMany(doc, op),
                        "duplicate" => Duplicate(doc, op),
                        "insertFootholdVertex" => InsertFootholdVertex(doc, op),
                        "extendFoothold" => ExtendFoothold(doc, op),
                        "moveLadder" => MoveLadder(doc, op),
                        "setRect" => SetRect(doc, op),
                        _ => throw new ArgumentException(UnknownEditKind(op.Kind)),
                    };

                    result.Applied++;
                    foreach (int[] addr in step.Placed)
                        result.Placed.Add(addr);
                    foreach (string note in step.Notes)
                        result.Notes.Add($"[{i}] {note}");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    // A rejected op is a sentence about the caller's input, not a
                    // server fault, so it comes back as data rather than a 500.
                    result.FailedAt = i;
                    result.Error = $"Operation {i} ('{op.Kind}') failed: {ex.Message}";

                    if (!request.StopOnError)
                        continue;

                    // Stop, but keep what already landed as one undoable step so
                    // the caller can roll the whole attempt back in one call.
                    result.Partial = result.Applied > 0;
                    break;
                }
            }

            CollapseBatch(doc, depthBefore, request.Label);
            result.Ok = result.FailedAt < 0;
            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    /// <summary>
    /// Folds every history entry a batch pushed back into one, so the batch is a
    /// single undo step and cannot exhaust the stack on its own.
    /// </summary>
    private static void CollapseBatch(EditorDoc doc, int depthBefore, string? label)
    {
        int pushed = doc.Undo.Count - depthBefore;
        if (pushed <= 0)
            return;

        // The stack holds the newest at the top, so the batch's entries are the
        // last `pushed` of ToArray() — and they must be replayed oldest-first.
        Change[] entries = doc.Undo.ToArray();
        Change[] batch = entries[^pushed..];

        Change[] kept = entries[..^pushed];
        Change grouped = Change.Group(
            string.IsNullOrWhiteSpace(label) ? $"batch of {pushed} edits" : label,
            batch);

        doc.Undo.Clear();
        for (int i = kept.Length - 1; i >= 0; i--)
            doc.Undo.Push(kept[i]);
        doc.Undo.Push(grouped);
    }

    /// <summary>The op kinds <c>/edit</c> offers, named in one place so the
    /// refusal text and the switch cannot drift apart.</summary>
    private static string UnknownEditKind(string kind) =>
        $"'{kind}' is not an edit this endpoint knows. It offers setValue, setField, " +
        "move, moveFoothold, moveLife, delete, moveMany, deleteMany, duplicate, " +
        "insertFootholdVertex, extendFoothold, moveLadder and setRect.";

    public MapEditResultDto Edit(MapEditRequest request)
    {
        lock (_gate)
        {
            EditorDoc doc = RequireWritable(request.Path);
            doc.Touched = DateTime.UtcNow;
            MapEditOp op = request.Op;

            MapEditResultDto result = op.Kind switch
            {
                "setValue" => SetValue(doc, op),
                "move" => Move(doc, op),
                "moveFoothold" => MoveFoothold(doc, op),
                "moveLife" => MoveLife(doc, op),
                "delete" => Delete(doc, op),
                "setField" => SetField(doc, op),
                "moveMany" => MoveMany(doc, op),
                "deleteMany" => DeleteMany(doc, op),
                "duplicate" => Duplicate(doc, op),
                "insertFootholdVertex" => InsertFootholdVertex(doc, op),
                "extendFoothold" => ExtendFoothold(doc, op),
                "moveLadder" => MoveLadder(doc, op),
                "setRect" => SetRect(doc, op),
                _ => throw new ArgumentException(UnknownEditKind(op.Kind)),
            };

            result.UndoDepth = doc.Undo.Count;
            result.RedoDepth = doc.Redo.Count;
            result.Dirty = doc.Dirty;
            return result;
        }
    }

    public MapUndoResultDto UndoRedo(string path, bool redo)
    {
        lock (_gate)
        {
            EditorDoc doc = RequireWritable(path);
            Stack<Change> from = redo ? doc.Redo : doc.Undo;
            Stack<Change> to = redo ? doc.Undo : doc.Redo;

            string? applied = null;
            if (from.Count > 0)
            {
                Change change = from.Pop();
                if (redo) change.Apply(); else change.Revert();
                to.Push(change);
                applied = change.Label;
                // Clean means "the model equals what was loaded or last saved".
                // History survives a save, so the clean point is the undo depth
                // recorded AT the save (0 for a fresh document) — undoing past
                // it makes the document dirty in the other direction.
                doc.Dirty = doc.Undo.Count != doc.CleanDepth;
            }

            return new MapUndoResultDto
            {
                Applied = applied,
                UndoDepth = doc.Undo.Count,
                RedoDepth = doc.Redo.Count,
                Dirty = doc.Dirty,
            };
        }
    }

    private static MapEditResultDto SetValue(EditorDoc doc, MapEditOp op)
    {
        WzNode node = doc.NodeAt(op.Addr);
        if (op.Value == null)
            throw new ArgumentException("setValue needs a value.");

        // Snapshot by the type the node already has; SetText/SetNumber keep it.
        Change change = ValueChange(node, $"set {node.Name}");
        node.SetText(op.Value);
        change.CaptureNew();
        doc.Push(change);

        return new MapEditResultDto { Structural = false };
    }

    private static MapEditResultDto Move(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("move needs x and y.");

        WzNode node = doc.NodeAt(op.Addr);
        WzNode? x = node.Child("x");
        WzNode? y = node.Child("y");
        if (x == null || y == null)
        {
            throw new InvalidOperationException(
                $"'{node.Name}' has no x/y children to move. Only entries that store their position " +
                "as x and y can be dragged.");
        }

        Change cx = ValueChange(x, "");
        Change cy = ValueChange(y, "");
        x.SetNumber(nx);
        y.SetNumber(ny);
        cx.CaptureNew();
        cy.CaptureNew();
        doc.Push(Change.Group($"move {node.Name}", cx, cy));

        return new MapEditResultDto
        {
            Structural = false,
            Moved = { new MapMovedDto { Addr = op.Addr, X1 = nx, Y1 = ny } },
        };
    }

    /// <summary>
    /// Moves one endpoint of one foothold, and with it every linked endpoint
    /// that coincided with it — the invariant the data actually keeps: on all
    /// but 38 of 784,735 footholds, <c>prev</c>'s (x2,y2) equals this one's
    /// (x1,y1).
    ///
    /// <para><b>What this never does:</b> it never writes <c>prev</c> or
    /// <c>next</c>, and it never "repairs" an asymmetric pair. 6,744 footholds
    /// ship with a prev whose next is someone else — forks — and every one of
    /// them is legal.</para>
    ///
    /// <para>Propagation is a connected-component walk over the endpoints that
    /// sat at the old point <i>within the same foothold layer</i> (chains cross
    /// groups freely — 54,016 do — but cross a layer only 5 times in the whole
    /// client, so a layer is the propagation boundary), following only actual
    /// prev/next references. Two unrelated footholds that merely happen to
    /// touch the same coordinate are left alone.</para>
    /// </summary>
    private MapEditResultDto MoveFoothold(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("moveFoothold needs x and y.");
        if (op.Vertex is not (1 or 2))
            throw new ArgumentException("moveFoothold needs vertex 1 or 2.");
        if (op.Addr.Length != 4)
            throw new ArgumentException("A foothold address is foothold/layer/group/id — four segments.");

        WzNode root = doc.Doc.Nodes[op.Addr[0]];
        if (!string.Equals(root.Name, "foothold", StringComparison.Ordinal))
            throw new ArgumentException("moveFoothold only moves footholds.");

        WzNode layer = root.Children[op.Addr[1]];
        WzNode moving = doc.NodeAt(op.Addr);

        (string x1, string y1) = op.Vertex == 1 ? ("x1", "y1") : ("x2", "y2");
        long? px = moving.IntegerAt(x1);
        long? py = moving.IntegerAt(y1);
        if (px == null || py == null)
        {
            throw new InvalidOperationException(
                $"Foothold '{moving.Name}' has no {x1}/{y1}, so there is no vertex to move.");
        }

        // Everyone whose endpoint sat exactly at the old point, in this layer.
        List<(WzNode Node, int Vertex)> atPoint = new();
        foreach (WzNode group in layer.Children)
        {
            foreach (WzNode fh in group.Children)
            {
                if (fh.IntegerAt("x1") == px && fh.IntegerAt("y1") == py)
                    atPoint.Add((fh, 1));
                if (fh.IntegerAt("x2") == px && fh.IntegerAt("y2") == py)
                    atPoint.Add((fh, 2));
            }
        }

        // Connected component containing the dragged foothold, linked through
        // prev/next among the members. Forks join through their shared
        // neighbour, not through each other, which is exactly what a component
        // walk gives and a direct-link test would not.
        // WzNode does not override Equals, so set membership is reference
        // identity — which is the correct identity for nodes of a tree that
        // legally repeats names and values.
        HashSet<WzNode> component = new() { moving };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((WzNode fh, int _) in atPoint)
            {
                if (component.Contains(fh))
                    continue;
                foreach (WzNode member in component)
                {
                    if (Linked(fh, member))
                    {
                        component.Add(fh);
                        grew = true;
                        break;
                    }
                }
            }
        }

        List<Change> changes = new();
        List<(WzNode Node, int Vertex)> movedEndpoints = new();
        foreach ((WzNode fh, int vertex) in atPoint)
        {
            if (!component.Contains(fh))
                continue;
            (string fx, string fy) = vertex == 1 ? ("x1", "y1") : ("x2", "y2");
            WzNode xNode = fh.Child(fx)!;
            WzNode yNode = fh.Child(fy)!;
            Change cx = ValueChange(xNode, "");
            Change cy = ValueChange(yNode, "");
            xNode.SetNumber(nx);
            yNode.SetNumber(ny);
            cx.CaptureNew();
            cy.CaptureNew();
            changes.Add(cx);
            changes.Add(cy);
            movedEndpoints.Add((fh, vertex));
        }
        doc.Push(Change.Group($"move foothold {moving.Name} vertex {op.Vertex}", changes.ToArray()));

        // Report every foothold that moved, with its full geometry, addressed.
        MapEditResultDto result = new() { Structural = false };
        foreach (WzNode fh in movedEndpoints.Select(m => m.Node).Distinct())
        {
            int[]? addr = AddrOfFoothold(doc, op.Addr[0], fh);
            if (addr == null)
                continue;
            result.Moved.Add(new MapMovedDto
            {
                Addr = addr,
                X1 = fh.IntegerAt("x1") ?? 0,
                Y1 = fh.IntegerAt("y1") ?? 0,
                X2 = fh.IntegerAt("x2") ?? 0,
                Y2 = fh.IntegerAt("y2") ?? 0,
            });
        }
        return result;
    }

    /// <summary>
    /// Moves a life spawn by re-anchoring it: the foothold under the drop point
    /// is found the way placement finds one, <c>fh</c> is rewritten to its id
    /// and <c>cy</c> (and <c>y</c>) to that foothold's own y at the new x — the
    /// y/cy pairing that made freehand dragging unsafe, kept consistent by
    /// construction instead of being silently split. <c>rx0</c>/<c>rx1</c>
    /// (the patrol range) ride along by the same x delta, keeping their width.
    ///
    /// <para>Nothing below the drop point is a refusal, not a guess — the same
    /// sentence placement uses. Fields the record does not carry are not
    /// invented; only what is there is rewritten.</para>
    /// </summary>
    private static MapEditResultDto MoveLife(EditorDoc doc, MapEditOp op)
    {
        if (op.X is not long nx || op.Y is not long ny)
            throw new ArgumentException("moveLife needs x and y — the drop point.");

        WzNode life = doc.NodeAt(op.Addr);
        WzNode? x = life.Child("x");
        WzNode? y = life.Child("y");
        if (x == null || y == null)
            throw new InvalidOperationException(
                $"'{life.Name}' has no x/y children, so there is nothing to move.");

        long oldX = x.Integer;
        List<Change> changes = new();
        List<string> notes = new();

        void Write(WzNode node, long value)
        {
            Change change = ValueChange(node, "");
            node.SetNumber(value);
            change.CaptureNew();
            changes.Add(change);
        }

        // The anchor rule is placement's own: nearest standable foothold at or
        // below the drop point, cy from its geometry at the new x.
        (long fhId, long cy) = AnchorToFoothold(doc, nx, ny, null);

        Write(x, nx);
        Write(y, cy);
        if (life.Child("cy") is WzNode cyNode)
            Write(cyNode, cy);
        if (life.Child("fh") is WzNode fhNode)
        {
            if (fhNode.Integer != fhId)
                notes.Add($"Re-anchored to foothold {fhId}; cy recomputed as {cy}.");
            Write(fhNode, fhId);
        }

        long dx = nx - oldX;
        if (dx != 0)
        {
            if (life.Child("rx0") is WzNode rx0)
                Write(rx0, rx0.Integer + dx);
            if (life.Child("rx1") is WzNode rx1)
                Write(rx1, rx1.Integer + dx);
        }

        doc.Push(Change.Group($"move life {life.TextAt("id") ?? life.Name}", changes.ToArray()));

        return new MapEditResultDto
        {
            Structural = false,
            Notes = notes,
            Moved = { new MapMovedDto { Addr = op.Addr, X1 = nx, Y1 = cy } },
        };
    }

    /// <summary>Whether either foothold names the other through prev or next.
    /// Ids are node names; prev/next are stored numbers. One-directional is
    /// enough — that is what a fork looks like.</summary>
    private static bool Linked(WzNode a, WzNode b)
    {
        return Names(a.IntegerAt("prev"), b.Name) || Names(a.IntegerAt("next"), b.Name)
            || Names(b.IntegerAt("prev"), a.Name) || Names(b.IntegerAt("next"), a.Name);

        static bool Names(long? reference, string id) =>
            reference is long r && r != 0
            && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            && parsed == r;
    }

    private static int[]? AddrOfFoothold(EditorDoc doc, int rootIndex, WzNode fh)
    {
        WzNode root = doc.Doc.Nodes[rootIndex];
        for (int l = 0; l < root.Children.Count; l++)
        {
            WzNode layer = root.Children[l];
            for (int g = 0; g < layer.Children.Count; g++)
            {
                WzNode group = layer.Children[g];
                for (int i = 0; i < group.Children.Count; i++)
                {
                    if (ReferenceEquals(group.Children[i], fh))
                        return new[] { rootIndex, l, g, i };
                }
            }
        }
        return null;
    }

    private MapEditResultDto Delete(EditorDoc doc, MapEditOp op)
    {
        if (op.Addr.Length < 2)
        {
            throw new InvalidOperationException(
                "A top-level node is never deleted from the map editor. Deleting a whole kind — " +
                "even an empty container — is a diff against the client: reactor ships empty on " +
                "9,560 maps and pruning it changes the map.");
        }

        WzNode parent = doc.NodeAt(op.Addr[..^1]);
        int index = op.Addr[^1];
        if (index < 0 || index >= parent.Children.Count)
            throw new ArgumentException("The address names a child that is not there.");
        WzNode child = parent.Children[index];

        // A deleted subtree leaves the document but stays in the undo stack —
        // which the save's payload sweep cannot see. Pin and detach its
        // payloads NOW, while the reader they point at is still open, so
        // undoing the delete after a save restores real bytes. A failure here
        // is not fatal to the delete; it would only surface as the save's
        // honest fallback if this exact node is ever restored.
        lock (_session.Gate)
        {
            _ = PinPayloads(child, child.Name) ?? DetachPayloads(child, child.Name);
        }

        parent.Remove(child);
        doc.Push(new Change($"delete {child.Name}",
            apply: () => parent.Remove(child),
            revert: () => parent.Insert(index, child)));

        return new MapEditResultDto { Structural = true };
    }

    /// <summary>A reversible scalar write. Old value captured now, new value
    /// after the caller writes it.</summary>
    private static Change ValueChange(WzNode node, string label)
    {
        string? oldText = null;
        double oldNumber = 0;
        bool numeric;
        switch (node.Type)
        {
            case WzPropertyType.String:
            case WzPropertyType.UOL:
                oldText = node.Text;
                numeric = false;
                break;
            case WzPropertyType.Short:
            case WzPropertyType.Int:
            case WzPropertyType.Long:
                oldNumber = node.Integer;
                numeric = true;
                break;
            case WzPropertyType.Float:
                oldNumber = node.Single;
                numeric = true;
                break;
            case WzPropertyType.Double:
                oldNumber = node.Double;
                numeric = true;
                break;
            default:
                throw new InvalidOperationException(
                    $"'{node.Name}' is a {node.Type} and its value cannot be edited here. Containers, " +
                    "vectors and binary payloads are not scalar edits.");
        }

        string? newText = null;
        double newNumber = 0;
        Change change = null!;
        change = new Change(label,
            apply: () =>
            {
                if (numeric) node.SetNumber(newNumber);
                else node.SetText(newText ?? "");
            },
            revert: () =>
            {
                if (numeric) node.SetNumber(oldNumber);
                else node.SetText(oldText ?? "");
            })
        {
            OnCaptureNew = () =>
            {
                if (numeric)
                {
                    newNumber = node.Type switch
                    {
                        WzPropertyType.Float => node.Single,
                        WzPropertyType.Double => node.Double,
                        _ => node.Integer,
                    };
                }
                else
                {
                    newText = node.Text;
                }
            },
        };
        return change;
    }

    #endregion

    #region Inspector

    /// <summary>The raw view of one node and its immediate children.</summary>
    public MapNodeDto InspectNode(string path, int[] addr)
    {
        lock (_gate)
        {
            EditorDoc doc = Require(path);
            WzNode node = doc.NodeAt(addr);
            MapNodeDto dto = Describe(node, addr);
            for (int i = 0; i < node.Children.Count; i++)
            {
                int[] childAddr = new int[addr.Length + 1];
                addr.CopyTo(childAddr, 0);
                childAddr[^1] = i;
                dto.Children.Add(Describe(node.Children[i], childAddr));
            }
            return dto;
        }
    }

    private static MapNodeDto Describe(WzNode node, int[] addr) => new()
    {
        Addr = addr,
        Name = node.Name,
        Type = node.Type.ToString(),
        Value = node.Type switch
        {
            WzPropertyType.Vector =>
                $"({(node.HasVectorX ? node.Integer.ToString(CultureInfo.InvariantCulture) : "·")}, "
                + $"{(node.HasVectorY ? node.VectorY.ToString(CultureInfo.InvariantCulture) : "·")})",
            WzPropertyType.SubProperty or WzPropertyType.Convex or WzPropertyType.Canvas => null,
            _ => node.AsText(),
        },
        HasChildren = node.IsContainer && node.Children.Count > 0,
        ChildCount = node.IsContainer ? node.Children.Count : 0,
    };

    #endregion

    #region Save

    /// <summary>
    /// Rebuilds the image from the model, writes the archive through
    /// <see cref="WzSaveService"/>, and verifies against the saved-and-reopened
    /// file: the saved image's bytes must be exactly the model's bytes.
    ///
    /// <para><b>The document — and its undo history — survives the save.</b>
    /// The old hazard was carried payloads (canvas pixels, unmodelled binaries)
    /// pointing into the reader the save replaces, so every payload is pinned
    /// into memory BEFORE the write, while the old reader is still open. Only
    /// when a payload cannot be pinned (a shape nobody has shipped yet) does
    /// the save fall back to reloading the document, and it says so.</para>
    /// </summary>
    public MapSaveResultDto Save(string path)
    {
        lock (_gate)
        {
            EditorDoc doc = RequireWritable(path);

            byte[] modelBytes;
            OpenFile file;
            string? pinFailure;
            lock (_session.Gate)
            {
                file = _session.GetFileForPath(path);
                if (file.ReadOnly)
                {
                    throw new InvalidOperationException(
                        $"'{file.Name}' is reference-only. Release it before saving map edits into it.");
                }
                RefuseLiveClient(file);

                if (_session.Generation != doc.Generation)
                {
                    throw new InvalidOperationException(
                        "The session changed since this map was opened — an archive was opened, " +
                        "closed or saved — so the document's carried pixels may no longer be " +
                        "readable. Reopen the map and redo the edit rather than saving blind.");
                }

                // An NX map is not a WzImage -- it is a WzSubProperty -- but both
                // are WzImageProperty and both expose the same collection, so the
                // write below is one path rather than two. What differs is only
                // whether there are bytes to verify the rebuild against.
                WzObject liveNode = _session.Resolve(path);
                if (liveNode is not IPropertyContainer live)
                    throw new InvalidOperationException($"'{path}' no longer resolves to an image.");

                // While the old reader is still the one the payloads point at.
                pinFailure = PinPayloads(doc.Doc);

                WzImage rebuilt = doc.Doc.Build();
                // The rebuilt image shares the document's payload objects, and
                // the post-save reopen will DISPOSE the tree they ride in with
                // — nulling even pinned byte caches on those objects. Swap the
                // document onto byte-faithful clones now: the originals go
                // into the write, the clones stay here, and the second save's
                // byte-verification is the proof the clones are exact.
                if (pinFailure == null)
                    pinFailure = DetachPayloads(doc.Doc);

                // An NX map has no block of its own to serialise against and no
                // IV, so there is nothing to compare and the comparison is
                // skipped rather than faked. The reopen-and-compare below is
                // what stands in for it.
                if (live is WzImage image)
                {
                    modelBytes = MapRoundTrip.Serialize(rebuilt, doc.Iv);
                    WzSessionService.EnsureParsed(image);
                }
                else
                {
                    modelBytes = [];
                }

                // The model built the tree; the session's copy now becomes it.
                live.ClearProperties();
                foreach (WzImageProperty property in rebuilt.WzProperties)
                    live.AddProperty(property);
                MarkEnclosingImageDirty(liveNode);
            }

            SaveResult saved;
            try
            {
                saved = _save.Save(new SaveRequest { FileId = file.Id });
            }
            catch
            {
                // The save failed and the archive on disk is untouched (the save
                // service writes to a temp file first). The in-session tree,
                // however, now holds the rebuilt image; that is still the
                // document's content, so nothing is lost — but say what state
                // things are in rather than letting the next save surprise.
                _log.LogWarning("Map save failed for {Path}; the in-session image holds the edited tree.", path);
                throw;
            }

            // Verify on the saved and reopened archive, never on a counter.
            MapSaveResultDto result = new()
            {
                SavedTo = saved.SavedTo,
                BackupPath = saved.BackupPath,
                ArchiveBytes = saved.Bytes,
                Seconds = saved.Seconds,
                UnmodelledCarried = doc.Doc.Unmodelled.Count,
            };

            lock (_session.Gate)
            {
                WzObject reopenedNode = _session.Resolve(path);
                if (reopenedNode is not IPropertyContainer reopened)
                {
                    result.Verified = false;
                    result.Differences.Add("The saved archive no longer holds this map at its path.");
                    return result;
                }

                if (reopenedNode is WzImage reopenedImage)
                {
                    WzSessionService.EnsureParsed(reopenedImage);
                    byte[] savedBytes = MapRoundTrip.Serialize(reopenedImage, doc.Iv);
                    result.Verified = modelBytes.Length > 0 && savedBytes.AsSpan().SequenceEqual(modelBytes);

                    if (!result.Verified)
                    {
                        foreach (MapNodeDifference difference in
                                 MapRoundTrip.CompareForTest(reopenedImage, doc.Doc.Build()))
                            result.Differences.Add(difference.ToString());
                        if (result.Differences.Count == 0)
                            result.Differences.Add("The bytes differ although every node matches.");
                    }
                }
                else
                {
                    // An NX map has no bytes to compare, so the saved tree is
                    // compared against the model instead. That is the stronger
                    // check of the two: it reads what is actually on disk rather
                    // than a serialisation of what was about to be written.
                    foreach (MapNodeDifference difference in
                             MapRoundTrip.CompareForTest(
                                 reopenedNode is WzImage ri ? ri : NxMapImage(reopenedNode),
                                 doc.Doc.Build()))
                        result.Differences.Add(difference.ToString());
                    result.Verified = result.Differences.Count == 0;
                }

                if (result.Verified && pinFailure == null)
                {
                    // The document lives on: every payload is memory-backed, the
                    // saved file equals the model, and the history on both sides
                    // of this point still applies to these exact nodes.
                    doc.Generation = _session.Generation;
                    doc.MarkSaved();
                    result.HistoryKept = true;
                }
                else
                {
                    // The fallback arm: a payload that could not be pinned, or a
                    // verification failure. Reload from the saved file the way
                    // every save used to, and say what was lost and why.
                    result.HistoryKept = false;
                    result.HistoryNote = pinFailure != null
                        ? $"Undo history was cleared by this save: {pinFailure}"
                        : "Undo history was cleared because verification failed and the document " +
                          "was reloaded from the saved file.";
                    // Reloaded through the same lift Open used: an NX map is a property
                    // and the model wants an image, so the container is presented
                    // to it rather than the document being closed and reopened
                    // by hand.
                    MapLoadResult reload = MapDocument.Load(
                        reopenedNode is WzImage reopenImage ? reopenImage : NxMapImage(reopenedNode));
                    if (reload.Ok)
                    {
                        _docs[path] = new EditorDoc(path, reload.Document!, doc.Iv, _session.Generation)
                        {
                            // Nothing here closes an NX document. Open accepts it
                            // and Save writes it back, both verified on a real
                            // Map.nx; the arm that used to set NxReadOnlyReason
                            // here made a successfully saved map go read-only for
                            // the rest of the session.
                        };
                    }
                    else
                    {
                        _docs.Remove(path);
                        result.Differences.Add(
                            $"The saved image would not reload: {reload.Reason} The document was closed.");
                        result.Verified = false;
                    }
                }
            }

            // An NX session entry cannot keep pointing at the file it just
            // replaced: every canvas holds a reference to the NxArchive mapping
            // it was decoded from plus a bitmap-table index into it, and the
            // writer copies untouched canvases out of that mapping by index.
            // Left alone, the second save of this session asked a 34,386-entry
            // table for index 34415 and the process died.
            //
            // Done HERE, after the verification above, not in the save service:
            // that verification re-resolves the path and compares the tree, and
            // replacing the session tree first would make it compare against the
            // file just written rather than against what was written.
            //
            // It also sits OUTSIDE the session gate block above. ReopenNx builds
            // the new tree off-gate by design, but everything after it -- the
            // reload of the document onto the new tree -- must not run while
            // every other session request is held up behind a 679 MB conversion.
            if (IsNx(file) && result.Verified)
            {
                result.ReopenMs = _session.ReopenNx(file, file.FilePath);

                // The document was built over the old tree. Reload it from the
                // reopened archive, or the next edit lands on nodes the session
                // no longer holds.
                WzObject reopenedRoot = _session.Resolve(path);
                if (reopenedRoot is IPropertyContainer)
                {
                    WzImage reloadImage = reopenedRoot as WzImage ?? NxMapImage(reopenedRoot);
                    MapLoadResult afterReopen = MapDocument.Load(reloadImage);
                    if (afterReopen.Ok)
                    {
                        _docs[path] = new EditorDoc(path, afterReopen.Document!, doc.Iv, _session.Generation);
                    }
                    else
                    {
                        _docs.Remove(path);
                        result.Differences.Add(
                            $"The saved map would not reload after reopening the archive: {afterReopen.Reason} "
                            + "The document was closed.");
                        result.Verified = false;
                    }
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Pins every carried payload in the model into memory, so the document
    /// outlives the reader it was parsed from. Returns null on success, or a
    /// sentence naming the first payload that could not be pinned — the caller
    /// then falls back to reloading the document and says so, rather than
    /// keeping a model that would throw on its next build.
    /// </summary>
    private static string? PinPayloads(MapDocument map)
    {
        foreach (WzNode root in map.Nodes)
        {
            string? failure = PinPayloads(root, root.Name);
            if (failure != null)
                return failure;
        }
        return null;
    }

    private static string? PinPayloads(WzNode node, string where)
    {
        if (node.Payload is WzImageProperty payload)
        {
            try
            {
                // An NX canvas is not a payload this needs to pin. NxWriter copies an
                // untouched one straight out of the mapped file by index and never
                // reads its compressed bytes; only an EDITED canvas is re-encoded,
                // and it reaches NxWriter as an ordinary WzPngProperty through the
                // export path, which converts it there.
                //
                // Pinning one here reported a null-reader failure for a payload that
                // was perfectly readable and cleared the undo history over it.
                // Converting one instead - the first attempt at this fix - decoded
                // every canvas in the map and killed the process part-way through
                // the save. Neither is right; carrying it through untouched is.
                if (payload is NxPngProperty)
                {
                    foreach (WzNode child in node.Children)
                    {
                        string? childFailure = PinPayloads(child, $"{where}/{child.Name}");
                        if (childFailure != null)
                            return childFailure;
                    }
                    return null;
                }

                switch (payload)
                {
                    case WzPngProperty png:
                        // A canvas built in memory (a regenerated minimap, a
                        // fresh placement) compressed its bitmap at set time, so
                        // this returns the cached bytes without touching any
                        // reader; a parsed canvas reads its bytes NOW, while the
                        // reader is still the one it was parsed from.
                        if (png.GetCompressedBytes(saveInMemory: true) == null)
                            return $"the pixels under '{where}' could not be read into memory.";
                        break;
                    case WzBinaryProperty sound:
                        if (sound.GetBytes(saveInMemory: true) == null)
                            return $"the sound under '{where}' could not be read into memory.";
                        break;
                    case WzRawDataProperty raw:
                        if (raw.GetBytes(saveInMemory: true) == null)
                            return $"the raw data under '{where}' could not be read into memory.";
                        break;
                    case WzVideoProperty video:
                        if (video.GetBytes(saveInMemory: true) == null)
                            return $"the video under '{where}' could not be read into memory.";
                        break;
                    case WzLuaProperty:
                        break; // always memory-backed
                    default:
                        return $"'{where}' carries a {payload.GetType().Name}, a payload this " +
                            "save does not know how to pin in memory.";
                }
            }
            catch (Exception ex)
            {
                return $"pinning the payload under '{where}' failed: {ex.Message}";
            }
        }

        foreach (WzNode child in node.Children)
        {
            string? failure = PinPayloads(child, $"{where}/{child.Name}");
            if (failure != null)
                return failure;
        }
        return null;
    }

    /// <summary>Every payload swapped for its owned-bytes clone — see
    /// <see cref="WzNode.DetachPayload"/>. Null on success; on the first
    /// failure, a sentence naming the node, and the caller falls back to the
    /// reload arm.</summary>
    private static string? DetachPayloads(MapDocument map)
    {
        foreach (WzNode root in map.Nodes)
        {
            string? failure = DetachPayloads(root, root.Name);
            if (failure != null)
                return failure;
        }
        return null;
    }

    private static string? DetachPayloads(WzNode node, string where)
    {
        try
        {
            node.DetachPayload();
        }
        catch (Exception ex)
        {
            return $"cloning the payload under '{where}' failed: {ex.Message}";
        }
        foreach (WzNode child in node.Children)
        {
            string? failure = DetachPayloads(child, $"{where}/{child.Name}");
            if (failure != null)
                return failure;
        }
        return null;
    }

    /// <summary>The one folder this tool never writes: the live client.</summary>
    private static void RefuseLiveClient(OpenFile file)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(file.FilePath); }
        catch { return; }
        if (full.StartsWith(@"C:\MapleStory\232", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{file.FilePath}' is inside the live client folder, which this editor never " +
                "writes. Work on a copy — the game launcher re-downloads edited archives with no " +
                "backup, so an edit here is both dangerous and futile.");
        }
    }

    #endregion

    #region Document DTO

    private MapDocDto BuildDto(EditorDoc doc)
    {
        MapDocument map = doc.Doc;
        MapDocDto dto = new()
        {
            Path = doc.Path,
            ImageName = map.ImageName,
            MapId = map.MapId,
            Name = map.MapId is int id ? _strings.GetMapName(id) : null,
            Link = map.Info?.Link,
            HasGeometry = map.HasGeometry,
            Bgm = map.Info?.Bgm,
            MapMark = map.Info?.MapMark,
            UndoDepth = doc.Undo.Count,
            RedoDepth = doc.Redo.Count,
            Dirty = doc.Dirty,
            ReadOnly = doc.ReadOnlyReason != null,
            ReadOnlyReason = doc.ReadOnlyReason,
        };

        foreach (WzNode node in map.Unmodelled)
            dto.UnmodelledNames.Add(node.Name);
        dto.UnmodelledCount = dto.UnmodelledNames.Count;

        // One pass over the top level, index in hand — addresses are index
        // paths, and sibling names legally repeat.
        Dictionary<string, ArtRef> art = new(StringComparer.Ordinal);
        for (int i = 0; i < map.Nodes.Count; i++)
        {
            WzNode node = map.Nodes[i];
            switch (node.Name)
            {
                case "back":
                    ReadBacks(dto, node, i, inLayer: false, art);
                    break;
                case "foothold":
                    ReadFootholds(dto, node, i);
                    break;
                case "ladderRope":
                    ReadLadders(dto, node, i);
                    break;
                case "portal":
                    ReadPortals(dto, node, i);
                    break;
                case "life":
                    ReadLife(dto, node, i, art);
                    break;
                case "reactor":
                    ReadReactors(dto, node, i);
                    break;
                case "miniMap":
                    dto.MiniMap = new MapMiniMapDto
                    {
                        Width = node.IntegerAt("width"),
                        Height = node.IntegerAt("height"),
                        CenterX = node.IntegerAt("centerX"),
                        CenterY = node.IntegerAt("centerY"),
                        CanvasPath = node.Child("canvas") != null
                            ? WzPath.Child(WzPath.Child(doc.Path, "miniMap"), "canvas")
                            : null,
                    };
                    break;
                default:
                    if (MapNodeKinds.IsLayerName(node.Name)
                        && int.TryParse(node.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int layerIndex))
                        ReadLayer(dto, node, i, layerIndex, art);
                    else if (IsRectKind(node.Name))
                        ReadRects(dto, node, i);
                    break;
            }
        }

        // Bounds: VR when the map has one; computed from geometry for the 1,151
        // that do not. Three bounding rects exist (VR/MR/LB) and only VR is the
        // camera bound — the others are never used here.
        MapRect? vr = map.Info?.ViewBound;
        if (vr is MapRect rect)
        {
            dto.Bounds = new MapBoundsDto { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        }
        else
        {
            dto.Bounds = ComputedBounds(dto);
            dto.BoundsComputed = dto.Bounds != null;
        }

        ResolveArt(dto, art);
        return dto;
    }

    private void ReadLayer(
        MapDocDto dto, WzNode layer, int index, int layerNumber,
        Dictionary<string, ArtRef> art)
    {
        MapLayerDto layerDto = new()
        {
            Index = layerNumber,
            Addr = new[] { index },
            TS = layer.Descend("info/tS")?.AsText(),
            TSMag = layer.Descend("info/tSMag")?.AsInteger(),
        };

        for (int c = 0; c < layer.Children.Count; c++)
        {
            WzNode child = layer.Children[c];
            switch (child.Name)
            {
                case "tile":
                    for (int t = 0; t < child.Children.Count; t++)
                    {
                        WzNode tile = child.Children[t];
                        long tileZ = long.TryParse(tile.Name, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long parsedTileZ)
                            ? parsedTileZ
                            : t;
                        string? u = tile.TextAt("u");
                        string? key = layerDto.TS != null && u != null
                            ? Register(art, $"t|{layerDto.TS}|{u}|{tile.IntegerAt("no") ?? 0}",
                                new ArtRef { Kind = 't', Set = layerDto.TS, U = u, No = tile.IntegerAt("no") ?? 0 })
                            : null;
                        layerDto.Tiles.Add(new MapTileDto
                        {
                            Addr = new[] { index, c, t },
                            Z = tileZ,
                            X = tile.IntegerAt("x") ?? 0,
                            Y = tile.IntegerAt("y") ?? 0,
                            U = u,
                            No = tile.IntegerAt("no") ?? 0,
                            ZM = tile.IntegerAt("zM") ?? 0,
                            Art = key,
                        });
                    }
                    break;

                case "obj":
                    for (int o = 0; o < child.Children.Count; o++)
                    {
                        WzNode obj = child.Children[o];
                        string? oS = obj.TextAt("oS");
                        string? l0 = obj.TextAt("l0");
                        string? l1 = obj.TextAt("l1");
                        string? l2 = obj.TextAt("l2");
                        string? l3 = obj.TextAt("l3");
                        string? key = oS != null
                            ? Register(art, $"o|{oS}|{l0}|{l1}|{l2}|{l3}",
                                new ArtRef { Kind = 'o', Set = oS, L0 = l0, L1 = l1, L2 = l2, L3 = l3 })
                            : null;
                        layerDto.Objs.Add(new MapObjDto
                        {
                            Addr = new[] { index, c, o },
                            OS = oS,
                            L0 = l0,
                            L1 = l1,
                            L2 = l2,
                            L3 = l3,
                            X = obj.IntegerAt("x") ?? 0,
                            Y = obj.IntegerAt("y") ?? 0,
                            Z = obj.IntegerAt("z") ?? 0,
                            ZM = obj.IntegerAt("zM") ?? 0,
                            F = obj.IntegerAt("f") ?? 0,
                            Spine = !string.IsNullOrEmpty(obj.TextAt("spineAni")),
                            Art = key,
                        });
                    }
                    break;

                case "back":
                    // Anomaly 3: a background list inside a layer, 26 entries on
                    // 954090400.img. Rendered with the rest, marked in-layer.
                    layerDto.LayerBackCount = child.Children.Count;
                    ReadBacksFrom(dto, child, new[] { index, c }, inLayer: true, art);
                    break;
            }
        }
        dto.Layers.Add(layerDto);
    }

    private void ReadBacks(
        MapDocDto dto, WzNode container, int index, bool inLayer,
        Dictionary<string, ArtRef> art)
        => ReadBacksFrom(dto, container, new[] { index }, inLayer, art);

    private void ReadBacksFrom(
        MapDocDto dto, WzNode container, int[] baseAddr, bool inLayer,
        Dictionary<string, ArtRef> art)
    {
        for (int b = 0; b < container.Children.Count; b++)
        {
            WzNode back = container.Children[b];
            string? bS = back.TextAt("bS");
            long ani = back.IntegerAt("ani") ?? 0;
            long no = back.IntegerAt("no") ?? 0;
            string? key = !string.IsNullOrEmpty(bS)
                ? Register(art, $"b|{bS}|{ani}|{no}",
                    new ArtRef { Kind = 'b', Set = bS, Ani = ani, No = no })
                : null;

            int[] addr = new int[baseAddr.Length + 1];
            baseAddr.CopyTo(addr, 0);
            addr[^1] = b;

            dto.Backs.Add(new MapBackDto
            {
                Addr = addr,
                BS = bS,
                No = no,
                Ani = ani,
                X = back.IntegerAt("x") ?? 0,
                Y = back.IntegerAt("y") ?? 0,
                Cx = back.IntegerAt("cx") ?? 0,
                Cy = back.IntegerAt("cy") ?? 0,
                Rx = back.IntegerAt("rx") ?? 0,
                Ry = back.IntegerAt("ry") ?? 0,
                Type = back.IntegerAt("type") ?? 0,
                Front = back.IntegerAt("front") ?? 0,
                F = back.IntegerAt("f") ?? 0,
                A = back.IntegerAt("a") ?? 255,
                InLayer = inLayer,
                Spine = ani == 2 || !string.IsNullOrEmpty(back.TextAt("spineAni")),
                Art = key,
            });
        }
    }

    private static void ReadFootholds(MapDocDto dto, WzNode root, int index)
    {
        for (int l = 0; l < root.Children.Count; l++)
        {
            WzNode layer = root.Children[l];
            for (int g = 0; g < layer.Children.Count; g++)
            {
                WzNode group = layer.Children[g];
                for (int f = 0; f < group.Children.Count; f++)
                {
                    WzNode fh = group.Children[f];
                    dto.Footholds.Add(new MapFootholdDto
                    {
                        Addr = new[] { index, l, g, f },
                        Id = fh.Name,
                        Layer = layer.Name,
                        Group = group.Name,
                        X1 = fh.IntegerAt("x1") ?? 0,
                        Y1 = fh.IntegerAt("y1") ?? 0,
                        X2 = fh.IntegerAt("x2") ?? 0,
                        Y2 = fh.IntegerAt("y2") ?? 0,
                        Prev = fh.IntegerAt("prev") ?? 0,
                        Next = fh.IntegerAt("next") ?? 0,
                    });
                }
            }
        }
    }

    private static void ReadLadders(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode ladder = root.Children[i];
            dto.Ladders.Add(new MapLadderDto
            {
                Addr = new[] { index, i },
                X = ladder.IntegerAt("x") ?? 0,
                Y1 = ladder.IntegerAt("y1") ?? 0,
                Y2 = ladder.IntegerAt("y2") ?? 0,
                L = ladder.IntegerAt("l") ?? 0,
            });
        }
    }

    private static void ReadPortals(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode portal = root.Children[i];
            dto.Portals.Add(new MapPortalDto
            {
                Addr = new[] { index, i },
                Pn = portal.TextAt("pn"),
                Pt = portal.IntegerAt("pt") ?? 0,
                X = portal.IntegerAt("x") ?? 0,
                Y = portal.IntegerAt("y") ?? 0,
                Tm = portal.IntegerAt("tm") ?? 0,
                Tn = portal.TextAt("tn"),
                Script = portal.TextAt("script"),
            });
        }
    }

    private void ReadLife(
        MapDocDto dto, WzNode root, int index,
        Dictionary<string, ArtRef> art)
    {
        // Both shapes: the ordinary life/<i> and the life/<categoryId>/<i> the
        // isCategory flag switches on — 25 maps, 2,516 spawns an editor that
        // only reads the first shape writes back empty.
        bool categorised = root.Child("isCategory")?.AsInteger() == 1;

        for (int c = 0; c < root.Children.Count; c++)
        {
            WzNode child = root.Children[c];
            if (string.Equals(child.Name, "isCategory", StringComparison.Ordinal))
                continue;

            if (categorised)
            {
                for (int e = 0; e < child.Children.Count; e++)
                    dto.Life.Add(LifeDto(
                        child.Children[e], new[] { index, c, e }, child.Name, art));
            }
            else
            {
                dto.Life.Add(LifeDto(child, new[] { index, c }, null, art));
            }
        }
    }

    private MapLifeDto LifeDto(
        WzNode life, int[] addr, string? category,
        Dictionary<string, ArtRef> art)
    {
        string? lifeId = life.TextAt("id");
        string? type = life.TextAt("type");
        string? name = null;
        string? artKey = null;
        if (lifeId != null && int.TryParse(lifeId, out int numericId))
        {
            name = string.Equals(type, "n", StringComparison.OrdinalIgnoreCase)
                ? _strings.GetNpcName(numericId)
                : _strings.GetMobName(numericId);

            if (string.Equals(type, "n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "m", StringComparison.OrdinalIgnoreCase))
            {
                string normalizedType = type.ToLowerInvariant();
                artKey = Register(art, $"l|{normalizedType}|{numericId}", new ArtRef
                {
                    Kind = 'l',
                    Set = numericId.ToString(CultureInfo.InvariantCulture),
                    U = normalizedType,
                });
            }
        }
        return new MapLifeDto
        {
            Addr = addr,
            Id = lifeId,
            Type = type,
            X = life.IntegerAt("x") ?? 0,
            Y = life.IntegerAt("y") ?? 0,
            Cy = life.IntegerAt("cy") ?? 0,
            Fh = life.IntegerAt("fh") ?? 0,
            F = life.IntegerAt("f") ?? 0,
            Hide = life.IntegerAt("hide") ?? 0,
            MobTime = life.IntegerAt("mobTime") ?? 0,
            Category = category,
            Name = name,
            Art = artKey,
        };
    }

    private static void ReadReactors(MapDocDto dto, WzNode root, int index)
    {
        for (int i = 0; i < root.Children.Count; i++)
        {
            WzNode reactor = root.Children[i];
            dto.Reactors.Add(new MapReactorDto
            {
                Addr = new[] { index, i },
                Id = reactor.TextAt("id"),
                X = reactor.IntegerAt("x") ?? 0,
                Y = reactor.IntegerAt("y") ?? 0,
                F = reactor.IntegerAt("f") ?? 0,
                ReactorName = reactor.TextAt("name"),
            });
        }
    }

    private static MapBoundsDto? ComputedBounds(MapDocDto dto)
    {
        long left = long.MaxValue, top = long.MaxValue, right = long.MinValue, bottom = long.MinValue;
        void Point(long x, long y)
        {
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }

        foreach (MapFootholdDto fh in dto.Footholds) { Point(fh.X1, fh.Y1); Point(fh.X2, fh.Y2); }
        foreach (MapLadderDto l in dto.Ladders) { Point(l.X, l.Y1); Point(l.X, l.Y2); }
        foreach (MapLayerDto layer in dto.Layers)
        {
            foreach (MapTileDto t in layer.Tiles) Point(t.X, t.Y);
            foreach (MapObjDto o in layer.Objs) Point(o.X, o.Y);
        }
        foreach (MapPortalDto p in dto.Portals) Point(p.X, p.Y);
        foreach (MapLifeDto l in dto.Life) Point(l.X, l.Y);

        if (left > right || top > bottom)
            return null;

        // Headroom mirrors what VR typically adds over raw geometry.
        return new MapBoundsDto
        {
            Left = (int)(left - 100),
            Top = (int)(top - 300),
            Right = (int)(right + 100),
            Bottom = (int)(bottom + 100),
        };
    }

    #endregion

    #region Art resolution

    private sealed class ArtRef
    {
        public char Kind;
        public string Set = "";
        public string? U;
        public long No;
        public string? L0, L1, L2, L3;
        public long Ani;
    }

    private static string Register(Dictionary<string, ArtRef> art, string key, ArtRef reference)
    {
        if (!art.ContainsKey(key))
            art[key] = reference;
        return key;
    }

    /// <summary>
    /// Resolves every distinct art reference to a session path plus the origin
    /// and size the renderer needs. A reference that does not resolve is
    /// reported <see cref="MapArtDto.Missing"/> rather than dropped — the
    /// integrity baseline is not zero (30 missing obj sets and 45 missing paths
    /// ship in the client), and a silent gap looks like a renderer bug.
    /// </summary>
    private void ResolveArt(MapDocDto dto, Dictionary<string, ArtRef> art)
    {
        lock (_session.Gate)
        {
            Dictionary<string, (WzObject Image, string Path)?> imageCache = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string key, ArtRef reference) in art)
            {
                MapArtDto resolved = new() { Missing = true };
                try
                {
                    resolved = reference.Kind switch
                    {
                        't' => ResolveTile(reference, imageCache),
                        'o' => ResolveObj(reference, imageCache),
                        'b' => ResolveBack(reference, imageCache),
                        'l' => ResolveLife(reference, imageCache),
                        _ => resolved,
                    };
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Art reference {Key} failed to resolve", key);
                }
                dto.Art[key] = resolved;
            }
        }
    }

    private MapArtDto ResolveTile(ArtRef reference, Dictionary<string, (WzObject, string)?> cache)
    {
        (WzObject image, string path)? set = FindSetImage("Tile", reference.Set, cache);
        if (set == null)
            return new MapArtDto { Missing = true };

        WzImageProperty? node = InSet(set.Value.image, 
            $"{reference.U}/{reference.No.ToString(CultureInfo.InvariantCulture)}");
        return Meta(node, WzPath.Child(WzPath.Child(set.Value.path, reference.U ?? ""),
            reference.No.ToString(CultureInfo.InvariantCulture)));
    }

    private MapArtDto ResolveObj(ArtRef reference, Dictionary<string, (WzObject, string)?> cache)
    {
        (WzObject image, string path)? set = FindSetImage("Obj", reference.Set, cache);
        if (set == null)
            return new MapArtDto { Missing = true };

        List<string> segments = new() { reference.L0 ?? "", reference.L1 ?? "", reference.L2 ?? "" };
        if (!string.IsNullOrEmpty(reference.L3))
            segments.Add(reference.L3);

        WzImageProperty? node = InSet(set.Value.image, string.Join('/', segments));
        string nodePath = segments.Aggregate(set.Value.path, (parent, name) => WzPath.Child(parent, name));

        // The leaf is usually a frame list. Two or more numbered frames make an
        // animation the viewer can play; frame 0 stays the still. A leaf that
        // is itself drawable is used as it stands.
        if (node != null && node.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
        {
            MapArtDto? animated = AnimatedMeta(node, nodePath);
            if (animated != null)
                return animated;

            WzImageProperty? frame = node.WzProperties?.FirstOrDefault(p => p.Name == "0")
                ?? node.WzProperties?.FirstOrDefault();
            if (frame != null)
            {
                nodePath = WzPath.Child(nodePath, frame.Name);
                node = frame;
            }
        }
        return Meta(node, nodePath);
    }

    private MapArtDto ResolveBack(ArtRef reference, Dictionary<string, (WzObject, string)?> cache)
    {
        (WzObject image, string path)? set = FindSetImage("Back", reference.Set, cache);
        if (set == null)
            return new MapArtDto { Missing = true };

        // ani = 0 -> back/<no>, ani = 1 -> ani/<no> (a frame list the viewer
        // plays), ani = 2 -> spine, which this viewer does not animate — the
        // entry is badged and its still (the same pixels phase 2 drew) is
        // drawn, never a fake. Resolution order is phase 2's exactly, with
        // spine/<no> added as a LAST resort so a spine entry that resolves
        // nowhere else still shows something rather than a missing box.
        string branch = reference.Ani != 0 ? "ani" : "back";
        string no = reference.No.ToString(CultureInfo.InvariantCulture);
        WzImageProperty? node = InSet(set.Value.image, $"{branch}/{no}");
        string nodePath = WzPath.Child(WzPath.Child(set.Value.path, branch), no);
        if (node == null && reference.Ani != 0)
        {
            node = InSet(set.Value.image, $"back/{no}");
            nodePath = WzPath.Child(WzPath.Child(set.Value.path, "back"), no);
        }
        if (node == null && reference.Ani == 2)
        {
            node = InSet(set.Value.image, $"spine/{no}");
            nodePath = WzPath.Child(WzPath.Child(set.Value.path, "spine"), no);
        }

        if (node != null && node.PropertyType is not WzPropertyType.Canvas and not WzPropertyType.UOL)
        {
            // Spine subtrees hold texture pages, not frames; playing them as a
            // frame list would be a fake. Only ani = 1 branches animate.
            MapArtDto? animated = reference.Ani == 2 ? null : AnimatedMeta(node, nodePath);
            if (animated != null)
                return animated;

            WzImageProperty? frame = node.WzProperties?.FirstOrDefault(p => p.Name == "0")
                ?? node.WzProperties?.FirstOrDefault();
            if (frame != null)
            {
                nodePath = WzPath.Child(nodePath, frame.Name);
                node = frame;
            }
        }
        return Meta(node, nodePath);
    }

    /// <summary>
    /// Resolves the visible pose for a map life spawn. NPC and mob images are
    /// root images rather than Map/Obj-style set libraries, so they need their
    /// own lookup. Stand is preferred; uncommon actors fall back to their first
    /// drawable top-level state instead of disappearing behind a name label.
    /// </summary>
    private MapArtDto ResolveLife(
        ArtRef reference, Dictionary<string, (WzObject, string)?> cache)
    {
        string family = string.Equals(reference.U, "n", StringComparison.OrdinalIgnoreCase)
            ? "Npc"
            : "Mob";
        (WzObject image, string path)? source = FindLifeImage(family, reference.Set, cache);
        if (source == null)
            return new MapArtDto { Missing = true };

        string[] preferred = family == "Npc"
            ? new[] { "stand", "default", "move", "sit", "speak" }
            : new[] { "stand", "move", "fly", "jump" };

        foreach (string state in preferred)
        {
            WzImageProperty? node = InSet(source.Value.image, state);
            MapArtDto? art = LifeStateMeta(node, WzPath.Child(source.Value.path, state));
            if (art != null)
                return art;
        }

        // Through ChildrenOf rather than WzProperties: an NX life container is a
        // property, and asking it for WzProperties is the same empty answer.
        foreach (WzObject child in ChildrenOf(source.Value.image))
        {
            if (child is not WzImageProperty node)
                continue;
            if (string.Equals(node.Name, "info", StringComparison.OrdinalIgnoreCase))
                continue;
            MapArtDto? art = LifeStateMeta(
                node, WzPath.Child(source.Value.path, node.Name));
            if (art != null)
                return art;
        }

        return new MapArtDto { Missing = true };
    }

    private static MapArtDto? LifeStateMeta(WzImageProperty? node, string path)
    {
        if (node == null)
            return null;
        if (node.PropertyType is WzPropertyType.Canvas or WzPropertyType.UOL)
        {
            MapArtDto direct = Meta(node, path);
            return direct.Missing ? null : direct;
        }

        MapArtDto? animated = AnimatedMeta(node, path);
        if (animated != null)
            return animated;

        WzImageProperty? frame = node.WzProperties?
            .Where(p => int.TryParse(p.Name, NumberStyles.None,
                CultureInfo.InvariantCulture, out _))
            .OrderBy(p => int.Parse(p.Name, CultureInfo.InvariantCulture))
            .FirstOrDefault();
        if (frame == null)
            return null;
        MapArtDto still = Meta(frame, WzPath.Child(path, frame.Name));
        return still.Missing ? null : still;
    }

    private (WzObject Image, string Path)? FindLifeImage(
        string family, string id,
        Dictionary<string, (WzObject, string)?> cache)
    {
        string cacheKey = "life|" + family + "|" + id;
        if (cache.TryGetValue(cacheKey, out (WzObject, string)? cached))
            return cached;

        List<string> names = new() { id + ".img" };
        if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int numericId))
        {
            string padded = numericId.ToString("D7", CultureInfo.InvariantCulture) + ".img";
            if (!names.Contains(padded, StringComparer.OrdinalIgnoreCase))
                names.Add(padded);
        }

        (WzImage, string)? found = null;
        foreach ((OpenFile file, WzDirectory root) in FindRootImageArchives(family))
        {
            WzImage? image = null;
            foreach (string name in names)
            {
                image = root.GetImageByName(name);
                if (image != null)
                    break;
            }
            if (image == null)
                continue;
            WzSessionService.EnsureParsed(image);
            found = (image, WzPath.Child(_session.RoleRootPath(file, family), image.Name));
            break;
        }

        cache[cacheKey] = found;
        return found;
    }

    /// <summary>
    /// Recognises a numbered-frame container and describes it as a playable
    /// animation, composed at the shared origin by
    /// <see cref="AnimationService.PlaceAtSharedOrigin"/> — the single
    /// composition rule this app has; this caller reuses it rather than
    /// restating it. Returns null when the node holds fewer than two frames.
    /// </summary>
    private static MapArtDto? AnimatedMeta(WzImageProperty node, string nodePath)
    {
        if (node.WzProperties == null)
            return null;

        List<(int Number, WzImageProperty Frame)> frames = new();
        foreach (WzImageProperty child in node.WzProperties)
        {
            // A Spine subtree holds numbered TEXTURE PAGES beside its .atlas /
            // .json / .skel children. Playing those pages as frames would fake
            // a rig this viewer cannot run — the entry stays a badged still.
            if (child.Name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)
                || child.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || child.Name.EndsWith(".skel", StringComparison.OrdinalIgnoreCase))
                return null;

            if (int.TryParse(child.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                frames.Add((number, child));
        }
        if (frames.Count < 2)
            return null;
        frames.Sort((a, b) => a.Number.CompareTo(b.Number));

        List<AnimationFrameDto> dtos = new(frames.Count);
        List<string> paths = new(frames.Count);
        int z = 0;
        bool first = true;
        foreach ((int _, WzImageProperty frame) in frames)
        {
            // Meta already knows how to size a linking canvas (most animated
            // frames past 0 are _inlinks); reuse it per frame.
            string framePath = WzPath.Child(nodePath, frame.Name);
            MapArtDto meta = Meta(frame, framePath);
            if (meta.Missing)
                continue;
            if (first) { z = meta.Z; first = false; }
            dtos.Add(new AnimationFrameDto
            {
                Width = meta.W,
                Height = meta.H,
                OriginX = meta.Ox,
                OriginY = meta.Oy,
                Delay = FrameDelay(frame),
            });
            paths.Add(framePath);
        }
        if (dtos.Count < 2)
            return null;

        (int anchorX, int anchorY, int width, int height, int totalMs) =
            AnimationService.PlaceAtSharedOrigin(dtos);

        MapArtDto art = new()
        {
            Path = paths[0],
            W = width,
            H = height,
            Ox = anchorX,
            Oy = anchorY,
            Z = z,
            Missing = false,
            TotalMs = totalMs,
            Frames = new List<MapArtFrameDto>(dtos.Count),
        };
        for (int i = 0; i < dtos.Count; i++)
        {
            art.Frames.Add(new MapArtFrameDto
            {
                Path = paths[i],
                W = dtos[i].Width,
                H = dtos[i].Height,
                Dx = dtos[i].OffsetX,
                Dy = dtos[i].OffsetY,
                Delay = dtos[i].Delay,
            });
        }
        return art;
    }

    /// <summary>Frame duration in ms; missing or zero is the client's own
    /// 100 ms fallback — matching it keeps playback looking like the game.</summary>
    private static int FrameDelay(WzImageProperty frame)
    {
        try
        {
            WzImageProperty? resolved = frame;
            if (resolved is WzUOLProperty uol)
                resolved = uol.LinkValue as WzImageProperty;
            if (resolved is WzCanvasProperty canvas
                && canvas[WzCanvasProperty.AnimationDelayPropertyName] is { } delay)
            {
                int value = delay.GetInt();
                if (value > 0)
                    return value;
            }
        }
        catch { /* no usable delay property */ }
        return 100;
    }

    /// <summary>
    /// Finds <c>&lt;kind&gt;/&lt;set&gt;.img</c> across every open archive that
    /// carries that library, mount order deciding a name clash — the same rule
    /// the client applies. The set name is matched loosely because the client's
    /// own resolution is case-insensitive.
    /// </summary>
    /// <remarks>
    /// The container is a <see cref="WzImage"/> under .wz and a
    /// <see cref="WzImageProperty"/> under NX, so the tuple carries the
    /// common base. Callers that walk into it cast; the ones that only check for
    /// presence do not care which it is.
    /// </remarks>
    private (WzObject Image, string Path)? FindSetImage(
        string kind, string set, Dictionary<string, (WzObject, string)?> cache)
    {
        string cacheKey = kind + "|" + set;
        if (cache.TryGetValue(cacheKey, out (WzObject, string)? cached))
            return cached;

        (WzObject, string)? found = null;
        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            WzDirectory? root = _session.RoleRoot(file, "Map");
            if (root == null)
                continue;
            // EnumerateChildren rather than WzDirectories: an NX archive holds
            // Tile/Obj/Back as IMAGES whose set names are properties inside them,
            // so a directory walk found no library at all and every placement on
            // NX fell through to "the set could not be resolved".
            foreach (WzObject library in ChildrenOf(root))
            {
                if (!string.Equals(library.Name, kind, StringComparison.OrdinalIgnoreCase))
                    continue;
                // WzObject, not WzImageProperty: under NX the archive's Tile node
                // is an IMAGE whose sets are properties inside it, and a guard
                // that only accepted the property shape skipped the very node the
                // library lives in -- which is why placement refused a tile the
                // palette had just listed with a picture.
                if (library is not WzImage and not WzImageProperty)
                    continue;

                string libraryPath = WzPath.Child(_session.RoleRootPath(file, "Map"), library.Name);

                // Under .wz a set is an image; under NX it is a property. Either
                // way the name is the one the caller passed in.
                foreach (WzObject candidate in ChildrenOf(library))
                {
                    if (!string.Equals(candidate.Name, set + ".img", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (candidate is WzImage image)
                    {
                        WzSessionService.EnsureParsed(image);
                        found = (image, WzPath.Child(libraryPath, image.Name));
                    }
                    else if (candidate is WzImageProperty property)
                    {
                        found = (property, WzPath.Child(libraryPath, property.Name));
                    }
                    break;
                }
                if (found != null)
                    break;
            }
            if (found != null)
                break;
        }
        cache[cacheKey] = found;
        return found;
    }

    /// <summary>
    /// Walks into a set container, whichever of the two shapes it is.
    ///
    /// <para>Under .wz a set is a <see cref="WzImage"/>; under NX it is a
    /// <see cref="WzImageProperty"/> inside the archive's <c>Tile</c> image.
    /// Both carry <c>GetFromPath</c>, so callers would otherwise need a cast at
    /// every one of them — and every cast is a place the NX shape can be
    /// forgotten again.</para>
    /// </summary>
    private static WzImageProperty? InSet(WzObject? container, string path)
        => container switch
        {
            WzImage image => image.GetFromPath(path),
            WzImageProperty property => property.GetFromPath(path),
            _ => null,
        };

    /// <summary>
    /// Size, origin and z for a drawable node. A linking canvas (its pixels are
    /// a 1x1 placeholder) borrows the linked target's size but keeps its own
    /// origin when it has one — the link carries the placement, the target the
    /// pixels. The <see cref="MapArtDto.Path"/> stays the original node's path;
    /// /api/canvas follows the link itself when rendering.
    /// </summary>
    private static MapArtDto Meta(WzImageProperty? node, string path)
    {
        WzCanvasProperty? canvas = node as WzCanvasProperty;
        if (canvas == null && node is WzUOLProperty uol && uol.LinkValue is WzCanvasProperty linkedCanvas)
            canvas = linkedCanvas;
        if (canvas == null)
            return new MapArtDto { Missing = true };

        int w = canvas.PngProperty?.Width ?? 0;
        int h = canvas.PngProperty?.Height ?? 0;
        WzVectorProperty? origin = canvas["origin"] as WzVectorProperty;
        int z = (canvas["z"] as WzIntProperty)?.Value ?? 0;

        if (w <= 1 && h <= 1)
        {
            try
            {
                if (canvas.GetLinkedWzImageProperty() is WzCanvasProperty target
                    && !ReferenceEquals(target, canvas))
                {
                    w = target.PngProperty?.Width ?? w;
                    h = target.PngProperty?.Height ?? h;
                    origin ??= target["origin"] as WzVectorProperty;
                }
            }
            catch
            {
                // A dangling link. The size stays 1x1 and the client draws its
                // missing-art marker; the reference itself is preserved.
            }
        }

        return new MapArtDto
        {
            Path = path,
            W = w,
            H = h,
            Ox = origin?.X?.Value ?? 0,
            Oy = origin?.Y?.Value ?? 0,
            Z = z,
            Missing = false,
            // A node can BE a canvas and still carry no pixels: the NX sets hold
            // logic sub-nodes (foothold, portal, life) whose canvas is 1x1 or
            // empty. Meta reports those as present, which is correct for "this
            // node exists" -- but the palette must not show them as usable art,
            // or an agent places them and writes a reference to nothing.
            HasPixels = w > 1 || h > 1,
        };
    }

    /// <summary>
    /// True when <paramref name="node"/> is a canvas a client can actually draw:
    /// it resolves to pixel data of a real size.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Meta"/>'s <c>Missing</c> on purpose. Missing
    /// answers "is this node there"; this answers "can I see it". Measured on
    /// Map.nx: <c>Tile/ancientForest.img</c> holds nine sub-nodes — two foothold,
    /// one life, four portal, one back — all with w=0, h=0. It listed in the
    /// palette with a thumbnail, and placing it returned
    /// "does not resolve in the open session".
    /// </remarks>
    private static bool HasDrawablePixels(WzImageProperty? node)
    {
        WzCanvasProperty? canvas = node as WzCanvasProperty;
        if (canvas == null && node is WzUOLProperty uol && uol.LinkValue is WzCanvasProperty linked)
            canvas = linked;
        if (canvas == null)
            return false;

        int w = canvas.PngProperty?.Width ?? 0;
        int h = canvas.PngProperty?.Height ?? 0;
        if (w > 1 || h > 1)
            return true;

        // A 1x1 canvas is the linking placeholder; the pixels live in the target.
        try
        {
            if (canvas.GetLinkedWzImageProperty() is WzCanvasProperty target
                && !ReferenceEquals(target, canvas))
                return (target.PngProperty?.Width ?? 0) > 1
                    || (target.PngProperty?.Height ?? 0) > 1;
        }
        catch
        {
            // A dangling link has no pixels to offer.
        }

        return false;
    }

    #endregion

    #region Portal icons

    /// <summary>
    /// The portal icon table, read from <c>MapHelper.img/portal/editor</c>
    /// <b>child order</b> at runtime. Measured: <c>pt</c> does not index any
    /// alphabetical or documented order — pt 6 is <c>tp</c> in 78 of 78
    /// placements and <c>tp</c> is the sixth child.
    /// </summary>
    public List<MapPortalIconDto> PortalIcons()
    {
        lock (_session.Gate)
        {
            List<MapPortalIconDto> icons = new();
            (WzImage image, string path)? helper = FindHelperImage();
            if (helper == null)
                return icons;

            WzImageProperty? editor = helper.Value.image.GetFromPath("portal/editor");
            if (editor?.WzProperties == null)
                return icons;

            string editorPath = WzPath.Child(WzPath.Child(helper.Value.path, "portal"), "editor");
            int pt = 0;
            foreach (WzImageProperty child in editor.WzProperties)
            {
                MapArtDto meta = Meta(child, WzPath.Child(editorPath, child.Name));
                icons.Add(new MapPortalIconDto
                {
                    Pt = pt++,
                    Name = child.Name,
                    Path = meta.Missing ? null : meta.Path,
                    W = meta.W,
                    H = meta.H,
                    Ox = meta.Ox,
                    Oy = meta.Oy,
                });
            }
            return icons;
        }
    }

    private (WzImage Image, string Path)? FindHelperImage()
    {
        foreach (OpenFile file in Ordered(_session.SelectRoleSources("Map")))
        {
            WzImage? image = _session.RoleRoot(file, "Map")?.GetImageByName("MapHelper.img");
            if (image != null)
            {
                WzSessionService.EnsureParsed(image);
                return (image, WzPath.Child(_session.RoleRootPath(file, "Map"), image.Name));
            }
        }
        return null;
    }

    #endregion

    #region The document record

    private sealed class EditorDoc
    {
        public EditorDoc(string path, MapDocument doc, byte[] iv, int generation)
        {
            Path = path;
            Doc = doc;
            Iv = iv;
            Generation = generation;
            Touched = DateTime.UtcNow;
        }

        public string Path { get; }
        public MapDocument Doc { get; }
        public byte[] Iv { get; }

        /// <summary>
        /// Null for a document that may be edited. A sentence when it may not:
        /// the model here is a copy of the archive's content rather than a
        /// handle on it, so a write could not reach the file. Served on the DTO
        /// and enforced by <see cref="RequireWritable"/>, so the client can
        /// grey out what it is about to be refused.
        /// </summary>
        public string? ReadOnlyReason { get; set; }

        public int Generation { get; set; }
        public DateTime Touched { get; set; }
        public Stack<Change> Undo { get; } = new();
        public Stack<Change> Redo { get; } = new();
        public bool Dirty { get; set; }

        /// <summary>The undo depth at which the document equals the saved file.
        /// 0 for a fresh document; set to the current depth by a save (history
        /// survives it); -1 once the clean state is no longer reachable —
        /// undoing below a save point and editing a different way orphans it.</summary>
        public int CleanDepth { get; set; }

        /// <summary>Called by a verified save: the file now equals the model at
        /// this exact depth, and the history on either side of it stays usable.</summary>
        public void MarkSaved()
        {
            CleanDepth = Undo.Count;
            Dirty = false;
        }

        public void Push(Change change)
        {
            // An edit below the save point, replacing the redo path that led
            // back to it: the saved state can no longer be reached by depth.
            if (Undo.Count < CleanDepth)
                CleanDepth = -1;
            Undo.Push(change);
            Redo.Clear();
            Dirty = true;
            while (Undo.Count > MaxUndoDepth)
            {
                // Stack has no trim; rebuild without the oldest entry. Rare
                // enough (200 edits without a save) that clarity wins.
                Change[] kept = Undo.ToArray()[..MaxUndoDepth];
                Undo.Clear();
                for (int i = kept.Length - 1; i >= 0; i--)
                    Undo.Push(kept[i]);
                // The trimmed entry was the bottom of the stack; the clean
                // depth is measured from the bottom, so it moves with it.
                CleanDepth = CleanDepth <= 0 ? -1 : CleanDepth - 1;
            }
        }

        public WzNode NodeAt(int[] addr)
        {
            if (addr == null || addr.Length == 0)
                throw new ArgumentException("An empty address names nothing.");
            if (addr[0] < 0 || addr[0] >= Doc.Nodes.Count)
                throw new ArgumentException("The address is outside the document.");

            WzNode node = Doc.Nodes[addr[0]];
            for (int i = 1; i < addr.Length; i++)
            {
                if (addr[i] < 0 || addr[i] >= node.Children.Count)
                    throw new ArgumentException(
                        $"The address is stale — '{node.Name}' has {node.Children.Count} children and " +
                        $"index {addr[i]} was asked for. Re-fetch the document.");
                node = node.Children[addr[i]];
            }
            return node;
        }
    }

    /// <summary>A reversible edit. Built with its revert; the new state is
    /// captured after the caller applies it, so apply/revert replay exactly.</summary>
    private sealed class Change
    {
        private readonly Action _apply;
        private readonly Action _revert;

        public Change(string label, Action apply, Action revert)
        {
            Label = label;
            _apply = apply;
            _revert = revert;
        }

        public string Label { get; }
        public Action? OnCaptureNew { get; init; }

        public void CaptureNew() => OnCaptureNew?.Invoke();
        public void Apply() => _apply();
        public void Revert() => _revert();

        public static Change Group(string label, params Change[] parts) => new(
            label,
            apply: () => { foreach (Change part in parts) part.Apply(); },
            revert: () =>
            {
                for (int i = parts.Length - 1; i >= 0; i--)
                    parts[i].Revert();
            });
    }

    #endregion
}
