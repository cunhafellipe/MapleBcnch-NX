using MapleBench.Services.MapEditor;

namespace MapleBench.Api;

/// <summary>
/// The simulator's HTTP surface — a SEPARATE surface from the map editor.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not <c>/api/mapedit/simulate</c>. The map editor is
/// where a person edits a map: it opens documents, tracks dirty state, and owns
/// undo. None of that belongs to a read-only question about walkability, and
/// mounting it under the editor would make the simulator depend on having a map
/// open in an editing session — which is exactly the thing an agent verifying a
/// freshly generated map does not have.
/// </para>
/// <para>
/// The simulator answers about any map the BACKEND can see, and never mutates
/// one. It borrows the map model, not the editing session.
/// </para>
/// </remarks>
public static class SimulatorEndpoints
{
    public static void MapSimulator(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup("/simulate");

        // What the simulator can do with what is currently open. The frontend
        // uses this to explain itself when no map archive is loaded, rather than
        // offering a picker that cannot produce a result.
        group.MapGet("/capabilities", (MapEditorService maps) =>
            Results.Ok(maps.SimulatorCapabilities()));

        // List the maps that can be simulated. Same shape as the editor's list,
        // but it does NOT require an editing session to be open for them.
        group.MapGet("/maps", (string? q, int? limit, MapEditorService maps) =>
            Results.Ok(maps.ListMaps(q, Math.Clamp(limit ?? 200, 1, 2000))));

        // Walk a map. Read-only: it inspects and reports, and there is no
        // commit parameter anywhere in this group because nothing here writes.
        group.MapPost("/walk", (MapSimulateRequest request, MapEditorService maps) =>
            Results.Ok(maps.Simulate(request.Path, request.StartX, request.StartY)));

        // Walk an actual CHARACTER, under the client's own physics. This is the
        // one that can catch a map the link chain calls intact but that a
        // character physically cannot cross.
        group.MapPost("/character", (MapSimulateRequest request, MapEditorService maps) =>
            Results.Ok(maps.WalkCharacter(request.Path, request.StartX, request.StartY,
                request.Direction, request.JumpEvery, request.DropAtStep)));

        // Why is the character standing where it is? A caller that distrusts the
        // walk's Y needs to see which footholds exist under that X, and this is
        // the only way to ask without opening the map in the editor.
        group.MapGet("/probe", (string path, int x, int y, MapEditorService maps) =>
            Results.Ok(maps.ProbeGround(path, x, y)));
    }
}