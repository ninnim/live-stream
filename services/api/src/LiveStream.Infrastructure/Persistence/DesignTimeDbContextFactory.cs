using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LiveStream.Infrastructure.Persistence;

/// <summary>
/// Used only by the EF Core CLI when adding or scripting migrations. The connection string here is
/// never used to connect: migrations are generated from the model. Set <c>LIVESTREAM_MIGRATIONS_DSN</c>
/// when a command genuinely needs to reach a database (for example <c>database update</c>).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("LIVESTREAM_MIGRATIONS_DSN")
                               ?? "Host=localhost;Port=5432;Database=livestream;Username=livestream;Password=livestream";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .Options;

        return new AppDbContext(options);
    }
}
