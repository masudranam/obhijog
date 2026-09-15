using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Departments;
using Obhijog.Domain.Sla;
using Obhijog.Domain.Users;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Seeding;

/// <summary>
/// SPEC.md §8.11 and §14 F2.
///
/// Idempotent means: match on natural keys — <c>Department.Code</c>,
/// <c>ComplaintCategory.Name</c>, <c>User.Email</c> — insert only what is missing, and
/// leave identical row counts when run twice. It runs **after** migrations, so it never
/// calls <c>EnsureCreated</c> or <c>EnsureDeleted</c>.
/// </summary>
public class DatabaseSeeder(
    ObhijogDbContext db,
    UserManager<User> userManager,
    RoleManager<Role> roleManager,
    TimeProvider timeProvider,
    ILogger<DatabaseSeeder> logger)
{
    /// <summary>The environment variable holding the shared development password.</summary>
    public const string PasswordVariable = "SEED_PASSWORD";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Read it first and fail before writing anything. A seeder that creates departments
        // and then dies on the users leaves a half-populated database that the next run has
        // to reason about.
        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{PasswordVariable} is not set. Seeding is refused rather than planting a " +
                "known default password. Set it in the environment and run again. See SPEC.md §8.11.");
        }

        var departments = await SeedDepartmentsAsync(cancellationToken);
        await SeedCategoriesAsync(departments, cancellationToken);
        await SeedRolesAsync();
        await SeedUsersAsync(departments, password, cancellationToken);
        await SeedOverdueComplaintsAsync(cancellationToken);
    }

    private async Task<Dictionary<string, Department>> SeedDepartmentsAsync(
        CancellationToken cancellationToken)
    {
        var existing = await db.Departments.ToDictionaryAsync(d => d.Code, cancellationToken);

        foreach (var seed in SeedData.Departments)
        {
            if (existing.ContainsKey(seed.Code))
            {
                continue;
            }

            var department = new Department
            {
                Id = Guid.CreateVersion7(),
                Code = seed.Code,
                Name = seed.Name,
                IsActive = true,
            };

            db.Departments.Add(department);
            existing[seed.Code] = department;
            logger.LogInformation("Seeding department {Code}", seed.Code);
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    private async Task SeedCategoriesAsync(
        Dictionary<string, Department> departments,
        CancellationToken cancellationToken)
    {
        var existing = await db.ComplaintCategories
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);

        var known = existing.ToHashSet();

        foreach (var seed in SeedData.Categories)
        {
            if (known.Contains(seed.Name))
            {
                continue;
            }

            db.ComplaintCategories.Add(new ComplaintCategory
            {
                Id = Guid.CreateVersion7(),
                Name = seed.Name,
                DepartmentId = departments[seed.DepartmentCode].Id,
                SlaHours = seed.SlaHours,
                DefaultPriority = seed.DefaultPriority,
                IsActive = true,
            });

            logger.LogInformation("Seeding category {Name}", seed.Name);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in Enum.GetNames<UserRole>())
        {
            if (await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new Role
            {
                Id = Guid.CreateVersion7(),
                Name = role,
            });

            Ensure(result, $"create role '{role}'");
            logger.LogInformation("Seeding role {Role}", role);
        }
    }

    private async Task SeedUsersAsync(
        Dictionary<string, Department> departments,
        string password,
        CancellationToken cancellationToken)
    {
        foreach (var seed in SeedData.Users())
        {
            if (await userManager.FindByEmailAsync(seed.Email) is not null)
            {
                continue;
            }

            var user = new User
            {
                Id = Guid.CreateVersion7(),
                UserName = seed.Email,
                Email = seed.Email,
                EmailConfirmed = true,
                FullName = seed.FullName,
                Role = seed.Role,
                DepartmentId = seed.DepartmentCode is null
                    ? null
                    : departments[seed.DepartmentCode].Id,
                IsActive = true,
                CreatedAt = timeProvider.GetUtcNow(),
            };

            Ensure(await userManager.CreateAsync(user, password), $"create user '{seed.Email}'");

            // The Identity role table and User.Role are two copies of one fact (§8.1); they
            // are written together or the queryable copy lies.
            Ensure(
                await userManager.AddToRoleAsync(user, seed.Role.ToString()),
                $"add user '{seed.Email}' to role '{seed.Role}'");

            logger.LogInformation("Seeding user {Email} as {Role}", seed.Email, seed.Role);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The three deliberately overdue complaints of §8.11, so the escalation ladder is
    /// demonstrable the moment the API starts rather than after waiting out a real SLA window.
    ///
    /// <b>Computed backwards from the clock, never from a fixed date.</b> Each seed says how
    /// far through its window it should be *now*, and <c>CreatedAt</c> is derived from that
    /// against <c>TimeProvider.GetUtcNow()</c> — so a database seeded last month is still
    /// exactly as overdue today as it was then.
    ///
    /// Idempotent on the title, the only stable natural key a complaint has: the reference
    /// number comes from a sequence and is different on every run, so matching on it would
    /// make this method insert three more complaints every time it ran.
    /// </summary>
    private async Task SeedOverdueComplaintsAsync(CancellationToken cancellationToken)
    {
        var titles = SeedData.OverdueComplaints.Select(c => c.Title).ToArray();

        var existing = await db.Complaints
            .Where(c => titles.Contains(c.Title))
            .Select(c => c.Title)
            .ToListAsync(cancellationToken);

        var known = existing.ToHashSet();
        var now = timeProvider.GetUtcNow();

        foreach (var seed in SeedData.OverdueComplaints)
        {
            if (known.Contains(seed.Title))
            {
                continue;
            }

            var category = await db.ComplaintCategories
                .SingleOrDefaultAsync(c => c.Name == seed.CategoryName, cancellationToken);

            var citizen = await userManager.FindByEmailAsync(seed.CitizenEmail);

            if (category is null || citizen is null)
            {
                // Both come from the seed lists above and are created moments earlier, so
                // this only fires when §8.11's tables have drifted apart. Loudly, because a
                // silently skipped demo complaint looks exactly like a broken sweeper later.
                throw new InvalidOperationException(
                    $"Cannot seed '{seed.Title}': category '{seed.CategoryName}' or citizen "
                    + $"'{seed.CitizenEmail}' is missing. SPEC.md §8.11.");
            }

            // The window is the category's SLA, and CreatedAt is placed far enough back that
            // the complaint sits at exactly the elapsed percentage the seed asks for.
            var window = TimeSpan.FromHours(category.SlaHours);
            var createdAt = now - window * (seed.ElapsedPercent / 100d);

            db.Complaints.Add(new Complaint
            {
                Id = Guid.CreateVersion7(),
                ReferenceNumber = await NextReferenceNumberAsync(createdAt, cancellationToken),
                CitizenId = citizen.Id,
                CategoryId = category.Id,
                DepartmentId = category.DepartmentId,
                Priority = category.DefaultPriority,
                Title = seed.Title,
                Description = seed.Description,

                // Status is left at the entity's default of New. The sweeper's job is the
                // clock, not the workflow, and moving a seeded complaint to Assigned would
                // mean assigning a status outside the single write path (non-negotiable 3).
                Latitude = seed.Latitude,
                Longitude = seed.Longitude,
                AddressText = seed.AddressText,
                CreatedAt = createdAt,

                // Through SlaPolicy like every other complaint, so the seed cannot disagree
                // with what submission would have produced.
                SlaDueAt = SlaPolicy.DueAt(createdAt, category.SlaHours),
            });

            logger.LogInformation(
                "Seeding overdue complaint {Title} at {ElapsedPercent}% of its SLA window",
                seed.Title,
                seed.ElapsedPercent);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// §8.10, the same global sequence <c>ComplaintService</c> draws from — a seeded
    /// complaint gets a real reference number, not a hand-made one that could collide with
    /// a filed complaint later.
    /// </summary>
    private async Task<string> NextReferenceNumberAsync(
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        // The sequence name is a compile-time constant, never input.
        var next = await db.Database
            .SqlQueryRaw<long>(
                $"SELECT nextval('{ObhijogDbContext.ComplaintReferenceSequence}') AS \"Value\"")
            .SingleAsync(cancellationToken);

        return $"MC-{createdAt:yyyy}-{next:D6}";
    }

    /// <summary>
    /// Identity reports failure in a result object rather than by throwing. Swallowing that
    /// would leave a seeder that silently produces nothing.
    /// </summary>
    private static void Ensure(IdentityResult result, string what)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
        throw new InvalidOperationException($"Seeding failed to {what}. {errors}");
    }
}
