using Microsoft.EntityFrameworkCore;
using Obhijog.Infrastructure.Persistence;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// A real PostgreSQL, per SPEC.md §18: the EF in-memory provider has no transactions, no
/// <c>xmin</c>, no unique-index enforcement and no <c>ILIKE</c>, so it would silently pass
/// the exact cases these suites exist to guarantee.
///
/// When no database is reachable the tests <b>skip with a visible message</b> rather than
/// passing quietly — §18 again. CI always has one (the api gate's service container), so a
/// skip locally never becomes a skip on the SHA that merges.
/// </summary>
public class PostgresFixture
{
    /// <summary>
    /// The same key the API reads, so a developer who can run the API can run these tests
    /// with no extra setup. SPEC.md §19.
    /// </summary>
    public const string ConnectionStringVariable = "ConnectionStrings__Postgres";

    public static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable(ConnectionStringVariable);

    public static string SkipReason =>
        $"{ConnectionStringVariable} is not set, so no PostgreSQL is reachable. "
        + "Start infra/docker-compose.yml and export it to run this suite (SPEC.md §18, §19).";

    private readonly Lock _gate = new();
    private bool _migrated;

    public ObhijogDbContext CreateContext()
    {
        if (ConnectionString is null)
        {
            throw new InvalidOperationException(SkipReason);
        }

        var options = new DbContextOptionsBuilder<ObhijogDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var context = new ObhijogDbContext(options);

        // Once per fixture, not once per context: concurrent Migrate() calls on the same
        // database deadlock on the history table.
        lock (_gate)
        {
            if (!_migrated)
            {
                context.Database.Migrate();
                _migrated = true;
            }
        }

        return context;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself, with a reason the runner prints, when
/// there is no database to talk to.
/// </summary>
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (PostgresFixture.ConnectionString is null)
        {
            Skip = PostgresFixture.SkipReason;
        }
    }
}

/// <summary>As <see cref="RequiresPostgresFactAttribute"/>, for a theory.</summary>
public sealed class RequiresPostgresTheoryAttribute : TheoryAttribute
{
    public RequiresPostgresTheoryAttribute()
    {
        if (PostgresFixture.ConnectionString is null)
        {
            Skip = PostgresFixture.SkipReason;
        }
    }
}
