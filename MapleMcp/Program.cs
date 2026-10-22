using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MapleMcp;

namespace MapleMcp;

/// <summary>
/// MCP server exposing the map editor's authoring surface.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the older <c>wz-mcp</c>, which lives in another repository and
/// reaches only the generic node API. An agent asked to build a map cannot get
/// started there: there is no way to list maps, open one, place a tile or check
/// the result. This server is that missing surface.
/// </para>
/// <para>
/// Instructions are not decoration — an agent reads them before it touches
/// anything, so the order here is the order it should work in.
/// </para>
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout IS the JSON-RPC channel on a stdio MCP server. One log line
        // there corrupts the stream, so every level goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddSingleton(_ => new MapClient())
            .AddSingleton(_ => new ArchiveClient())
            .AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "maplebench-assets",
                    Version = "1.4.0",
                };

                options.ServerInstructions = """
                    Nine surfaces over one MapleBench backend, 91 tools.
                    All need the backend running (MapleBenchX.exe --browser
                    --port <p>), and the archives must be OPENED first.

                    START HERE

                      wz_asset_files        which archives are open, and their ids
                      wz_diag_health        backend version, memory, live sessions

                    FILE IDS ARE NOT STABLE. A path starts with f1, f2, f25 --
                    the id is handed out in the order archives were opened, so a
                    server that restarted, or that had archives closed and
                    reopened, renumbers them. MEASURED 2026-10-17: the same
                    Map.nx was f4 in one run and f25 in the next. A stale id
                    answers "No open file with id 'f4'", which reads like the
                    data is gone and is not. Take the id from wz_asset_files or
                    wz_map_list at the time you use it.

                    TWO SIMULATORS, AND PICKING THE WRONG ONE COSTS AN HOUR.

                      wz_map_simulate, wz_map_session_*, wz_map_ground
                          THE MAP EDITOR'S physics. Answers "can a character cross
                          this map", "where does it stop", "what is under this
                          foothold". No character state persists between calls.

                      wz_sim_*
                          THE LIVE SIMULATOR -- the session the viewer's canvas is
                          attached to. Answers "I pressed K and nothing happened",
                          "what did that cast do to that mob", "which pose plays".
                          A session survives across calls until wz_sim_close.

                    THE LIVE SIMULATOR, IN ORDER

                      1. wz_sim_open      a character on a map -> sessionId
                      2. wz_sim_catalog   what can be cast, what can be summoned
                      3. wz_sim_skills    bind a key: bindings ["1121008:K"]
                      4. wz_sim_summon    put a mob in the world
                      5. wz_sim_step      walk it into range (left/right/...)
                      6. wz_sim_cast      one frame asking for a skill
                      7. wz_sim_state     read it: action, skillStance,
                                           skillEffects, and every mob's hp

                    Damage is FIXED at 100 per hit and every mob spawns at 1000
                    (ADR-0006), so "the cast landed" is a number you can check
                    rather than a formula you would have to trust. Ten hits kill
                    anything.

                    GAME DATA DOMAINS. wz_data_* reaches skill, mob, npc, string
                    and cash shop with their own field validation and their own
                    bulk semantics. Use it rather than wz_asset_set_value for
                    those: the generic node route will happily write a value the
                    domain cannot load.

                    LONG JOBS. wz_job_* covers eight families (audit, dump,
                    compose, import, repair.*) with one shape. repair.* writes
                    archives, so its start takes commit. A family that does not
                    have the stage you asked for throws and names the families
                    that do -- it never substitutes another family's route.

                    RENDERING. wz_render_view proves a node DECODES. It does not
                    prove the pixels match a reference and it does not prove the
                    art is the right art. Do not report a rendering as
                    verification of anything beyond that.

                    To put a finished candidate .img INTO an archive the client
                    opens, use wz_asset_merge_nx. It takes the target .nx and a
                    list of {source, target} entries, previews with commit=false,
                    then rewrites the whole archive (it keeps a timestamped
                    backup and verifies the bytes by re-reading them). The target
                    archive must NOT be open in this session. Use this for map
                    IMGs and their object art; wz_map_save is for edits inside an
                    archive that is already open.

                    SAFETY. Every mutating tool takes commit and defaults to
                    false, which reports what it would do and changes nothing.
                    Preview, read the result, then commit. Nothing touches disk
                    until wz_map_save or wz_save_commit runs with commit=true,
                    and saving rewrites the whole archive -- it keeps a
                    timestamped backup, but it is still a rewrite. The wz_sim_*
                    family is the deliberate exception and says why in its own
                    remarks: those sessions live in memory and are gone at
                    wz_sim_close.

                    SAVING. An archive is memory-mapped for the life of the
                    process once it is opened, and no endpoint closes one. So
                    wz_asset_close discards unsaved work, wz_save_memory's sweep
                    is the safe handle, and a clean wz_save_preflight does not
                    promise that the save will succeed.

                    AUTHORING MAPS. Footholds are not optional and no tile set in
                    Map.nx carries foothold geometry, so a map with ground and no
                    footholds cannot be walked: they come from wz_map_place with
                    kind=foothold, they have to chain prev/next to real
                    neighbours, and wz_map_validate is what checks it.

                    SPLITTING ARCHIVES INTO .img FILES. wz_archive_split writes
                    one .img per resource with the backend's own serialiser, so
                    the bytes match what the editor saves. Two things cost hours
                    if ignored: a _Canvas folder beside a data archive holds the
                    sprites the data archive only POINTS at, so splitting it
                    yields hundreds of MB of archive contents rather than
                    resources (leave includeCanvas false); and 67 GB of archives
                    can split into more than the drive has room for, so dryRun
                    first.
                    """;
            })
            .WithToolsFromAssembly()
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
        return 0;
    }
}
