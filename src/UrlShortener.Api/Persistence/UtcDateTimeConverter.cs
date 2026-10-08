using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace UrlShortener.Api.Persistence;

/// <summary>
/// Stores DateTime values as UTC in datetime2 columns. Rejects non-UTC values on
/// write so local times cannot be persisted by accident, and marks values read
/// back from SQL Server as <see cref="DateTimeKind.Utc"/>.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => EnsureUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }

    public static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new ArgumentException(
                $"DateTime values must have Kind {DateTimeKind.Utc} but was {value.Kind}.",
                nameof(value));
}
