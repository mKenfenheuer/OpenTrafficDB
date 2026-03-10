using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTrafficDB.Data;
using OpenTrafficDB.Middleware;
using OpenTrafficDB.Models;
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
        builder.Services.AddSingleton<JwtService>();
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddScoped<IEmailService, SmtpEmailService>();
        builder.Services.AddScoped<ApiKeyService>();
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
                    ValidateIssuer           = true,
                    ValidateAudience         = true,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer              = builder.Configuration["Jwt:Issuer"] ?? "OpenTrafficDB",
                    ValidAudience            = builder.Configuration["Jwt:Audience"] ?? "OpenTrafficDB",
                    IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew                = TimeSpan.FromSeconds(30)
                };

                // Reject the token if the caller's IP no longer matches the IP it was issued for.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var jwtSvc      = context.HttpContext.RequestServices.GetRequiredService<JwtService>();
                        var ip          = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                        var tokenIpHash = context.Principal?.FindFirstValue("ipHash");

                        if (tokenIpHash != null && jwtSvc.HashIp(ip) != tokenIpHash)
                            context.Fail("Token is not valid for this IP address.");

                        return Task.CompletedTask;
                    }
                };
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name     = "otdb_session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite     = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration   = true;
                options.ExpireTimeSpan      = TimeSpan.FromHours(12);
                // Return 401 JSON instead of redirecting for API calls
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
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
        builder.Services.AddControllers();

        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title   = "OpenTrafficDB API",
                Version = "v1",
                Description = """
                    **OpenTrafficDB** is a community-driven traffic speed database.

                    Contributors submit anonymized GPS speed observations which are
                    server-side map-matched to Valhalla edge IDs and aggregated into
                    Valhalla-compatible predicted traffic exports.

                    **Machine authentication flow:**
                    1. Register at `/register.html` — creates an account and sends email verification
                    2. After verifying email, log in at `/` and create an API key on your profile page
                    3. `POST /api/v1/auth/token` — exchange API key for a short-lived JWT (IP-bound)
                    4. Include the JWT in every request: `Authorization: Bearer {token}`

                    **Roles:** User → Contributor (auto, after threshold observations) → Admin (manual)

                    **License:** Non-Commercial Community License
                    """,
                Contact = new OpenApiContact { Name = "OpenTrafficDB Community" }
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT access token. Enter: Bearer {token}",
                Name        = "Authorization",
                In          = ParameterLocation.Header,
                Type        = SecuritySchemeType.Http,
                Scheme      = "Bearer",
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

            // Seed default admin account if no Admin user exists
            if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
            {
                var jwtSvc  = scope.ServiceProvider.GetRequiredService<JwtService>();
                var pwdSvc  = scope.ServiceProvider.GetRequiredService<PasswordService>();
                var logger  = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

                var rawPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');

                var admin = new User
                {
                    Name               = "Admin",
                    EmailHash          = jwtSvc.HashEmail("admin@example.com"),
                    PasswordHash       = pwdSvc.HashPassword(rawPassword),
                    IsEmailVerified    = true,
                    Role               = UserRole.Admin,
                    CreatedAt          = DateTime.UtcNow
                };
                db.Users.Add(admin);
                await db.SaveChangesAsync();

                Console.WriteLine(
                    $"\n╔══════════════════════════════════════════════════════╗" +
                    $"\n║           DEFAULT ADMIN ACCOUNT CREATED              ║" +
                    $"\n║  Email:    admin@example.com                         ║" +
                    $"\n║  Password: {rawPassword,-40}║" +
                    $"\n║  Change this password after first login!             ║" +
                    $"\n╚══════════════════════════════════════════════════════╝\n");
            }
        }

        // ── Middleware pipeline ──────────────────────────────────────────────────
        app.UseForwardedHeaders();
        app.UseCors();
        app.UseMiddleware<IpBlockingMiddleware>();

        // Serve wwwroot static files; index.html is served at /
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Swagger always on (including production)
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "OpenTrafficDB API v1");
            c.RoutePrefix    = "swagger";
            c.DocumentTitle  = "OpenTrafficDB API";
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        await app.RunAsync();
    }
}
