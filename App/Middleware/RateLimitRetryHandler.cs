namespace OpenTrafficDB.App.Middleware;

/// <summary>
/// Retries a request once after 1 minute when the upstream returns 429 Too Many Requests.
/// Respects Retry-After header if present.
/// </summary>
public class RateLimitRetryHandler(ILogger<RateLimitRetryHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
    retry:
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
            return response;

        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
        logger.LogWarning("429 from {Url} — retrying after {Seconds}s", request.RequestUri, delay.TotalSeconds);

        try
        {
            await Task.Delay(delay, ct);
        }
        catch
        {

        }
        if (!ct.IsCancellationRequested)
        {
            response.Dispose();
            goto retry;
        }
        return response;
    }
}
