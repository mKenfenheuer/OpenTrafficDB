using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using OpenTrafficDB.Shared.Models.DTOs;
using OpenTrafficDB.AggregateWorker.Services;
using StackExchange.Redis;

namespace OpenTrafficDB.AggregateWorker.Controllers;

[ApiController]
[Route("internal/traffic")]
[Authorize]
public class TrafficInternalController(
    TrafficAggregationService aggregation,
    IDistributedCache cache,
    IConnectionMultiplexer redis) : ControllerBase
{
    private const string ValhallaCacheKey = "valhalla_csv";
    private const string ValhallaLockKey  = "valhalla_csv_lock";
    private static readonly DistributedCacheEntryOptions ValhallaCacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
    };

    [HttpGet("valhalla/csv")]
    [Produces("text/csv")]
    public async Task<IActionResult> GetValhallaExportCsv(CancellationToken ct)
    {
        Response.ContentType = "text/csv";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"valhalla_traffic.csv\"";

        // Fast path: serve from cache
        var cached = await cache.GetAsync(ValhallaCacheKey, ct);
        if (cached is not null)
        {
            await Response.Body.WriteAsync(cached, ct);
            return new EmptyResult();
        }

        // Distributed lock: only one worker (across all instances) generates at a time
        var db   = redis.GetDatabase();
        var held = await db.StringSetAsync(ValhallaLockKey, "1",
            TimeSpan.FromMinutes(35), When.NotExists);

        if (!held)
        {
            // Another instance is generating — ask client to retry
            Response.Headers["Retry-After"] = "30";
            return StatusCode(503, new { error = "CSV generation in progress. Retry in ~30 seconds." });
        }

        try
        {
            // Double-check after acquiring lock
            cached = await cache.GetAsync(ValhallaCacheKey, ct);
            if (cached is not null)
            {
                await Response.Body.WriteAsync(cached, ct);
                return new EmptyResult();
            }

            var tmpFile = Path.GetTempFileName();
            try
            {
                await using (var fileStream = new FileStream(tmpFile, FileMode.Create, FileAccess.Write,
                                 FileShare.None, bufferSize: 65536, useAsync: true))
                await using (var tee = new TeeStream(Response.Body, fileStream))
                {
                    await aggregation.WriteValhallaExportCsvAsync(tee, ct);
                }

                var bytes = await System.IO.File.ReadAllBytesAsync(tmpFile, ct);
                await cache.SetAsync(ValhallaCacheKey, bytes, ValhallaCacheOptions, ct);
            }
            finally
            {
                System.IO.File.Delete(tmpFile);
            }
        }
        finally
        {
            await db.KeyDeleteAsync(ValhallaLockKey);
        }

        return new EmptyResult();
    }

}

/// <summary>Writes every byte to two streams simultaneously.</summary>
internal sealed class TeeStream(Stream first, Stream second) : Stream
{
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        first .Write(buffer, offset, count);
        second.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        first .Write(buffer);
        second.Write(buffer);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var mem = buffer.AsMemory(offset, count);
        var t1  = first .WriteAsync(mem, ct);
        var t2  = second.WriteAsync(mem, ct);
        await t1;
        await t2;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var t1 = first .WriteAsync(buffer, ct);
        var t2 = second.WriteAsync(buffer, ct);
        await t1;
        await t2;
    }

    public override void Flush()
    {
        first .Flush();
        second.Flush();
    }

    public override async Task FlushAsync(CancellationToken ct)
    {
        var t1 = first .FlushAsync(ct);
        var t2 = second.FlushAsync(ct);
        await t1;
        await t2;
    }

    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();
}
