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
                    Name = "maplebench",
                    Version = "1.1.0",
                };

                options.ServerInstructions = """
                    Two surfaces over one MapleBench backend: build and edit
                    maps, and split archives into .img files. Both need the
                    backend running (MapleBenchX.exe --browser).

                    AUTHORING MAPS

                      1. wz_map_capabilities   what is open and usable
                      2. wz_map_list           find a map, or copy one as a style
                      3. wz_map_open           read its structure
                      4. wz_map_palette        choose material with drawableEntries > 0
                      5. wz_map_place          single edits
                         wz_map_batch          many edits as ONE undo step
                      6. wz_map_validate       the rules that make a map playable
                      7. wz_map_save           write to disk

                    SAFETY. Every mutating tool takes commit and defaults to
                    false, which reports what it would do and changes nothing.
                    Preview, read the result, then commit. Nothing touches disk
                    until wz_map_save runs with commit=true, and saving rewrites
                    the whole archive — it keeps a timestamped backup, but it is
                    still a rewrite.

                    FOOTHOLDS ARE NOT OPTIONAL. A map with ground and no
                    footholds cannot be walked. The tile material itself does
                    not carry foothold geometry — no set in Map.nx does — so
                    footholds must come from wz_map_place with kind=foothold, and
                    they have to chain: each one's prev/next points at a real
                    neighbour. wz_map_validate is what checks that.

                    Map paths look like "f1/Map/Map0/000000001.img" — an open
                    archive id, then the path inside it. wz_map_list hands these
                    back ready to use.

                    A map may be a link stub with no geometry of its own;
                    wz_map_open says so when that happens. Author against the
                    target, not the stub.

                    SPLITTING ARCHIVES INTO .img FILES

                      1. wz_archive_capabilities   backend up? what is open?
                      2. wz_archive_scan           what is in that folder
                      3. wz_archive_split          write the .img tree
                      4. wz_archive_export_img     one node, when you need to
                                                    check a single file

                    A split client stores data as .wz archives, but most tools
                    that read MapleStory data — WzImg-MCP, HaCreator, the
                    reference BeiDou-Client tree — want one .img file per
                    resource. wz_archive_split is the step between them: it
                    opens each archive and writes every image node as its own
                    file, using the backend's own serialiser so the bytes match
                    what the editor saves.

                    TWO THINGS THAT WASTE A LOT OF TIME IF IGNORED.

                    _Canvas. A category folder often holds both the data
                    archive and a _Canvas folder beside it, and the data
                    archive's image nodes hold names, fields and frame lists
                    that POINT at the sprites. The _Canvas archives hold the
                    sprites themselves, indexed by asset key — so each of its
                    nodes serialises to one .img that is most or all of the
                    archive. Splitting one produces single files in the hundreds
                    of MB that are archive contents, not resources. Leave
                    includeCanvas false.

                    SIZE. 67 GB of archives can split into far more than the
                    drive has room for. Run wz_archive_split with dryRun=true
                    first to see the count, and limitFiles to trial the path
                    end to end before committing to a full run.

                    A category root file such as Mob.wz is often 216 bytes: a
                    PKG1 pointer naming the real split files, not data. The
                    tools skip these and say so rather than reporting an empty
                    archive.
                    """;
            })
            .WithToolsFromAssembly()
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
        return 0;
    }
}