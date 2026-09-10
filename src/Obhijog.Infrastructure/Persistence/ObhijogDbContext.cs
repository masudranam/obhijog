using Microsoft.EntityFrameworkCore;

namespace Obhijog.Infrastructure.Persistence;

/// <summary>
/// The application database context. It holds no entities yet — the data model of
/// SPEC.md §8 lands in M2. It exists at M1 for two reasons: <c>/health/ready</c> needs
/// a database to check, and creating it now means M2's first migration is generated
/// with the snake_case convention of D11 already in force rather than retrofitted.
/// </summary>
public class ObhijogDbContext(DbContextOptions<ObhijogDbContext> options) : DbContext(options)
{
}
