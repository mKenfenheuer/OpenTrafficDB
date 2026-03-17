using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.AggregateWorker.Services;
using OpenTrafficDB.Shared.Services;

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseNetTopologySuite()));

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseNetTopologySuite()),
    ServiceLifetime.Scoped);

var readConn = builder.Configuration.GetConnectionString("ReadConnection")
    ?? builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ReadOnlyAppDbContext>(options =>
    options.UseNpgsql(readConn, o => o.UseNetTopologySuite()));

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<TrafficAggregationService>();
builder.Services.AddScoped<ValhallaMapMatchingService>();
builder.Services.AddScoped<TrafficTileService>();
builder.Services.AddHostedService<WayGeometryBackgroundService>();
builder.Services.AddTransient<RateLimitRetryHandler>();

builder.Services.AddHttpClient("valhalla", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<RateLimitRetryHandler>();

builder.Services.AddHttpClient("pbf", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTrafficDB/1.0");
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

var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConn;
    options.InstanceName = "otdb:";
});
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

await app.RunAsync();
