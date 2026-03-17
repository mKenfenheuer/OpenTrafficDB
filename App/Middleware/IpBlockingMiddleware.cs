using OpenTrafficDB.App.Services;

namespace OpenTrafficDB.App.Middleware;

public class IpBlockingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AbuseDetectionService abuseDetection)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (await abuseDetection.IsIpBlockedAsync(ip))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers["Retry-After"] = "86400";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Access denied.",
                detail = "This IP has been blocked due to policy violations."
            });
            return;
        }

        await next(context);
    }
}
