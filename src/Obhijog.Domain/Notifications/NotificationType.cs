namespace Obhijog.Domain.Notifications;

/// <summary>
/// SPEC.md §8.9. Stored as <c>varchar</c>, never a native PostgreSQL enum — adding a
/// value to a native enum needs a migration, which is exactly the friction to avoid here.
/// </summary>
public enum NotificationType
{
    ComplaintSubmitted,
    ComplaintAssigned,
    ComplaintResolved,
    SlaWarning,
    SlaBreached,
    SlaEscalatedLevel2,
    ComplaintReopened,
    ComplaintRejected,
}
