using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Erp.Api.IntegrationTests;

/// <summary>
/// Spins up real Postgres and Redis in Docker, then boots the API in-memory against them.
/// The API applies the EF Core migrations + demo seed on startup (code-first, same as production).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("erp").WithUsername("erp_app").WithPassword("erp_app_dev")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _redis.StartAsync());
        // Set before the host is built so Program.cs sees them during startup
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _pg.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", _redis.GetConnectionString());
        Environment.SetEnvironmentVariable("JWT_SECRET", "integration-test-secret-0123456789-abcdefghij");
        Environment.SetEnvironmentVariable("RateLimit__AuthPerMinute", "1000");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
        Environment.SetEnvironmentVariable("Database__SeedDemoData", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _pg.DisposeAsync();
        await _redis.DisposeAsync();
        await base.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory> { public const string Name = "api"; }
