using LiveStream.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveStream.TestSupport;

/// <summary>
/// A real relational database for tests, held in memory.
///
/// SQLite is used rather than EF's in-memory provider because tests need genuine relational
/// behaviour: foreign keys, unique constraints, and optimistic-concurrency failures. The production
/// schema is PostgreSQL and is verified separately against real migrations in
/// <c>LiveStream.DatabaseTests</c> (docs/decisions/0006-testing-and-database-providers.md).
///
/// The connection is kept open for the lifetime of the instance because an in-memory SQLite
/// database is discarded when its last connection closes.
/// </summary>
public sealed class SqliteTestDatabase : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public DbContextOptions<AppDbContext> Options { get; }

    /// <summary>
    /// The shared open connection. Hand this to <c>UseSqlite</c> when the context is built by a
    /// host's DI container rather than by <see cref="CreateContext"/>.
    /// </summary>
    public SqliteConnection Connection => _connection;

    /// <summary>Creates a fresh context, so tests can assert against state rather than a stale change tracker.</summary>
    public AppDbContext CreateContext() => new(Options);

    public void Dispose() => _connection.Dispose();

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
