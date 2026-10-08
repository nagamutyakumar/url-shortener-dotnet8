using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.Api.Persistence.Configurations;

public sealed class ClickEventConfiguration : IEntityTypeConfiguration<ClickEvent>
{
    public const int VisitorHashLength = 32;
    public const int CountryCodeLength = 2;
    public const int BrowserMaxLength = 32;

    public void Configure(EntityTypeBuilder<ClickEvent> builder)
    {
        builder.ToTable("ClickEvents");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityColumn();

        builder.Property(x => x.ClickedAtUtc).HasColumnType("datetime2(3)");

        builder.Property(x => x.VisitorHash)
            .HasMaxLength(VisitorHashLength)
            .IsFixedLength();

        builder.Property(x => x.CountryCode)
            .HasMaxLength(CountryCodeLength)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(x => x.Browser)
            .HasMaxLength(BrowserMaxLength)
            .IsUnicode(false);

        // Leading UrlMappingId column also covers the foreign key.
        builder.HasIndex(x => new { x.UrlMappingId, x.ClickedAtUtc })
            .HasDatabaseName("IX_ClickEvents_UrlMappingId_ClickedAtUtc");
    }
}
