using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UrlShortener.Api.Persistence.Entities;
using UrlShortener.Api.ShortCodes;

namespace UrlShortener.Api.Persistence.Configurations;

public sealed class UrlMappingConfiguration : IEntityTypeConfiguration<UrlMapping>
{
    public const string ShortCodeCollation = "Latin1_General_100_BIN2";
    public const int OriginalUrlMaxLength = 2048;
    public const int OriginalUrlHashLength = 32;

    public void Configure(EntityTypeBuilder<UrlMapping> builder)
    {
        builder.ToTable("UrlMappings");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();

        // char(7) with a binary collation: uniqueness and lookups are case-sensitive.
        builder.Property(x => x.ShortCode)
            .HasMaxLength(ShortCodeFormat.Length)
            .IsFixedLength()
            .IsUnicode(false)
            .UseCollation(ShortCodeCollation)
            .IsRequired();

        builder.Property(x => x.OriginalUrl)
            .HasMaxLength(OriginalUrlMaxLength)
            .IsRequired();

        builder.Property(x => x.OriginalUrlHash)
            .HasMaxLength(OriginalUrlHashLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(x => x.CreatedAtUtc).HasColumnType("datetime2(3)").Metadata.SetAfterSaveBehavior(
            Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Throw);
        builder.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        builder.Property(x => x.ExpiresAtUtc).HasColumnType("datetime2(3)");

        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.ShortCode)
            .IsUnique()
            .HasDatabaseName("UX_UrlMappings_ShortCode");

        builder.HasIndex(x => x.OriginalUrlHash)
            .HasDatabaseName("IX_UrlMappings_OriginalUrlHash");

        // Restrict: links are deactivated rather than deleted, so click history survives.
        builder.HasMany(x => x.ClickEvents)
            .WithOne(x => x.UrlMapping)
            .HasForeignKey(x => x.UrlMappingId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
