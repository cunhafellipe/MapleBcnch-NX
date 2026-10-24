using System.Globalization;
using MapleBench.Models;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.MapEditor;

/// <summary>One animation a mob can play, named the way the client names it.</summary>
public sealed class MobActionDto
{
    /// <summary>
    /// The node name in the Mob archive — <c>stand</c>, <c>move</c>, <c>jump</c>,
    /// <c>hit1</c>, <c>die1</c>, <c>fly</c>, <c>attack1</c>.
    /// </summary>
    /// <remarks>
    /// One name is not a node name: <c>attackN/ball</c> is the projectile's own
    /// art, which lives at <c>attackN/info/ball</c> rather than beside the
    /// actions (<c>Mob.cpp:163-165</c>). It is spelled with a slash so it cannot
    /// collide with an archive node name, and it travels through the same list so
    /// there is one place that knows what art a mob has.
    /// </remarks>
    public string Name { get; set; } = "";

    /// <summary>Session path of the action node, for /api/animation and /api/canvas.</summary>
    public string Path { get; set; } = "";

    public List<AnimationFrameDto> Frames { get; set; } = new();
}

/// <summary>
/// Which actions a mob carries, and where its art lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than a caller's own /api/animation.</b> The
/// browser needs three things at once — every action's frames in one response,
/// the mob's image path, and whether <c>info/link</c> moved the art somewhere
/// else — and asking for them one at a time is a round trip per action on a map
/// with 29 mobs. It also cannot guess the ACTION NAMES: they are the reference's
/// (<c>Mob.cpp:114-133</c>), and a viewer that invented "walk1" would look for
/// art the Mob archive does not carry.
/// </para>
/// <para>
/// <b>info/link is resolved here, not left to the caller.</b> A mob whose
/// <c>info/link</c> points elsewhere carries its own stats and no art of its
/// own (<c>Mob.cpp:43-57</c>). Reading the animations off the original node
/// leaves those mobs alive and moving with nothing ever drawn, which is exactly
/// the failure this file is for.
/// </para>
/// <para>
/// <b>Where the art lives in the archive.</b> <c>Mob.nx/&lt;7-digit id&gt;.img</c>
/// — the id zero-padded to seven characters, the same
/// <c>string_format::extend_id(mid, 7)</c> the reference uses
/// (<c>Mob.cpp:38</c>). Both the NX shape (images at the archive root) and the
/// WZ shape (<c>Character/</c>-style folder) are probed, because which one a
/// given archive uses is a property of the archive and not of this code.
/// </para>
/// </remarks>
public sealed class MobSpriteService
{
    private readonly WzSessionService _session;
    private readonly AnimationService _animation;

    public MobSpriteService(WzSessionService session, AnimationService animation)
    {
        _session = session;
        _animation = animation;
    }

    /// <summary>Every mob action the archive holds, for one mob id.</summary>
    /// <remarks>
    /// Returns an empty list — not an error — for a mob with no art. "This map's
    /// spawns reference a mob nothing in the open archive draws" is a finding
    /// the caller has to be able to see, not a 404 it has to guess at.
    /// </remarks>
    /// <param name="fileId">One open archive, or null for every open one.</param>
    /// <param name="mobId">The id a <c>life</c> entry names.</param>
    /// <param name="kind">
    /// <c>"m"</c> (the default) or <c>"n"</c> — the value <c>life/&lt;i&gt;/type</c>
    /// stores, and the only thing that says which archive holds the id. A map's
    /// NPC spawns and mob spawns carry ids from two different archives under one
    /// key, so a reader that guessed the archive would resolve every mob and no
    /// NPC, or the reverse.
    /// </param>
    public MobSpriteDto Describe(string? fileId, string mobId, string? kind = null)
        => Describe(fileId, mobId, IsNpc(kind) ? MobKind.Npc : MobKind.Mob);

