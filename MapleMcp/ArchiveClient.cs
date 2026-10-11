using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapleMcp;

/// <summary>
/// Talks to the running MapleBench backend's archive endpoints — the ones that
/// are not <c>/api/mapedit/*</c>.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="MapClient"/> on purpose. That client documents
/// why it speaks only <c>/api/mapedit/*</c>: so there is exactly one set of
/// assumptions about how a path is spelled. Mixing the generic archive routes in
/// would undo that, and the two vocabularies genuinely differ — a map session is
/// <c>f1/Map/Map0/000000001.img</c>, while the export routes take a plain node
/// path and answer with bytes rather than JSON.
/// </para>
/// <para>
/// This client is the piece the map server was missing. It can open an archive
/// and serialise any image node to a standalone <c>.img</c> file, which is the
/// step that turns a split client's 67 GB of archives into a tree of files a
/// tool like WzImg-MCP can read.
/// </para>
/// </remarks>
public sealed class ArchiveClient
{
    private readonly HttpClient _http;

    public ArchiveClient(string? baseUrl = null)
    {
        BaseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("WZ_SERVER")
                   ?? MapClient.DefaultServer).TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };

        // Opening a 679 MB archive runs ~0.5s; exporting one image node reads
        // and serialises it. A whole-archive split is thousands of these.
        _http.Timeout = TimeSpan.FromMinutes(5);
    }

    public string BaseUrl { get; }

    public sealed class BackendError(string message) : Exception(message);

    /// <summary>Is the backend up? Cheap, and the answer changes what to advise.</summary>
    public async Task<bool> IsUpAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage res = await _http.GetAsync("api/files", ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<JsonNode?> GetJsonAsync(string route, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.GetAsync(route, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        throw new BackendError(Detail(route, (int)res.StatusCode, text));
    }

    public async Task<JsonNode?> PostJsonAsync(
        string route, object? body, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.PostAsJsonAsync(route, body ?? new { }, ct);
        string text = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode)
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        throw new BackendError(Detail(route, (int)res.StatusCode, text));
    }

    /// <summary>Closing is best-effort: never let cleanup mask the real failure.</summary>
    /// <remarks>
    /// A transport failure returns false, but a refusal from the backend carries the
    /// reason it refused — "it has unsaved changes", "it is not open". Collapsing both
    /// into false reports a live backend as gone and throws the explanation away.
    /// </remarks>
    public async Task<bool> CloseAsync(string fileId, CancellationToken ct = default)
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Delete, $"api/files/{fileId}");
            using HttpResponseMessage res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
                return true;

            string body = await res.Content.ReadAsStringAsync(ct);
            throw new BackendError(
                $"The backend refused to close {fileId} (HTTP {(int)res.StatusCode}): "
                + (ExtractMessage(body) ?? body));
        }
        catch (BackendError)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The backend's own sentence, when it sent one.</summary>
    private static string? ExtractMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fetches serialised bytes for a node — here, one .img.</summary>
    public async Task<byte[]> GetBytesAsync(string route, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.GetAsync(route, ct);
        if (!res.IsSuccessStatusCode)
        {
            string text = await res.Content.ReadAsStringAsync(ct);
            throw new BackendError(Detail(route, (int)res.StatusCode, text));
        }
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static string Detail(string route, int status, string body)
    {
        string detail = body;
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj &&
                obj["message"]?.GetValue<string>() is { Length: > 0 } m)
            {
                detail = m;
            }
        }
        catch (JsonException)
        {
            // Not JSON — the raw body is the best available message.
        }
        return $"{route} -> HTTP {status}: {detail}";
    }

    public static string J(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    public static string Q(string value) => Uri.EscapeDataString(value);
}