namespace Obhijog.Domain.Departments;

/// <summary>
/// SPEC.md §8.2. Seeded only — there is no department CRUD API (§22).
/// </summary>
public class Department
{
    public Guid Id { get; set; }

    /// <summary>Unique. Max 80 characters.</summary>
    public required string Name { get; set; }

    /// <summary>Unique, uppercase, e.g. <c>WATER</c>. Max 12 characters.</summary>
    public required string Code { get; set; }

    public bool IsActive { get; set; } = true;
}
