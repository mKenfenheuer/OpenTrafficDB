using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTrafficDB.App.Data;
using OpenTrafficDB.App.Services;

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")),
    ServiceLifetime.Scoped);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<TrafficAggregationService>();
builder.Services.AddScoped<ValhallaMapMatchingService>();
builder.Services.AddScoped<TrafficTileService>();
builder.Services.AddScoped<WayGeometryService>();
builder.Services.AddHostedService<AggregationBackgroundService>();
builder.Services.AddHostedService<WayGeometryBackgroundService>();
builder.Services.AddTransient<RateLimitRetryHandler>();

builder.Services.AddHttpClient("valhalla", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<RateLimitRetryHandler>();

builder.Services.AddHttpClient("overpass", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTrafficDB/1.0");
}).AddHttpMessageHandler<RateLimitRetryHandler>();

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

builder.Services.AddMemoryCache();
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
