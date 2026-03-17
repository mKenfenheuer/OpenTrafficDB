namespace OpenTrafficDB.App.Services;

/// <summary>HTTP client handler that retries on 429 Too Many Requests with exponential backoff.</summary>
public class RateLimitRetryHandler : DelegatingHandler
{
    private const int MaxRetries = 3;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode != 429 || attempt == MaxRetries)
                return response;

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            if (response.Headers.RetryAfter?.Delta is { } retryAfter)
                delay = retryAfter;

            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
