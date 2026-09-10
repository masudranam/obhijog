using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Obhijog.Domain.Auth;
using Obhijog.Infrastructure.Identity;

namespace Obhijog.Infrastructure.Persistence.Configurations;

/// <summary>SPEC.md §10.2.</summary>
public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    /// <summary>Hex-encoded SHA-256.</summary>
    private const int HashLength = 64;

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(HashLength);
        builder.Property(t => t.ReplacedByHash).HasMaxLength(HashLength);

        // Every refresh is a lookup by hash, and two tokens sharing one is a bug worth a
        // constraint violation rather than an ambiguous match.
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Revoking a family walks every token a user owns.
        builder.HasIndex(t => new { t.UserId, t.ExpiresAt });

        // Deleting a user takes their tokens with them: unlike a complaint or an audit row,
        // a refresh token has no value once its owner is gone.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
