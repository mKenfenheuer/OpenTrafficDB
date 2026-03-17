using Microsoft.EntityFrameworkCore;
using OpenTrafficDB.OsmWayImporter.Services;
using OpenTrafficDB.Shared.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseNetTopologySuite()));

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddHostedService<WayGeometryBackgroundService>();

builder.Services.AddHttpClient("pbf", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTrafficDB/1.0");
});

builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);

var app = builder.Build();

app.MapHealthChecks("/health");

await app.RunAsync();
