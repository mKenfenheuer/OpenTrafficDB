using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTrafficDB.Data;
using OpenTrafficDB.Middleware;
using OpenTrafficDB.Services;

namespace OpenTrafficDB;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Prevent ASP.NET Core from remapping JWT claim names (keep "sub" as "sub", not ClaimTypes.NameIdentifier)
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        var builder = WebApplication.CreateBuilder(args);

        // ── Database ────────────────────────────────────────────────────────────
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

        // ── Services ────────────────────────────────────────────────────────────
        builder.Services.AddSingleton<AbuseDetectionService>();
        builder.Services.AddScoped<JwtService>();
        builder.Services.AddScoped<SpeedValidationService>();
        builder.Services.AddScoped<TrafficAggregationService>();
        builder.Services.AddScoped<ValhallaMapMatchingService>();
        builder.Services.AddHostedService<AggregationBackgroundService>();

        // HTTP client for Valhalla map matching
        builder.Services.AddHttpClient("valhalla", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Authentication & Authorization ──────────────────────────────────────
        var jwtKey = builder.Configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key must be configured.");

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "OpenTrafficDB",
                    ValidAudience = builder.Configuration["Jwt:Audience"] ?? "OpenTrafficDB",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("AdminOnly", policy => policy.RequireClaim("isAdmin", "true"));

        // ── Controllers & Swagger ───────────────────────────────────────────────
        builder.Services.AddControllers();

        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "OpenTrafficDB API",
                Version = "v1",
                Description = """
                    **OpenTrafficDB** is a community-driven traffic speed database.

                    Contributors submit anonymized GPS speed observations which are
                    server-side map-matched to Valhalla edge IDs and aggregated into
                    Valhalla-compatible predicted traffic exports.

                    **Authentication flow:**
                    1. `POST /api/v1/auth/register` — receive a permanent API key
                    2. `POST /api/v1/auth/token` — exchange it for a short-lived JWT
                    3. Include the JWT in every request: `Authorization: Bearer {token}`

                    **License:** Non-Commercial Community License
                    """,
                Contact = new OpenApiContact { Name = "OpenTrafficDB Community" }
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT access token. Enter: Bearer {token}",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT"
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

            // Include XML doc comments
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
            options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        var app = builder.Build();

        // ── Database creation ────────────────────────────────────────────────────
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();

            // Seed in-memory blocked IP cache from DB
            var abuse = app.Services.GetRequiredService<AbuseDetectionService>();
            await abuse.SeedFromDbAsync();
        }

        // ── Middleware pipeline ──────────────────────────────────────────────────
        app.UseForwardedHeaders();
        app.UseCors();
        app.UseMiddleware<IpBlockingMiddleware>();

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
        app.MapControllers();

        // Redirect root to Swagger
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

        await app.RunAsync();
    }
}
