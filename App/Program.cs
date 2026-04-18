using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTrafficDB.Shared.Data;
using OpenTrafficDB.App.Middleware;
using OpenTrafficDB.App.Services;
using OpenTrafficDB.Shared.Models;
using OpenTrafficDB.Shared.Services;

namespace OpenTrafficDB;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ── Database ────────────────────────────────────────────────────────────
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
                o => o.UseNetTopologySuite()));

        // Read-replica context — falls back to the primary connection when
        // ConnectionStrings:ReadConnection is not configured.
        builder.Services.AddDbContext<ReadOnlyAppDbContext>(options =>
            options.UseNpgsql(
                builder.Configuration.GetConnectionString("ReadConnection")
                    ?? builder.Configuration.GetConnectionString("DefaultConnection"),
                o => o.UseNetTopologySuite()));

        // ── Redis distributed cache (tile cache) ────────────────────────────────
        var redisConn = builder.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            builder.Services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redisConn;
                o.InstanceName  = "otdb:";
            });
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        // ── Services ────────────────────────────────────────────────────────────
        builder.Services.AddSingleton<AbuseDetectionService>();
        builder.Services.AddSingleton<CryptoService>();
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddScoped<IEmailService, SmtpEmailService>();
        builder.Services.AddScoped<ApiKeyService>();

        // Map-matching, aggregation, tile build, routing — all in-process.
        builder.Services.AddScoped<MapMatchingService>();
        builder.Services.AddScoped<TrafficAggregationService>();
        builder.Services.AddScoped<TrafficTileService>();
        builder.Services.AddScoped<HazardTileService>();
        builder.Services.AddScoped<RoutingService>();
        builder.Services.AddSingleton<SpeedValidationService>();
        builder.Services.AddSingleton<HazardValidationService>();

        builder.Services.AddSingleton<StatsHistoryService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<StatsHistoryService>());

        // Recurring in-process background jobs.
        builder.Services.AddHostedService<AggregationService>();
        builder.Services.AddHostedService<PartitionMaintenanceService>();
        builder.Services.AddHostedService<WayGeometryBackgroundService>();

        // ── OpenTelemetry ────────────────────────────────────────────────────
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddPrometheusExporter())
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        // HTTP client for OpenTrafficRouting (map-matching + routing).
        builder.Services.AddHttpClient("otr", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                    ApiKeyAuthenticationHandler.SchemeName, options => { })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = "otdb_session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.LoginPath = "/Home/Login";
                options.AccessDeniedPath = "/Home/Login";
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("AdminOnly", policy =>
                policy.RequireClaim("role", "Admin"))
            .AddPolicy("ContributorOrAbove", policy =>
                policy.RequireClaim("role", "Contributor", "Admin"))
            .AddPolicy("WebAdminOnly", policy =>
            {
                policy.AuthenticationSchemes.Add(CookieAuthenticationDefaults.AuthenticationScheme);
                policy.RequireClaim("role", "Admin");
            });

        // ── Controllers & Swagger ───────────────────────────────────────────────
        builder.Services.AddControllersWithViews().AddRazorRuntimeCompilation();

        builder.Services.AddHealthChecks()
            .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!);

        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "OpenTrafficDB API",
                Version = "v1",
                Description = """
                    **OpenTrafficDB** is a community-driven traffic speed database.

                    Contributors submit anonymized GPS speed observations which are
                    server-side map-matched to OSM way IDs and aggregated into
                    predicted traffic exports.

                    **Machine authentication flow:**
                    1. Register at `/Home/Register` — creates an account and sends email verification
                    2. After verifying email, log in and create an API key on your profile page
                    3. Include the API key in every request: `Authorization: Bearer {api-key}`

                    **License:** Non-Commercial Community License
                    """,
                Contact = new OpenApiContact { Name = "OpenTrafficDB Community" }
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "API key from your profile page. Format: `Bearer <your-api-key>`",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
                    },
                    []
                }
            });

            var xmlPath = Path.Combine(AppContext.BaseDirectory, "OpenTrafficDB.xml");
            if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);
        });

        // ── Forwarded headers (for reverse proxy / Docker) ──────────────────────
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // ── CORS ────────────────────────────────────────────────────────────────
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            options.AddPolicy("Tiles", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        });

        var app = builder.Build();

        // ── Database creation ────────────────────────────────────────────────────
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();

            // Seed in-memory blocked IP cache from DB
            var abuse = app.Services.GetRequiredService<AbuseDetectionService>();
            await abuse.SeedFromDbAsync();

            // Seed default admin account if no Admin user exists
            if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
            {
                var cryptoSvc = scope.ServiceProvider.GetRequiredService<CryptoService>();
                var pwdSvc    = scope.ServiceProvider.GetRequiredService<PasswordService>();
                var logger    = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

                var rawPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');

                var admin = new User
                {
                    Name = "Admin",
                    EmailHash = cryptoSvc.HashEmail("admin@example.com"),
                    PasswordHash = pwdSvc.HashPassword(rawPassword),
                    IsEmailVerified = true,
                    Role = UserRole.Admin,
                    CreatedAt = DateTime.UtcNow
                };
                db.Users.Add(admin);
                await db.SaveChangesAsync();

                Console.WriteLine(
                    $"\n╔══════════════════════════════════════════════════════╗" +
                    $"\n║           DEFAULT ADMIN ACCOUNT CREATED              ║" +
                    $"\n║  Email:    admin@example.com                         ║" +
                    $"\n║  Password: {rawPassword,-42}║" +
                    $"\n║  Change this password after first login!             ║" +
                    $"\n╚══════════════════════════════════════════════════════╝\n");
            }
        }

        // ── Middleware pipeline ──────────────────────────────────────────────────
        app.UseForwardedHeaders();
        app.UseCors();
        app.UseMiddleware<IpBlockingMiddleware>();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Swagger always on (including production)
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "OpenTrafficDB API v1");
            c.RoutePrefix = "swagger";
            c.DocumentTitle = "OpenTrafficDB API";
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapStaticAssets();
        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}")
            .WithStaticAssets();

        app.MapHealthChecks("/health");
        app.MapPrometheusScrapingEndpoint("/metrics");

        await app.RunAsync();
    }
}
