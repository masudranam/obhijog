using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Obhijog.Domain.Departments;
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
