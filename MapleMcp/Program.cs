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
            .AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "maplebench-assets",
                    Version = "1.3.0",
                };

                options.ServerInstructions = """
                    Build and edit MapleStory maps (.img) in the archives the
                    MapleBench backend has open.

                    Generic WZ/IMG/NX tools are available as wz_asset_*.
                    Begin with wz_asset_files, open references read-only, inspect
                    nodes before editing, and save to a separate candidate path.
                    Generic previews only report intent; they do NOT validate the
                    backend or encryption. Paths and profiles differ from Orz.
                    This server does NOT yet replace Orz nx_build/publish,
                    split-WZ source lookup or patch verification. Trusted source
                    aliases use MAPLEBENCH_SOURCES; real encryption profiles must
                    be independently verified. Synthetic IMG roundtrip is tested;
                    production WZ/NX and game compatibility remain unverified.
                    Never claim that saving NX synchronized its source IMG.

                    The order to work in:

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
                    """;
            })
            .WithToolsFromAssembly()
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
        return 0;
    }
}