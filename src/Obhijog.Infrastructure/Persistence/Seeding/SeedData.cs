using Obhijog.Domain.Complaints;
using Obhijog.Domain.Users;

namespace Obhijog.Infrastructure.Persistence.Seeding;

/// <summary>
/// The seed rows of SPEC.md §8.11, as data rather than as code. Changing this list means
/// changing that table in the same commit.
/// </summary>
public static class SeedData
{
    public record DepartmentSeed(string Code, string Name);

    public record CategorySeed(
        string Name,
        string DepartmentCode,
        int SlaHours,
        ComplaintPriority DefaultPriority);

    public record UserSeed(string Email, string FullName, UserRole Role, string? DepartmentCode);

    public static readonly DepartmentSeed[] Departments =
    [
        new("WATER", "Water & Sewerage"),
        new("ELEC", "Street Lighting & Electrical"),
        new("SANI", "Sanitation & Waste"),
    ];

    public static readonly CategorySeed[] Categories =
    [
        new("Water leak — main", "WATER", 8, ComplaintPriority.Critical),
        new("Water leak — household supply", "WATER", 48, ComplaintPriority.Normal),
        new("No water supply", "WATER", 24, ComplaintPriority.High),
        new("Broken streetlight", "ELEC", 72, ComplaintPriority.Normal),
        new("Exposed electrical cable", "ELEC", 4, ComplaintPriority.Critical),
        new("Traffic signal fault", "ELEC", 12, ComplaintPriority.High),
        new("Uncollected garbage", "SANI", 24, ComplaintPriority.Normal),
        new("Illegal dumping", "SANI", 72, ComplaintPriority.Low),
        new("Blocked drain", "SANI", 24, ComplaintPriority.High),
    ];

    /// <summary>
    /// Per department: one DeptAdmin and two Staff, plus two Citizens. Emails follow
    /// <c>&lt;role&gt;&lt;n&gt;.&lt;deptcode&gt;@example.test</c> (§8.11), lowercased.
    /// </summary>
    public static IEnumerable<UserSeed> Users()
    {
        foreach (var department in Departments)
        {
            var code = department.Code.ToLowerInvariant();

            yield return new UserSeed(
                $"admin1.{code}@example.test",
                $"{department.Name} Administrator",
                UserRole.DeptAdmin,
                department.Code);

            for (var n = 1; n <= 2; n++)
            {
                yield return new UserSeed(
                    $"staff{n}.{code}@example.test",
                    $"{department.Name} Staff {n}",
                    UserRole.Staff,
                    department.Code);
            }
        }

        yield return new UserSeed("citizen1@example.test", "Aminul Islam", UserRole.Citizen, null);
        yield return new UserSeed("citizen2@example.test", "Rehana Begum", UserRole.Citizen, null);
    }
}
