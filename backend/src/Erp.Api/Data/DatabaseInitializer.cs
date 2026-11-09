using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Data;

/// <summary>
/// Applies EF Core migrations, sets the AI role's password, and (optionally) seeds demo data.
/// Run it once per deployment, before the API starts:
///   dotnet Erp.Api.dll --migrate [--seed]
/// or set Database:MigrateOnStartup=true for local development.
/// </summary>
public static class DatabaseInitializer
{
    public static readonly Guid DemoOrgId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherOrgId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public const string DemoAdminEmail = "admin@demo.com";
    public const string DemoAdminPassword = "Admin@123";

    public static async Task RunAsync(IServiceProvider services, bool seed, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));
        var options = sp.GetRequiredService<DbContextOptions<ErpDbContext>>();
        var cfg = sp.GetRequiredService<IConfiguration>();

        await using (var db = new ErpDbContext(options, new FixedTenantContext(Guid.Empty)))
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            log.LogInformation("Applying {Count} pending migration(s): {Names}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);

            await ConfigureAiLoginAsync(db, cfg, log, ct);
        }

        if (seed) await SeedDemoDataAsync(options, sp.GetRequiredService<IPasswordHasher>(), log, ct);
        await BootstrapOwnerAsync(options, cfg, sp.GetRequiredService<IPasswordHasher>(), log, ct);
    }

    /// <summary>
    /// Production has no demo data and (by default) no public sign-up, so the first organization and its
    /// SuperAdmin are created here from configuration (secrets), e.g. in the deploy workflow:
    ///   Bootstrap__OrganizationName, Bootstrap__Email, Bootstrap__Password, Bootstrap__FullName
    /// Idempotent: does nothing if that email already exists. Remove the secrets after the first run.
    /// </summary>
    private static async Task BootstrapOwnerAsync(DbContextOptions<ErpDbContext> options, IConfiguration cfg,
        IPasswordHasher hasher, ILogger log, CancellationToken ct)
    {
        var email = cfg["Bootstrap:Email"]?.Trim().ToLowerInvariant();
        var password = cfg["Bootstrap:Password"];
        var orgName = cfg["Bootstrap:OrganizationName"]?.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(orgName)) return;
        if (password.Length < 10)
            throw new InvalidOperationException("Bootstrap:Password must be at least 10 characters.");

        await using var db = new ErpDbContext(options, new FixedTenantContext(Guid.Empty));
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            log.LogInformation("Bootstrap owner {Email} already exists; nothing to do", email);
            return;
        }
        var org = new Organization { Id = Guid.NewGuid(), Name = orgName };
        db.Organizations.Add(org);
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), OrganizationId = org.Id, Email = email, PasswordHash = hasher.Hash(password),
            FullName = cfg["Bootstrap:FullName"]?.Trim() is { Length: > 0 } n ? n : "Owner", RoleId = Roles.SuperAdminId,
        });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Bootstrapped organization {Org} with SuperAdmin {Email}", orgName, email);
    }

    /// <summary>
    /// The migration creates <c>erp_ai_ro</c> (NOLOGIN) and gives it the AI's SELECT grants in this database.
    /// Passwords never live in source; they come from AI_DB_USER / AI_DB_PASSWORD at deploy time.
    ///
    /// Postgres roles are server-wide, not per database. When several environments share one server
    /// (e.g. dev = defaultdb, prod = erp_prod on the same Aiven service), each environment gets its own
    /// login role (AI_DB_USER, e.g. erp_ai_ro_dev / erp_ai_ro_prod) that inherits erp_ai_ro's grants,
    /// and CONNECT to this database is restricted so one environment's AI user can't reach another's data.
    /// With AI_DB_USER=erp_ai_ro (single-environment server, e.g. local Docker) erp_ai_ro logs in directly.
    /// </summary>
    private static async Task ConfigureAiLoginAsync(ErpDbContext db, IConfiguration cfg, ILogger log, CancellationToken ct)
    {
        const string group = "erp_ai_ro";
        var user = cfg["AI_DB_USER"] ?? cfg["Database:AiRoleUser"] ?? group;
        var password = cfg["AI_DB_PASSWORD"] ?? cfg["Database:AiRolePassword"];
        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z_][a-z0-9_]{0,62}$"))
            throw new InvalidOperationException($"AI_DB_USER '{user}' is not a valid lower-case Postgres role name.");
        if (string.IsNullOrEmpty(password))
        {
            log.LogWarning("AI_DB_PASSWORD not set: {User} gets no login and the AI service cannot connect", user);
            return;
        }

        var pw = password.Replace("'", "''");
        // Role/database names are validated identifiers; ALTER ROLE cannot take bind parameters.
