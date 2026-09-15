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

    /// <summary>
    /// One deliberately overdue complaint. SPEC.md §8.11, M7.
    /// </summary>
    /// <param name="Title">
    /// The natural key. Complaints have no business key of their own — the reference number
    /// is allocated from a sequence and differs every run — so idempotency matches on the
    /// title, which is why these three are worded distinctively enough not to collide with
    /// anything a citizen would file.
    /// </param>
    /// <param name="ElapsedPercent">
    /// How far through its SLA window the complaint should be **at the moment the seeder
    /// runs**. Its <c>CreatedAt</c> is computed backwards from
    /// <c>TimeProvider.GetUtcNow()</c> so it is still overdue whenever seeding happens,
    /// rather than at a fixed date that goes stale (§8.11).
    /// </param>
    public record OverdueComplaintSeed(
        string Title,
        string Description,
        string CategoryName,
        string CitizenEmail,
        double ElapsedPercent,
        decimal Latitude,
        decimal Longitude,
        string AddressText);

    /// <summary>
    /// The three rungs of §11.2, made demonstrable without waiting real hours: one past the
    /// 80% warning, one past the 100% breach, one past the 150% second escalation. A single
    /// sweep after seeding warns the first, breaches the second, and breaches *and*
    /// escalates the third.
    ///
    /// All three are left <c>New</c> and unassigned. That is not laziness about coverage —
    /// <c>complaint.Status = …</c> exists in exactly one file (CLAUDE.md non-negotiable 3)
    /// and a seeder that assigned its way to <c>InProgress</c> would either break that or
    /// need a signed-in user it does not have. Unassigned also exercises the recipient rule
    /// that is easiest to get wrong: with nobody holding the complaint, §11.2 sends the
    /// warning to every active Dept Admin instead.
    /// </summary>
    public static readonly OverdueComplaintSeed[] OverdueComplaints =
    [
        new(
            "Seeded demo — mains leak flooding the lane (past 80%)",
            "A mains pipe has been leaking into the lane since the early morning and the "
            + "water is now standing ankle-deep outside the shops.",
            "Water leak — main",
            "citizen1@example.test",
            ElapsedPercent: 90,
            23.7461m,
            90.3742m,
            "Lane 4, Dhanmondi"),

        new(
            "Seeded demo — drain blocked outside the school (past 100%)",
            "The storm drain at the school gate is completely blocked and the overflow is "
            + "crossing the footpath the children use.",
            "Blocked drain",
            "citizen1@example.test",
            ElapsedPercent: 120,
            23.7509m,
            90.3934m,
            "School Road, Kalabagan"),

        new(
            "Seeded demo — streetlight out on the main road (past 150%)",
            "The streetlight at the main road junction has been out for days and the "
            + "crossing is unlit after sunset.",
            "Broken streetlight",
            "citizen2@example.test",
            ElapsedPercent: 170,
            23.7806m,
            90.4074m,
            "Mohakhali junction"),
    ];
}
