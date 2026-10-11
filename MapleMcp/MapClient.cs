using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapleMcp;

/// <summary>
/// Talks to the running MapleBench backend's mapedit endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Small on purpose. The older <c>WzCli</c> client is 217 KB and speaks the
/// whole archive vocabulary; this one speaks <c>/api/mapedit/*</c> and nothing
/// else, so there is no second set of assumptions about how a path is spelled
/// to drift out of agreement with the endpoints actually serving it.
/// </para>
/// <para>
/// Every failure carries the server's own message. The backend is unusually
/// good at this — it answers "Tile/ancientForest.img/bsc/10 does not resolve in
/// the open session" rather than a status code — and swallowing that would
/// throw away the most useful thing an agent gets.
/// </para>
/// </remarks>
public sealed class MapClient
{
    public const string DefaultServer = "http://127.0.0.1:5100";

    private readonly HttpClient _http;

    public MapClient(string? baseUrl = null)
    {
        BaseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("WZ_SERVER")
                   ?? DefaultServer).TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };

        // A 679 MB archive takes ~0.5s to open and ~10s to save. The default
        // 100s would be tight on a cold archive over a mapped file.
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public string BaseUrl { get; }

    /// <summary>An error carrying the backend's own words, not a bare status.</summary>
    public sealed class BackendError(string message) : Exception(message);

    public async Task<JsonNode?> GetAsync(string route, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.GetAsync(route, ct);
        return await ReadAsync(res, route, ct);
    }

    public async Task<JsonNode?> PostAsync(string route, object? body, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.PostAsJsonAsync(route, body ?? new { }, ct);
        return await ReadAsync(res, route, ct);
    }

    private static async Task<JsonNode?> ReadAsync(
        HttpResponseMessage res, string route, CancellationToken ct)
    {
        string text = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);

        // The backend answers a bad request with a sentence that names the
        // offending argument. That sentence IS the error message the agent
        // needs, so it is passed through verbatim.
        string detail = ExtractMessage(text) ?? text;
        throw new BackendError($"{route} -> HTTP {(int)res.StatusCode}: {detail}");
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            JsonNode? node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                string? message = obj["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(message))
                    return message;
            }
        }
        catch (JsonException)
        {
            // Not JSON — the raw body is the best we have.
        }
        return null;
    }

    /// <summary>Serialises for a tool result: indented, so a human can read it too.</summary>
    public static string J(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    /// <summary>
    /// Percent-encodes a value for a query string.
    /// </summary>
    /// <remarks>
    /// Map paths contain slashes and often CJK. A map name like
    /// <c>彩虹岛 : 蘑菇村西入口</c> has to survive as a query parameter, so it is
    /// encoded rather than concatenated — the backend decodes it, and an agent
    /// that searches by name will hit this on its first try.
    /// </remarks>
    public static string Q(string value) => Uri.EscapeDataString(value);
}