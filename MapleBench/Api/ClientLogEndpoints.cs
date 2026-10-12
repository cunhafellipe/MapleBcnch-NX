using System.Collections.Concurrent;

namespace MapleBench.Api;

/// <summary>
/// Receives what the page reports about itself.
/// </summary>
/// <remarks>
/// <para>
/// This project verified itself two ways and both ways passed while the page
/// was broken. <c>node --check</c> parses and cannot see a
/// <c>ReferenceError</c>; a curl against the API cannot see that a section threw
/// before it rendered. A leftover <c>this.pickerHost = list</c> in
/// <c>simulator.js</c> — pointing at a variable that no longer existed — passed
/// every automated check in the repository and left the simulator blank.
/// </para>
/// <para>
/// The browser already knew what happened. Nothing was listening. The page's
/// <c>js/errorreport.js</c> now posts uncaught errors, promise rejections and
/// console errors here, so a failure can be read with the same one-line curl as
/// everything else in this project.
/// </para>
/// <para>
/// It is a diagnostic surface, not a log: entries are held in memory, newest
/// last, and never touch the disk.
/// </para>
/// </remarks>
public static class ClientLogEndpoints
{
    private const int MaxEntries = 200;

    /// <summary>One report from the page.</summary>
    private sealed record ClientLogEntry(DateTimeOffset At, string Body);

    private static readonly ConcurrentQueue<ClientLogEntry> Entries = new();

    /// <summary>How many reports have arrived since the process started.</summary>
    public static int Count;

    public static void MapClientLog(this RouteGroupBuilder api)
    {
        api.MapPost("/clientlog", async (HttpContext ctx) =>
        {
            // ReadToEnd() is a synchronous read, which ASP.NET refuses by default
            // ("Synchronous operations are disallowed"). That turned the POST into
            // a 400, and a 400 here is invisible: the page's reporting silently
            // dropped every error, which is the one thing this endpoint exists to
            // prevent.
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            if (body.Length > 64_000) body = body[..64_000];

            Entries.Enqueue(new ClientLogEntry(DateTimeOffset.UtcNow, body));
            Interlocked.Increment(ref Count);
            while (Entries.Count > MaxEntries && Entries.TryDequeue(out _)) { /* trim */ }

            return Results.Ok(new { ok = true, count = Entries.Count });
        });

        // The read side. A GET is what makes this usable from a terminal.
        api.MapGet("/clientlog", (string? kind) =>
        {
            var list = Entries.ToArray();
            var filtered = string.IsNullOrEmpty(kind)
                ? list
                : list.Where(e => e.Body.Contains(kind, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Results.Ok(new
            {
                count = list.Length,
                entries = filtered.Select(e => new { at = e.At, body = e.Body }),
            });
        });

        api.MapDelete("/clientlog", () =>
        {
            while (Entries.TryDequeue(out _)) { }
            Interlocked.Exchange(ref Count, 0);
            return Results.Ok(new { ok = true });
        });
    }
}