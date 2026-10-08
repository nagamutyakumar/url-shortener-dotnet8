using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.Api.Persistence;

public sealed class UrlShortenerDbContext : DbContext
{
    private readonly TimeProvider _timeProvider;

    public UrlShortenerDbContext(
        DbContextOptions<UrlShortenerDbContext> options,
        TimeProvider timeProvider)
        : base(options)
    {
        _timeProvider = timeProvider;
    }

    public DbSet<UrlMapping> UrlMappings => Set<UrlMapping>();

    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampUrlMappingTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampUrlMappingTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UrlShortenerDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime column is UTC: non-UTC writes throw, reads come back as Kind.Utc.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    private void StampUrlMappingTimestamps()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var entry in ChangeTracker.Entries<UrlMapping>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAtUtc = now;
                    entry.Entity.UpdatedAtUtc = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    break;
            }
        }
    }
}
