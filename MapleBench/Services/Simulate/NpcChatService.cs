using System.Globalization;
using MapleBench.Models;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services.Simulate;

/// <summary>
/// What one NPC says, and the balloon it says it in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reference's own chain, which is a two-hop lookup and not one.</b>
/// </para>
/// <list type="bullet">
/// <item><c>Npc.cpp:127-128</c> — for each node of <c>Npc.nx/&lt;id&gt;.img</c>,
/// the children of that node's <c>speak</c> are STRING KEYS, not the lines:
/// <c>lines[state].push_back(strsrc[speaknode.get_string()]);</c></item>
/// <item><c>Npc.cpp:60</c> — <c>strsrc</c> is
/// <c>String.nx["Npc.img"][id]</c>, so the key resolves there.</item>
/// <item><c>Npc.cpp:457-469</c> — <c>speak()</c> prefers <c>lines["info"]</c> and
/// falls back to <c>lines[stance]</c>: the general chatter wins unless the NPC
/// carries none.</item>
/// <item><c>Npc.cpp:478-484</c> — <c>pool = states.size() + lines.size()</c>,
/// one <c>random.next_int(pool)</c>, and it speaks only when
/// <c>pick &gt;= states.size()</c>. An NPC with things to do therefore says less,
/// which is the behaviour and not an accident.</item>
/// <item><c>Npc.cpp:203</c> + <c>Npc.h:131-132</c> — the first line comes at
/// <c>3000 + rand(6000)</c> ms, NOT on frame 1, so a row of NPCs does not talk
/// in unison and a quest giver does not speak before its marker is worked out
/// 60 ticks in.</item>
/// <item><c>Npc.cpp:276-284</c> — a line goes out only when
/// <c>chat_delay &lt;= 0</c>, no balloon is up, and no quest marker is showing.</item>
/// <item><c>Npc.h:133-135</c> — the reference's balloon stays 5 seconds and
/// then fades over another, and the <c>CHAT_DURATION = 3000</c> on the line
/// below is THIS PORT's deliberately shortened value, not the reference's.</item>
/// <item><c>ChatBalloon.cpp:61-82</c> + <c>Npc.h:127</c> — the balloon is
/// <c>UI.nx["ChatBalloon.img"]["npc"]</c>: a 9-slice (<c>n ne e se s sw w nw c</c>),
/// an <c>arrow</c>, and a <c>clr</c> the text is tinted with.</item>
/// </list>
///
/// <para>
/// <b>MEASURED on this archive, 2026-10-16.</b> <c>String.nx/Npc.img</c> holds
/// 7,123 entries. Over the first 60: <c>name</c> on 60, <c>n0</c> 42,
/// <c>n1</c> 38, <c>d0</c> 28, <c>d1</c> 28, <c>func</c> 26, <c>s0</c> 11,
/// <c>s1</c> 10, <c>n2</c> 9. So <c>n*</c> is not an edge case — two thirds of
/// NPCs carry idle chatter. The per-action keys are named after the action:
/// <c>9101002</c> has <c>info/speak = [n0 n1 n2 n3]</c>,
/// <c>finger/speak = [f0]</c>, <c>wink/speak = [w0]</c>,
/// <c>heart/speak = [h0]</c>, and <c>stand</c> carries no <c>speak</c> at all.
/// <c>ChatBalloon.img/npc</c> is present with 11 children and
/// <c>clr = -8388608</c> = <c>0xFF800000</c>, the maroon the reference's comment
/// names for NPCs.
/// </para>
/// <para>
/// <b>A missing key is not an empty line.</b> An NPC whose action names a string
/// key the String archive does not carry produces a balloon with nothing in it;
/// those lines are dropped here rather than drawn blank.
/// </para>
/// </remarks>
public sealed class NpcChatService
{
    /// <summary>The reference's first-line delay: Npc.h:131-132.</summary>
    public const int ChatMinDelayMs = 3000;

    /// <summary>The reference's delay spread: Npc.h:132.</summary>
    public const int ChatDelaySpreadMs = 6000;

    /// <summary>How long a balloon stays up: this port's own 3000 ms, which the
    /// source calls "deliberately shorter than the original" (Npc.h:133-135).</summary>
    public const int ChatDurationMs = 3000;

