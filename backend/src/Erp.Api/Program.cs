using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- Options ----------
builder.Services.Configure<JwtOptions>(o =>
{
    o.Secret = cfg["JWT_SECRET"] ?? cfg["Jwt:Secret"] ?? throw new InvalidOperationException("JWT secret missing");
    o.Issuer = cfg["JWT_ISSUER"] ?? cfg["Jwt:Issuer"] ?? "erp-api";
    o.Audience = cfg["JWT_AUDIENCE"] ?? cfg["Jwt:Audience"] ?? "erp-clients";
    o.AccessTokenMinutes = cfg.GetValue("Jwt:AccessTokenMinutes", 15);
});
builder.Services.Configure<LoginSessionOptions>(cfg.GetSection("Auth"));
var jwtSecret = cfg["JWT_SECRET"] ?? cfg["Jwt:Secret"]!;
if (Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    throw new InvalidOperationException("JWT secret must be at least 32 bytes (HS256).");

// ---------- Production guard rails: fail fast on dev defaults ----------
if (builder.Environment.IsProduction())
{
    if (jwtSecret.StartsWith("change-me", StringComparison.Ordinal))
        throw new InvalidOperationException("Production is using the placeholder JWT secret. Set JWT_SECRET.");
    if (cfg.GetValue("Database:SeedDemoData", false) || args.Contains("--seed"))
        throw new InvalidOperationException("Demo seed data (known admin password) is not allowed in Production.");
    if (cfg.GetValue("Database:MigrateOnStartup", false))
        throw new InvalidOperationException("Run migrations as a one-off job (--migrate), not on every Production pod.");
}

// ---------- Data ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddDbContext<ErpDbContext>(o => o
    .UseNpgsql(cfg.GetConnectionString("Postgres"), npg => npg.EnableRetryOnFailure(3))
    .UseSnakeCaseNamingConvention());

var redis = cfg.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis))
    builder.Services.AddStackExchangeRedisCache(o => { o.Configuration = redis; o.InstanceName = "erp:"; });
else
    builder.Services.AddDistributedMemoryCache(); // local dev / tests

// ---------- App services ----------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IMasterService, MasterService>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider, PagePolicyProvider>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PageRequirementHandler>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IProductService, ProductService>();

// ---------- Auth ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = cfg["JWT_ISSUER"] ?? cfg["Jwt:Issuer"] ?? "erp-api",
            ValidateAudience = true, ValidAudience = cfg["JWT_AUDIENCE"] ?? cfg["Jwt:Audience"] ?? "erp-clients",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = AuthClaims.Role,
            NameClaimType = "sub",
        };
        // A valid signature isn't enough: the token's login session must still be active
        // (not logged out, revoked, or expired). Cached in Redis, see SessionService.IsActiveAsync.
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var sid = ctx.Principal?.FindFirstValue(AuthClaims.SessionId);
                var sessions = ctx.HttpContext.RequestServices.GetRequiredService<ISessionService>();
                if (!Guid.TryParse(sid, out var id) || !await sessions.IsActiveAsync(id, ctx.HttpContext.RequestAborted))
                    ctx.Fail("Session is no longer active.");
            }
        };
    });
builder.Services.AddAuthorization();

// ---------- Rate limiting (per user / per IP) ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetTokenBucketLimiter(
            ctx.User.FindFirstValue("sub") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 100, TokensPerPeriod = 50, ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0, AutoReplenishment = true
            }));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = cfg.GetValue("RateLimit:AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1)
        }));
});

// ---------- Web ----------
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddResponseCompression(o => { o.EnableForHttps = true; o.Providers.Add<BrotliCompressionProvider>(); });
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    // Each entry may itself be comma-separated (CORS_ORIGINS env var -> Cors__Origins__0)
    .WithOrigins((cfg.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
        .SelectMany(o => o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray())
    .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ERP API", Version = "v1" });
    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = [] });
});

var hc = builder.Services.AddHealthChecks();
if (!string.IsNullOrWhiteSpace(cfg.GetConnectionString("Postgres")))
    hc.AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: ["ready"]);
if (!string.IsNullOrWhiteSpace(redis)) hc.AddRedis(redis, tags: ["ready"]);

var app = builder.Build();

// ---------- Database (code-first) ----------
// `dotnet Erp.Api.dll --migrate [--seed]` applies EF Core migrations (and demo data), then exits.
// Use this as a one-off job before rolling out new API pods (docker-compose `migrator`, k8s Job).
var migrateOnly = args.Contains("--migrate");
var seed = args.Contains("--seed") || cfg.GetValue("Database:SeedDemoData", false);
if (migrateOnly || cfg.GetValue("Database:MigrateOnStartup", false))
{
    await DatabaseInitializer.RunAsync(app.Services, seed);
    if (migrateOnly) return;
}

var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
// The API is only reachable through nginx / the ingress (another container, not loopback), so trust its
// X-Forwarded-* headers. Without this, every client looks like nginx's IP (breaks per-IP rate limits and
// session IPs) and HTTPS isn't detected (refresh cookie wouldn't get the Secure flag).
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
// Number of proxies in front of the API: 1 for nginx; 2 for Netlify -> Render (both append to X-Forwarded-For).
forwarded.ForwardLimit = cfg.GetValue("Proxy:ForwardLimit", 1);
app.UseForwardedHeaders(forwarded);
app.UseExceptionHandler();
app.UseResponseCompression();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

app.Run();

public partial class Program; // for WebApplicationFactory in integration tests
