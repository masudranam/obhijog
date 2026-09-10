using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Obhijog.Domain.Auth;
using Obhijog.Domain.Complaints;
using Obhijog.Domain.Departments;
using Obhijog.Domain.Notifications;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence;

/// <summary>
/// The application database context. SPEC.md §8.
///
/// Naming is snake_case globally via <c>UseSnakeCaseNamingConvention()</c>, configured
/// where the context is registered (D11) — never per property.
/// </summary>
public class ObhijogDbContext(DbContextOptions<ObhijogDbContext> options)
    : IdentityDbContext<User, Role, Guid>(options)
{
    /// <summary>
    /// The global, never-reset reference sequence of §8.10. Gaps are expected: an
    /// abandoned insert burns a number, because <c>nextval</c> is exempt from rollback
    /// by design. The reference is an identifier, not a count (D8).
    /// </summary>
    public const string ComplaintReferenceSequence = "complaint_reference_seq";

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<ComplaintCategory> ComplaintCategories => Set<ComplaintCategory>();

    public DbSet<Complaint> Complaints => Set<Complaint>();

    public DbSet<ComplaintStatusHistory> ComplaintStatusHistories => Set<ComplaintStatusHistory>();

    public DbSet<ComplaintComment> ComplaintComments => Set<ComplaintComment>();

    public DbSet<ComplaintAttachment> ComplaintAttachments => Set<ComplaintAttachment>();

    public DbSet<EscalationEvent> EscalationEvents => Set<EscalationEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasSequence<long>(ComplaintReferenceSequence).StartsAt(1).IncrementsBy(1);

        builder.ApplyConfigurationsFromAssembly(typeof(ObhijogDbContext).Assembly);
    }
}