#pragma warning disable EF1002
        if (user == group)
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER ROLE {group} WITH LOGIN PASSWORD '{pw}'", ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync($$"""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{user}}') THEN
                    CREATE ROLE {{user}} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
                  END IF;
                END $$;
                ALTER ROLE {{user}} WITH LOGIN PASSWORD '{{pw}}';
                GRANT {{group}} TO {{user}};
                ALTER ROLE {{user}} SET statement_timeout = '5s';
                ALTER ROLE {{user}} SET default_transaction_read_only = on;
                ALTER ROLE {{user}} SET idle_in_transaction_session_timeout = '10s';
                ALTER ROLE {{group}} NOLOGIN PASSWORD NULL;
                """, ct);

            // Only this environment's users may connect to this database.
            var dbName = db.Database.GetDbConnection().Database.Replace("\"", "\"\"");
            var owns = await db.Database.SqlQueryRaw<bool>(
                "SELECT pg_get_userbyid(datdba) = current_user AS \"Value\" FROM pg_database WHERE datname = current_database()")
                .SingleAsync(ct);
            if (owns)
            {
                await db.Database.ExecuteSqlRawAsync($"""
                    REVOKE CONNECT ON DATABASE "{dbName}" FROM PUBLIC;
                    GRANT CONNECT ON DATABASE "{dbName}" TO current_user, {user};
                    """, ct);
            }
            else
            {
                log.LogWarning("Not the owner of database {Db}: could not restrict CONNECT to {User}", dbName, user);
            }
        }
#pragma warning restore EF1002
        log.LogInformation("AI login role {User} configured", user);
    }

    /// <summary>Deterministic demo data. Idempotent: does nothing if the demo org already exists.</summary>
    private static async Task SeedDemoDataAsync(
        DbContextOptions<ErpDbContext> options, IPasswordHasher hasher, ILogger log, CancellationToken ct)
    {
        // Tenant-owned rows get OrganizationId stamped from the context's tenant in SaveChangesAsync,
        // so each tenant's data is written through a context bound to that tenant.
        await using var demo = new ErpDbContext(options, new FixedTenantContext(DemoOrgId));
        if (await demo.Organizations.AnyAsync(o => o.Id == DemoOrgId, ct))
        {
            log.LogInformation("Demo data already present; skipping seed");
            return;
        }

        var rng = new Random(20240101);
        var now = DateTime.UtcNow;

        demo.Organizations.AddRange(
            new Organization { Id = DemoOrgId, Name = "Demo Corp" },
            new Organization { Id = OtherOrgId, Name = "Other Tenant Ltd" });
        // Hierarchy demo: SuperAdmin (org owner) > Admin > User
        demo.Users.AddRange(
            new User
            {
                Id = Guid.NewGuid(), OrganizationId = DemoOrgId, Email = DemoAdminEmail,
                PasswordHash = hasher.Hash(DemoAdminPassword), FullName = "Demo Admin", RoleId = Roles.SuperAdminId,
            },
            new User
            {
                Id = Guid.NewGuid(), OrganizationId = DemoOrgId, Email = "manager@demo.com",
                PasswordHash = hasher.Hash("Manager@123"), FullName = "Demo Manager", RoleId = Roles.AdminId,
            },
            new User
            {
                Id = Guid.NewGuid(), OrganizationId = DemoOrgId, Email = "staff@demo.com",
                PasswordHash = hasher.Hash("Staff@1234"), FullName = "Demo Staff", RoleId = Roles.UserId,
            });

        string[] cities = ["Bengaluru", "Mumbai", "Delhi", "Pune", "Chennai"];
        var customers = Enumerable.Range(1, 200).Select(g => new Customer
        {
            Id = Guid.NewGuid(), Name = $"Customer {g}", Email = $"customer{g}@example.com",
            City = cities[g % cities.Length], Country = "India",
        }).ToList();

        string[] categories = ["Electronics", "Furniture", "Stationery", "Apparel"];
        var products = Enumerable.Range(1, 100).Select(g => new Product
        {
            Id = Guid.NewGuid(), Sku = $"SKU-{g:0000}", Name = $"Product {g}",
            Category = categories[g % categories.Length],
            UnitPrice = Math.Round((decimal)(rng.NextDouble() * 9000 + 100), 2),
            StockQuantity = rng.Next(0, 501),
        }).ToList();

        // 5 orders per customer over the past year, 2 distinct products per order
        var orders = new List<Order>();
        foreach (var c in customers)
        for (var i = 0; i < 5; i++)
        {
            var items = products.OrderBy(_ => rng.Next()).Take(2).Select(p => new OrderItem
            {
                Id = Guid.NewGuid(), ProductId = p.Id, Quantity = rng.Next(1, 6), UnitPrice = p.UnitPrice,
            }).ToList();
            orders.Add(new Order
            {
                Id = Guid.NewGuid(), CustomerId = c.Id,
                Status = OrderStatus.All[rng.Next(OrderStatus.All.Length)],
                OrderedAt = now - TimeSpan.FromDays(rng.NextDouble() * 365),
                Items = items,
                TotalAmount = OrderService.CalculateTotal(items.Select(x => (x.Quantity, x.UnitPrice))),
            });
        }

        demo.Customers.AddRange(customers);
        demo.Products.AddRange(products);
        demo.Orders.AddRange(orders);
        await demo.SaveChangesAsync(ct);

        // One row in another tenant: integration tests use it to prove tenant isolation (RLS + EF filters)
        await using var other = new ErpDbContext(options, new FixedTenantContext(OtherOrgId));
        other.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(), Name = "SECRET OTHER TENANT CUSTOMER", City = "Paris", Country = "France",
        });
        await other.SaveChangesAsync(ct);

        log.LogInformation("Seeded demo data: {Customers} customers, {Products} products, {Orders} orders",
            customers.Count, products.Count, orders.Count);
    }
}
