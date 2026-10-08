using UrlShortener.Api.Persistence;

namespace UrlShortener.UnitTests.Persistence;

public class UtcDateTimeConverterTests
{
    private readonly UtcDateTimeConverter _converter = new();

    [Fact]
    public void ToProvider_AcceptsUtcValues()
    {
        var value = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(value, _converter.ConvertToProvider(value));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ToProvider_RejectsNonUtcValues(DateTimeKind kind)
    {
        var value = new DateTime(2026, 10, 7, 12, 0, 0, kind);

        Assert.Throws<ArgumentException>(() => _converter.ConvertToProvider(value));
    }

    [Fact]
    public void FromProvider_MarksValuesAsUtc()
    {
        var stored = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Unspecified);

        var result = (DateTime)_converter.ConvertFromProvider(stored)!;

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(stored.Ticks, result.Ticks);
    }
}
