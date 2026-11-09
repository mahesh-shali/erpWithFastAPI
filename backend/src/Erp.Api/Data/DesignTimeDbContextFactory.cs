using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Api.Data;

/// <summary>
/// Used only by the `dotnet ef` CLI (migrations add / script / database update).
/// Reads the connection string from ConnectionStrings__Postgres; `migrations add`
/// never connects, so the placeholder is fine for generating migrations.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ErpDbContext>
{
    public ErpDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
                 ?? "Host=localhost;Port=5432;Database=erp;Username=erp_app;Password=erp_app_dev";
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseNpgsql(cs)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ErpDbContext(options, new FixedTenantContext(Guid.Empty));
    }
}
