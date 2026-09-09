namespace Obhijog.Domain;

/// <summary>
/// Anchors <c>typeof(...).Assembly</c> so tests and future assembly scanning have a
/// stable handle on this project. The domain layer carries no types yet — entities and
/// enums arrive in M2, <c>ComplaintStateMachine</c> in M6 and <c>SlaPolicy</c> in M7
/// (SPEC.md §16.3).
/// </summary>
public sealed class DomainAssemblyMarker
{
}
