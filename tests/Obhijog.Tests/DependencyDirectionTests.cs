using Obhijog.Domain;
using Xunit;

namespace Obhijog.Tests;

/// <summary>
/// SPEC.md §6 and CLAUDE.md non-negotiable 7: the dependency direction is one-way,
/// <c>Api → Infrastructure → Domain</c>, and the domain layer references nothing.
///
/// This is the one test M1 can carry. The four suites of SPEC.md §18 all cover logic
/// that does not exist yet — there is no state machine, no SLA policy and no scoping
/// seam until M2 onwards — and `dotnet test` exits non-zero when it discovers no tests
/// at all, so the Tests project cannot ship empty. Guarding the layering invariant is
/// the cheapest honest thing to put here, and it fails the moment someone adds an EF
/// Core or ASP.NET dependency to Domain.
/// </summary>
public class DependencyDirectionTests
{
    /// <summary>
    /// Assemblies whose presence in Domain would mean persistence or transport concerns
    /// have leaked into the layer that must stay a pure function over values.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.DependencyInjection",
        "Npgsql",
        "Azure.",
    ];

    [Fact]
    public void DomainReferencesNoPersistenceOrTransportAssembly()
    {
        var domain = typeof(DomainAssemblyMarker).Assembly;

        var violations = domain.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }
}
