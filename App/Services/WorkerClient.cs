using System.Net.Http.Headers;
using System.Net.Http.Json;
using OpenTrafficDB.Shared.Models.DTOs;

namespace OpenTrafficDB.Shared.Services;

/// <summary>
/// Forwards requests to worker services with round-robin load balancing,
/// inter-service JWT authentication, and retry on failure.
/// Reports per-request latency and errors to <see cref="WorkerHealthService"/>.
/// </summary>
public class WorkerClient(
    IHttpClientFactory httpClientFactory,
    InternalJwtService jwtService,
    IConfiguration config,
    ILogger<WorkerClient> logger,
    // Optional: resolved lazily to avoid circular dependency during startup
    IServiceProvider services)
{
    private readonly Dictionary<string, int> _counters = new();
    private readonly object _lock = new();

    private WorkerHealthService? Health =>
        services.GetService<WorkerHealthService>();

    // ── URL picking ───────────────────────────────────────────────────────────

    public string[]? GetUrls(string workerName) =>
        config.GetSection($"Workers:{workerName}:Urls").Get<string[]>();

    private string? PickUrl(string workerName)
    {
        var urls = GetUrls(workerName);
        if (urls is null || urls.Length == 0) return null;
        if (urls.Length == 1) return urls[0];

        var health = Health;
        lock (_lock)
        {
            if (!_counters.TryGetValue(workerName, out var cur)) cur = 0;

            // Prefer non-unhealthy workers, round-robining through them in order.
            for (var i = 0; i < urls.Length; i++)
            {
                var idx = (cur + i) % urls.Length;
                if (health?.GetInstanceStatus(workerName, urls[idx]) != WorkerStatus.Unhealthy)
                {
                    _counters[workerName] = idx + 1;
                    return urls[idx];
                }
            }

            // All workers unhealthy — fall back to plain round-robin.
            var fallback = cur % urls.Length;
            _counters[workerName] = cur + 1;
            return urls[fallback];
        }
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <summary>Checks health of a specific instance URL. Used by WorkerHealthService.</summary>
    public async Task<bool> CheckHealthAsync(string url, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("workers");
        try
        {
            using var resp = await client.GetAsync(url + "/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            sw.Stop();
            Health?.RecordRequest(workerName, url, sw.Elapsed.TotalMilliseconds, success: false);
            logger.LogError(ex, "Worker {Worker} request to {Path} failed", workerName, path);
            outResponse.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await outResponse.WriteAsJsonAsync(new { error = "Worker service unavailable." }, ct);
            return;
        }
        sw.Stop();
        Health?.RecordRequest(workerName, url, sw.Elapsed.TotalMilliseconds, resp.IsSuccessStatusCode);

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            resp = await client.GetAsync(url + path, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            sw.Stop();
            Health?.RecordRequest(workerName, url, sw.Elapsed.TotalMilliseconds, success: false);
            logger.LogError(ex, "Worker {Worker} public request to {Path} failed", workerName, path);
            outResponse.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        sw.Stop();
        Health?.RecordRequest(workerName, url, sw.Elapsed.TotalMilliseconds, resp.IsSuccessStatusCode);

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
    /// Sends a match job to a specific worker URL and returns the result.
    /// Uses the long-timeout "workerJobs" HTTP client.
    /// Returns null if the request fails.
    /// </summary>
    public async Task<MatchJobResult?> SendMatchJobAsync(string workerUrl, MatchJobRequest req, CancellationToken ct = default)
    {
        var token = jwtService.CreateServiceToken();
        var client = httpClientFactory.CreateClient("workerJobs");
        using var request = new HttpRequestMessage(HttpMethod.Post, workerUrl + "/internal/jobs/match");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(req);
        try
        {
            using var resp = await client.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<MatchJobResult>(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Match job request to {Url} failed", workerUrl);
            return null;
        }
    }

    /// <summary>Sends an aggregate job (partitioned by way ID) to a specific worker URL. Returns null on failure.</summary>
    public async Task<AggregateJobResult?> SendAggregateJobAsync(string workerUrl, AggregateJobRequest req, CancellationToken ct = default)
    {
        var token = jwtService.CreateServiceToken();
        var client = httpClientFactory.CreateClient("workerJobs");
        using var request = new HttpRequestMessage(HttpMethod.Post, workerUrl + "/internal/jobs/aggregate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(req);
        try
        {
            using var resp = await client.SendAsync(request, ct);
            return resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<AggregateJobResult>(ct)
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aggregate job request to {Url} failed", workerUrl);
            return null;
        }
    }

    /// <summary>Sends a finalize request to a round-robin-picked Aggregate worker.</summary>
    public Task SendFinalizeAsync(CancellationToken ct = default)
    {
        var url = PickUrl("Aggregate");
        if (url is null)
        {
            logger.LogWarning("No Aggregate worker URLs configured — skipping finalize.");
            return Task.CompletedTask;
        }
        return SendFinalizeAsync(url, ct);
    }

    /// <summary>Sends a finalize request (refresh constrained speeds + delete old obs) to a specific worker.</summary>
    public async Task SendFinalizeAsync(string workerUrl, CancellationToken ct = default)
    {
        var token = jwtService.CreateServiceToken();
        var client = httpClientFactory.CreateClient("workerJobs");
        using var request = new HttpRequestMessage(HttpMethod.Post, workerUrl + "/internal/jobs/finalize");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await client.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("Finalize request to {Url} returned {Status}", workerUrl, resp.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finalize request to {Url} failed", workerUrl);
        }
    }
}
