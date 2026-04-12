using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.AggregateWorker.Services;
using OpenTrafficDB.Shared.Services;
using StackExchange.Redis;

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        o => { o.UseNetTopologySuite(); o.CommandTimeout(300); }));

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        o => { o.UseNetTopologySuite(); o.CommandTimeout(300); }),
    ServiceLifetime.Scoped);

var readConn = builder.Configuration.GetConnectionString("ReadConnection")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ReadOnlyAppDbContext>(options =>
    options.UseNpgsql(readConn, o => o.UseNetTopologySuite()));

// ── Redis ─────────────────────────────────────────────────────────────────────
var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConn;
    options.InstanceName  = "otdb:";
});
// Register IConnectionMultiplexer for distributed locking and pub/sub
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConn));

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<TrafficAggregationService>();
builder.Services.AddScoped<MapMatchingService>();
builder.Services.AddScoped<TrafficTileService>();
builder.Services.AddScoped<HazardTileService>();
builder.Services.AddHostedService<PartitionMaintenanceService>();
builder.Services.AddTransient<RateLimitRetryHandler>();

// ── HTTP clients ──────────────────────────────────────────────────────────────
// OpenTrafficRouting: resilience pipeline — retry (x2) + circuit breaker + per-attempt timeout
var otrUrl = builder.Configuration["OpenTrafficRouting:BaseUrl"] ?? "http://localhost:8002";
builder.Services.AddHttpClient("otr", client =>
{
    client.BaseAddress = new Uri(otrUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
}).AddResilienceHandler("otr-pipeline", pipeline =>
{
    pipeline.AddTimeout(TimeSpan.FromMinutes(5));
    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay            = TimeSpan.FromSeconds(1),
        BackoffType      = DelayBackoffType.Constant,
        ShouldHandle     = args => ValueTask.FromResult(
            (args.Outcome.Exception is not null
                && args.Outcome.Exception is not Polly.CircuitBreaker.BrokenCircuitException) ||
            (int?)args.Outcome.Result?.StatusCode >= 500)
    });
    pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
    {
        SamplingDuration   = TimeSpan.FromMinutes(5),
        FailureRatio       = 0.5,
        MinimumThroughput  = 5,
        BreakDuration      = TimeSpan.FromMinutes(5)
    });
});

// ── Internal JWT Authentication ───────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer           = true,
            ValidIssuer              = "opentrafficdb-app",
            ValidateAudience         = true,
            ValidAudience            = "opentrafficdb-internal",
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.FromSeconds(30)
        };
    });

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!)
    .AddRedis(redisConn, name: "redis")
    .AddUrlGroup(new Uri(otrUrl + "/api/v1/graph/status"), name: "opentrafficrouting",
        timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

// ── Migrations ────────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint("/metrics");

await app.RunAsync();
