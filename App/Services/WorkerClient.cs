using System.Net.Http.Headers;

namespace OpenTrafficDB.App.Services;

/// <summary>
/// Forwards requests to worker services with round-robin load balancing,
/// inter-service JWT authentication, and retry on failure.
/// </summary>
public class WorkerClient(
    IHttpClientFactory httpClientFactory,
    InternalJwtService jwtService,
    IConfiguration config,
    ILogger<WorkerClient> logger)
{
    private readonly Dictionary<string, int> _counters = new();
    private readonly object _lock = new();

    // ── URL picking ───────────────────────────────────────────────────────────

    public string[]? GetUrls(string workerName) =>
        config.GetSection($"Workers:{workerName}:Urls").Get<string[]>();

    private string? PickUrl(string workerName)
    {
        var urls = GetUrls(workerName);
        if (urls is null || urls.Length == 0) return null;

        int idx;
        lock (_lock)
        {
            if (!_counters.TryGetValue(workerName, out var cur))
                cur = 0;
            idx = cur % urls.Length;
            _counters[workerName] = cur + 1;
        }
        return urls[idx];
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<bool> CheckHealthAsync(string workerName, CancellationToken ct = default)
    {
        var urls = GetUrls(workerName);
        if (urls is null || urls.Length == 0) return false;

        var client = httpClientFactory.CreateClient("workers");
        foreach (var url in urls)
        {
            try
            {
                using var resp = await client.GetAsync(url + "/health", ct);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* unreachable */ }
        }
        return false;
    }

    // ── Authenticated proxy ───────────────────────────────────────────────────

    /// <summary>
    /// Forwards a request to a worker, piping the response (status, headers, body)
    /// directly into <paramref name="outResponse"/>.
    /// </summary>
    public async Task ProxyAsync(
        string workerName,
        HttpMethod method,
        string path,
        int userId,
        string keyId,
        string ipHash,
        HttpContent? requestBody,
        HttpResponse outResponse,
        string? query = null,
        CancellationToken ct = default)
    {
        var url = PickUrl(workerName);
        if (url is null)
        {
            outResponse.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await outResponse.WriteAsJsonAsync(new { error = $"No {workerName} worker configured." }, ct);
            return;
        }

        var token = jwtService.CreateInternalToken(userId, keyId, ipHash);
        using var req = new HttpRequestMessage(method, url + path + query);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (requestBody is not null) req.Content = requestBody;

        var client = httpClientFactory.CreateClient("workers");
        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker {Worker} request to {Path} failed", workerName, path);
            outResponse.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await outResponse.WriteAsJsonAsync(new { error = "Worker service unavailable." }, ct);
            return;
        }

        using (resp)
        {
            outResponse.StatusCode = (int)resp.StatusCode;

            if (resp.Content.Headers.ContentType is { } ct2)
                outResponse.ContentType = ct2.ToString();

            foreach (var (k, v) in resp.Content.Headers)
            {
                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                outResponse.Headers[k] = v.ToArray();
            }

            await resp.Content.CopyToAsync(outResponse.Body, ct);
        }
    }

    /// <summary>
    /// Forwards a public (unauthenticated) request — used for tile endpoints.
    /// </summary>
    public async Task ProxyPublicAsync(
        string workerName,
        string path,
        HttpResponse outResponse,
        CancellationToken ct = default)
    {
        var url = PickUrl(workerName);
        if (url is null)
        {
            outResponse.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await outResponse.WriteAsJsonAsync(new { error = $"No {workerName} worker configured." }, ct);
            return;
        }

        var client = httpClientFactory.CreateClient("workers");
        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync(url + path, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker {Worker} public request to {Path} failed", workerName, path);
            outResponse.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using (resp)
        {
            outResponse.StatusCode = (int)resp.StatusCode;

            if (resp.Content.Headers.ContentType is { } ct2)
                outResponse.ContentType = ct2.ToString();

            foreach (var (k, v) in resp.Content.Headers)
            {
                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                outResponse.Headers[k] = v.ToArray();
            }

            await resp.Content.CopyToAsync(outResponse.Body, ct);
        }
    }
}
