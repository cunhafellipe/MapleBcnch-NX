// The two doors the live simulator needs, opened without touching the
// existing read path.
//
// MapEditorService already parses a map the one way the whole editor agrees
// with -- LoadForReading's own comment (:255) says a loader that guessed the
// shape differently "would disagree with the editor about the same map, which
// is worse than having no simulator at all". The live body in
// Services/Simulate/MapSimSessionStore.cs has to walk that same parsed map and
// step under the same lock, so it needs those two members. They stay internal:
// this widens access from one sibling namespace, not from the whole assembly,
// and the existing private methods are untouched rather than re-plumbed.

using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

public sealed partial class MapEditorService
{
    /// <summary>
    /// The editor's own lock over open documents.
    /// </summary>
    /// <remarks>
    /// A stepping character and an editor edit touch the same parsed map, so
    /// they have to share one lock. A second lock here would be worse than none:
    /// it would look correct while letting a frame read a map mid-edit.
    /// </remarks>
    internal object EditorGate => _session.Gate;

    /// <summary>
    /// Parse a map for reading, exactly as the rest of the simulator does.
    /// </summary>
    internal MapDocument LoadForReadingShared(string path) => LoadForReading(path);

    /// <summary>
    /// Where a character dropped at (x, y) lands, or where it starts when no
    /// height was given.
    /// </summary>
    internal static FootholdPhysics.Foot? FindStartFootholdShared(
        FootholdPhysics physics, int x, int? startY)
        => FindStartFoothold(physics, x, startY);
}