    /// <summary>The balloon every NPC uses: Npc.h:127.</summary>
    public const string BalloonType = "npc";

    private static readonly string[] FrameEdges = { "nw", "n", "ne", "w", "c", "e", "sw", "s", "se" };

    private readonly WzSessionService _session;

    public NpcChatService(WzSessionService session) => _session = session;

    /// <summary>Every line one NPC can say, and the balloon to say it in.</summary>
    /// <param name="npcId">A numeric NPC id, padded or not.</param>
    public NpcChatDto Describe(string? npcId)
    {
        var dto = new NpcChatDto { NpcId = (npcId ?? "").Trim() };
        if (dto.NpcId.Length == 0) return dto;
        if (!int.TryParse(dto.NpcId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return dto;

        lock (_session.Gate)
        {
            if (!TryNpcImage(numeric, out WzObject? image, out string imagePath)) return dto;
            dto.Found = true;
            dto.ImagePath = imagePath;

            foreach (WzObject node in Children(image))
            {
                string state = node.Name;

                // Npc.cpp:123 — the random pool is `special == 0`, so an NPC that marks
                // an action keeps it OUT of the pool (the reference's own example
                // is 9010022's stand_old, special=1; measured present in Npc.nx).
                // Hence `!= 0`, NOT `== 1`, and the `?? 0` is not decoration:
                // ReadInt returns `int?` and yields null for a node with no
                // `special` child, and in C# `null != 0` is TRUE -- so writing
                // `!= 0` straight onto ReadInt's result marks every UNMARKED
                // node special and empties the pool (measured: `states=[]` for
                // all six npcchat-verify cases, against the reference's pool of
                // the non-info actions). The reference's `operator[]` creates a
                // zero-valued property for a missing `special`, so missing == 0
                // == in the pool.
                bool special = (ReadInt(node, "special") ?? 0) != 0;
                if (state != "info") dto.Actions.Add(state);
                if (!special && state != "info") dto.States.Add(state);

                foreach (string key in SpeakKeys(node))
                {
                    string? text = ReadNpcString(numeric, key);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (state == "info") dto.General.Add(text);
                    else dto.ByAction[state] = dto.ByAction.TryGetValue(state, out var list)
                        ? [.. list, text]
                        : [text];
                }
            }

            dto.Balloon = ReadBalloon();
        }
        return dto;
    }

    /// <summary>
    /// The 9-slice, arrow and colour of <c>UI.nx/ChatBalloon.img/npc</c>.
    /// </summary>
    private NpcBalloonDto ReadBalloon()
    {
        var balloon = new NpcBalloonDto();
        foreach (OpenFile file in _session.SelectRoleSources("UI", null))
        {
            if (_session.TryResolve(WzPath.Join(file.Id, "ChatBalloon.img", BalloonType)) is not { } node)
                continue;

            foreach (WzObject child in Children(node))
            {
                switch (child.Name)
                {
                    case "arrow": balloon.Arrow = $"{file.Id}/ChatBalloon.img/{BalloonType}/arrow"; break;
                    case "clr":
                        // ChatBalloon.cpp:71-77 reads it as ARGB and keeps the
                        // low three bytes. -8388608 is 0xFF800000, the maroon the
                        // reference's own comment names for NPCs.
                        balloon.TextColor = ReadInt(node, "clr") ?? 0x00800000;
                        break;
                    default:
                        if (FrameEdges.Contains(child.Name))
                            balloon.Edges[child.Name] = $"{file.Id}/ChatBalloon.img/{BalloonType}/{child.Name}";
                        break;
                }
            }
            balloon.Found = balloon.Edges.Count > 0;
            return balloon;
        }
        return balloon;
    }

    /// <summary>
    /// The string keys a node's <c>speak</c> names — <c>Npc.cpp:127-128</c>.
    /// </summary>
    private static IEnumerable<string> SpeakKeys(WzObject node)
    {
        foreach (WzObject action in WzChildren(node))
        {
            if (!action.Name.Equals("speak", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (WzObject key in WzChildren(action))
            {
                string? value = key switch
                {
                    WzStringProperty s => s.Value,
                    WzIntProperty i => i.Value.ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
            }
        }
    }

    /// <summary>One string off <c>String.nx/Npc.img/&lt;id&gt;</c>.</summary>
    private string? ReadNpcString(int npcId, string key)
    {
        foreach (OpenFile file in _session.SelectRoleSources("String", null))
        {
            foreach (string name in new[]
                     {
                         npcId.ToString("D7", CultureInfo.InvariantCulture),
                         npcId.ToString(CultureInfo.InvariantCulture),
                     })
            {
                if (_session.TryResolve(WzPath.Join(file.Id, "Npc.img", name, key)) is WzStringProperty s)
                    return s.Value;
            }
        }
        return null;
    }

    private bool TryNpcImage(int npcId, out WzObject? image, out string path)
    {
        image = null;
        path = "";
        foreach (string candidate in new[]
                 {
                     npcId.ToString("D7", CultureInfo.InvariantCulture),
                     npcId.ToString(CultureInfo.InvariantCulture),
                 })
        {
            foreach (OpenFile file in _session.SelectRoleSources("Npc", null))
            {
                foreach (string prefix in new[] { "", "Npc/" })
                {
                    string candidatePath = WzPath.Join(file.Id, prefix + candidate + ".img");
                    if (_session.TryResolve(candidatePath) is { } found)
                    {
                        image = found;
                        path = candidatePath;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private IEnumerable<WzObject> Children(WzObject node)
    {
        try { return _session.EnumerateChildren(node).ToList(); }
        catch { return Array.Empty<WzObject>(); }
    }

    private static int? ReadInt(WzObject parent, string key)
    {
        foreach (WzObject child in WzChildren(parent))
        {
            if (!child.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return child switch
            {
                WzIntProperty i => i.Value,
                WzShortProperty s => s.Value,
                WzLongProperty l => (int)l.Value,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// A node's properties, or nothing.
    /// </summary>
    /// <remarks>
    /// <c>WzProperties</c> lives on <c>WzImageProperty</c>, not on the
    /// <c>WzObject</c> base — the compile error is what proved it. An NX container
    /// that is not a property (a bare canvas, say) simply has none, and that is a
    /// node with no children rather than a failure.
    /// </remarks>
    private static IEnumerable<WzObject> WzChildren(WzObject node)
    {
        if (node is not WzImageProperty container) return Array.Empty<WzObject>();
        try
        {
            return container.WzProperties is { } properties
                ? properties.Cast<WzObject>().ToList()
                : new List<WzObject>();
        }
        catch { return Array.Empty<WzObject>(); }
    }
}

/// <summary>What one NPC can say.</summary>
public sealed class NpcChatDto
{
    public string NpcId { get; set; } = "";

    /// <summary>False when no open Npc archive holds this id.</summary>
    public bool Found { get; set; }

    /// <summary>Session path of the NPC image, for /api/canvas.</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>
    /// <c>lines["info"]</c> — what <c>speak()</c> prefers (Npc.cpp:461-467).
    /// </summary>
    public List<string> General { get; } = new();

    /// <summary><c>lines[action]</c>, the per-animation fallback.</summary>
    public Dictionary<string, List<string>> ByAction { get; } = new();

    /// <summary>
    /// Every animation node on the image, <c>info</c> excluded. Reported
    /// separately from <see cref="States"/> because the two differ: a node whose
    /// <c>special</c> is not 0 is an animation but not part of the random pool
    /// (Npc.cpp:123-124 tests <c>== 0</c> for the pool), and collapsing them
    /// would make the pool look complete when it is not.
    /// </summary>
    public List<string> Actions { get; } = new();

    /// <summary>The animation states that join the random pool (Npc.cpp:123-124).</summary>
    public List<string> States { get; } = new();

    public NpcBalloonDto? Balloon { get; set; }
}

/// <summary>The balloon NPC text is drawn in.</summary>
public sealed class NpcBalloonDto
{
    public bool Found { get; set; }

    /// <summary>ARGB as the archive stores it; <c>0xFF800000</c> measured.</summary>
    public int TextColor { get; set; }

    public string Arrow { get; set; } = "";

    /// <summary>The 9-slice edge name -> canvas path.</summary>
    public Dictionary<string, string> Edges { get; } = new(StringComparer.OrdinalIgnoreCase);
}