    private static bool IsNpc(string? kind)
        => string.Equals(kind, "n", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which archive an id is looked up in.
    /// </summary>
    /// <remarks>
    /// The lifecycle states differ too, and they are not the mob's: the client's
    /// <c>Npc.cs</c> has stand/sit/speak/move/happy/bye, while <c>Mob.cpp</c>
    /// names stand/move/jump/fly/hit1/die1/attack1. Both are read the same way —
    /// off whatever actions the node actually carries — so the enum decides only
    /// which archive to open.
    /// </remarks>
    private enum MobKind
    {
        Mob,
        Npc,
    }

    private MobSpriteDto Describe(string? fileId, string mobId, MobKind kind)
    {
        MobSpriteDto dto = new() { MobId = mobId, Kind = kind == MobKind.Npc ? "n" : "m" };

        lock (_session.Gate)
        {
            if (!TryMobImage(fileId, mobId, kind, out WzImage? image, out string imagePath))
                return dto;

            dto.ImagePath = imagePath;

            // info/link first: when it is present the art belongs to the OTHER mob.
            string? link = ReadText(image, "info", "link");
            if (link is not null && int.TryParse(link, out int linked))
            {
                dto.LinkedFrom = mobId;
                if (TryMobImage(fileId, linked.ToString(), kind, out WzImage? target, out string targetPath))
                {
                    dto.MobId = linked.ToString();
                    dto.ImagePath = targetPath;
                    image = target;
                    // The PATH moves with the NODE.
                    //
                    // MEASURED 2026-10-24: `f5/9500135.img` carries only `info`,
                    // and `info/link` = "7130010" -- whose image `f5/7130010.img`
                    // carries `attack1`, `die1`, `hit1` and `stand`. Following the
                    // link swapped the NODE but not the PATH: the action list was
                    // enumerated off the linked image while every action's path
                    // was built from the ORIGINAL image's path, so
                    // `AnimationService.Describe` was handed
                    // "f5/9500135.img/attack1" -- a path that does not exist --
                    // and threw. The endpoint answered 404 "'attack1' not found
                    // under 'f5/9500135.img'" for a mob whose art it had just
                    // found, and the contract it documents ("an empty list, not
                    // an error") was broken for EVERY mob that carries a link.
                    // The same stale path fed the ball loop below, so a linked
                    // mob's projectile art was never published either.
                    imagePath = targetPath;
                }
            }

            // The action list is read from the NODE, not from a fixed set, because a
            // mob with an attack carries attack1..attack8 and one without carries
            // none. Mob.cpp:143-146 walks "attack" + (index+1) for exactly this
            // reason.
            //
            // EnumerateChildren, not image.WzProperties: under NX these are
            // properties rather than a collection, so the latter reported no
            // actions at all for every mob in Mob.nx. The occurrence counter is
            // what keeps a repeated name addressable, exactly as
            // WzPath.Child(path, name, occurrence) expects.
            int occurrence = 0;
            Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (WzObject node in _session.EnumerateChildren(image!))
            {
                string name = node.Name ?? "";
                seen.TryGetValue(name, out occurrence);
                seen[name] = occurrence + 1;

                if (string.Equals(name, "info", StringComparison.OrdinalIgnoreCase))
                    continue;

                WzImageProperty? action = node as WzImageProperty;
                if (action is null)
                    continue;

                // The art node, or a null child when the action is absent. Probing
                // the node rather than the container means an action that exists as
                // an empty container does not claim to be drawable.
                if (!HasNumericCanvasChild(action))
                    continue;

                dto.Actions.Add(new MobActionDto
                {
                    Name = name,
                    Path = WzPath.Child(imagePath, name, occurrence),
                });
            }

            foreach (MobActionDto action in dto.Actions)
            {
                // Reuse the one animation reader. A second implementation of "what
                // are this action's frames" is a second thing that can disagree
                // about a frame's origin or delay, and the origin is what decides
                // whether a mob's feet touch the ground.
                AnimationDto described = _animation.Describe(action.Path);
                action.Frames = described.Frames;
                dto.AnchorX = Math.Max(dto.AnchorX, described.AnchorX);
                dto.AnchorY = Math.Max(dto.AnchorY, described.AnchorY);
            }

            // THE PROJECTILE'S OWN ART, Mob.cpp:163-165 --
            //
            //     attack.has_ball = bool(data["ball"]["0"]);
            //     attack.ball     = data["ball"];
            //
            // `ball` is not a sibling of the actions: it hangs off the ATTACK's
            // info, at `attackN/info/ball`, so the loop above never sees it. It is
            // added here under the name `attackN/ball` so the same animation
            // reader and the same client sprite cache can carry it, rather than
            // being given a second path-shaped thing that only one caller knows.
            for (int n = 1; n <= 8; n++)
            {
                string attackName = "attack" + n.ToString(CultureInfo.InvariantCulture);
                if (_session.TryResolve(WzPath.Child(imagePath, attackName)) is not WzObject attack)
                    continue;
                WzObject? ball = FindChild(attack, "info") is { } info
                    ? FindChild(info, "ball")
                    : null;
                if (ball is null)
                    continue;

                MobActionDto dtoBall = new()
                {
                    Name = attackName + "/ball",
                    // `WzPath.Child` per level, NOT `WzPath.Join(imagePath, ...)`:
                    // `Join` escapes every segment it is given, and `imagePath`
                    // already contains its own slashes -- so Join turned
                    // `f5/4220000.img` into `f5%2F4220000.img` and every ball
                    // 404'd on a file id that does not exist. MEASURED: that was the
                    // whole reason no ball was published.
                    Path = WzPath.Child(
                        WzPath.Child(WzPath.Child(imagePath, attackName), "info"), "ball"),
                };
                AnimationDto described;
                try
                {
                    described = _animation.Describe(dtoBall.Path);
                }
                catch (Exception)
                {
                    // `AnimationService.Describe` resolves by PATH while the walk
                    // above went by NODE, and the two can disagree about a name
                    // that needs an occurrence suffix or a different casing. The
                    // projectile art is optional (`MobAttack.cpp:164`,
                    // `has_ball`), so a ball that will not describe is a ball that
                    // is not published -- not a reason to 404 the whole sprite.
                    continue;
                }
                // `Describe` IS the test that this ball has art, and it is the same
                // reader the client's frames come from. `HasNumericCanvasChild`
                // was tried here first and answered false for a node
                // `Describe` reads as two 32x32 canvases -- a second opinion about
                // the same question, and it was the one that was wrong.
                dtoBall.Frames = described.Frames;
                if (dtoBall.Frames.Count == 0)
                    continue;
                dto.Actions.Add(dtoBall);
            }
        }

        return dto;
    }

    /// <summary>
    /// A named child of a node, or null. Case-insensitive, like every other reader
    /// of these archives.
    /// </summary>
    private WzObject? FindChild(WzObject node, string name)
    {
        try
        {
            foreach (WzObject child in _session.EnumerateChildren(node))
            {
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
        }
        catch (Exception)
        {
            // A node that cannot be enumerated is a node that does not hold the
            // child being asked for. Swallowing it here is the same answer
            // HasNumericCanvasChild gives, and a throw here would take down the
            // whole sprite description for one bad node.
        }
        return null;
    }

    /// <summary>Whether a container holds a numerically-named canvas child.</summary>
    /// <remarks>
    /// <para>
    /// A mob action's frames are CANVASES with <c>origin</c>/<c>head</c>/<c>delay</c>
    /// children (measured on Mob.nx, not assumed), unlike a character action's
    /// frames which are containers of canvases. Testing for a numeric child rather
    /// than for either shape is what keeps this correct on both archives at once.
    /// </para>
    /// <para>
    /// <b>The probe goes through <c>EnumerateChildren</c>, not
    /// <c>WzProperties</c>.</b> That is not a style choice: under NX the numbered
    /// children are properties rather than a collection, so <c>WzProperties</c>
    /// returns null or empty and every mob in Mob.nx reported <c>hasArt: false</c>
    /// — the images were open, the actions were listed, and not one frame was
    /// ever found. <c>AnimationService.Describe</c> reads frames through the same
    /// call for the same reason, and a second reader here would be a second thing
    /// that could disagree with it.
    /// </para>
    /// </remarks>
    private bool HasNumericCanvasChild(WzObject action)
    {
        List<WzObject>? children;
        try
        {
            children = _session.EnumerateChildren(action).ToList();
        }
        catch
        {
            // A branch this sweep cannot read is a branch it did not find art in.
            // Reporting "no art" is the honest answer; throwing would take the whole
            // map's mob list down for one unreadable action.
            return false;
        }

        foreach (WzObject child in children)
        {
            if (!int.TryParse(child.Name, out _))
                continue;

            WzImageProperty? resolved = child as WzImageProperty;
            if (resolved is WzUOLProperty uol)
            {
                try { resolved = uol.LinkValue as WzImageProperty; }
                catch { continue; }
            }

            if (resolved is WzCanvasProperty)
                return true;

            // NX keeps a frame container where WZ has a bare canvas. The action
            // still HAS drawable art, and AnimationService is what turns those
            // layers into frames, so it is this method's job to say yes.
            if (resolved is not null && resolved.WzProperties is not null)
            {
                foreach (WzObject layer in _session.EnumerateChildren(resolved))
                    if (layer is WzCanvasProperty or WzUOLProperty)
                        return true;
            }
        }
        return false;
    }

    /// <summary>The mob's image for an id, or null when no open archive holds it.</summary>
    private bool TryMobImage(string? fileId, string mobId, MobKind kind, out WzImage? image, out string path)
    {
        image = null;
        path = "";
        if (!int.TryParse(mobId, out int numeric))
            return false;

        string role = kind == MobKind.Npc ? "Npc" : "Mob";

        // extend_id(mid, 7), Mob.cpp:38 — and the UNPADDED form, because a WZ
        // archive written by an older tool names its images either way and guessing
        // one of the two would report "no art" for a mob that has it. Both the NX
        // shape (images at the archive root) and the WZ shape (a Mob/ folder) are
        // probed for the same reason.
        foreach (string candidate in new[] { numeric.ToString("D7"), numeric.ToString() })
        {
            foreach (OpenFile file in Candidates(fileId, role))
            {
                foreach (string prefix in new[] { "", role + "/" })
                {
                    string child = WzPath.Join(file.Id, prefix + candidate + ".img");
                    if (_session.TryResolve(child) is WzImage found)
                    {
                        image = found;
                        path = child;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>Archives that could hold mobs, optionally narrowed to one.</summary>
    private List<OpenFile> Candidates(string? fileId, string role)
        => fileId is null
            ? _session.SelectRoleSources(role, null)
            : _session.SelectRoleSources(role, fileId);

    private string? ReadText(WzImage image, string group, string key)
    {
        // EnumerateChildren, for the same NX reason as the action list above: an
        // NX node's children are properties, and WzProperties is the empty answer
        // for them. Reading info/link this way is what makes a linked mob draw as
        // the mob it links to instead of as nothing at all.
        WzObject? parent = _session.EnumerateChildren(image)
            .FirstOrDefault(p => string.Equals(p.Name, group, StringComparison.OrdinalIgnoreCase));
        if (parent is null)
            return null;

        WzObject? leaf = _session.EnumerateChildren(parent)
            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

        return leaf switch
        {
            WzStringProperty s => s.Value,
            WzIntProperty i => i.Value.ToString(),
            WzShortProperty s => s.Value.ToString(),
            WzLongProperty l => l.Value.ToString(),
            _ => null,
        };
    }
}

/// <summary>Every action one mob carries, plus where its art came from.</summary>
public sealed class MobSpriteDto
{
    /// <summary>
    /// The id whose art was read, which is NOT the id asked for when the mob's
    /// <c>info/link</c> moved it. Both are reported so a reader can say "this is
    /// 9500102, which draws as 9500100" rather than claiming the first has art.
    /// </summary>
    public string MobId { get; set; } = "";

    /// <summary>The id that asked, when <c>info/link</c> was followed.</summary>
    public string LinkedFrom { get; set; } = "";

    /// <summary>
    /// <c>m</c> or <c>n</c> — which family this id was read out of.
    /// </summary>
    /// <remarks>
    /// Sent because "no art" and "looked in the wrong archive" produce the same
    /// empty answer. A map's NPCs and its mobs name ids from two archives, so a
    /// caller drawing a placeholder has to say which archive was consulted.
    /// </remarks>
    public string Kind { get; set; } = "m";

    /// <summary>Session path of the image the actions hang off.</summary>
    public string ImagePath { get; set; } = "";

    public List<MobActionDto> Actions { get; set; } = new();

    /// <summary>The largest origin across every action — the shared anchor.</summary>
    public int AnchorX { get; set; }

    public int AnchorY { get; set; }

    /// <summary>True when at least one action carried real frames.</summary>
    public bool HasArt => Actions.Any(a => a.Frames.Count > 0);
}