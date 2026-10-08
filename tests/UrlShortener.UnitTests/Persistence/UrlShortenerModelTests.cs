using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Persistence.Entities;

namespace UrlShortener.UnitTests.Persistence;

// Inspects the EF Core model only; building the model never opens a SQL Server connection.
public class UrlShortenerModelTests
{
    private readonly IModel _model;

    public UrlShortenerModelTests()
    {
        var options = new DbContextOptionsBuilder<UrlShortenerDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options;

        using var context = new UrlShortenerDbContext(options, TimeProvider.System);
        _model = context.GetService<IDesignTimeModel>().Model;
    }

    private IEntityType UrlMappingType => _model.FindEntityType(typeof(UrlMapping))!;

    private IEntityType ClickEventType => _model.FindEntityType(typeof(ClickEvent))!;

    [Fact]
    public void ShortCode_IsChar7WithBinaryCollation()
    {
        var property = UrlMappingType.FindProperty(nameof(UrlMapping.ShortCode))!;

        Assert.Equal("char(7)", property.GetColumnType());
        Assert.Equal("Latin1_General_100_BIN2", property.GetCollation());
        Assert.False(property.IsNullable);
    }

    [Fact]
    public void ShortCode_HasUniqueIndex()
    {
        var index = Assert.Single(
            UrlMappingType.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(UrlMapping.ShortCode) }));

        Assert.True(index.IsUnique);
        Assert.Equal("UX_UrlMappings_ShortCode", index.GetDatabaseName());
    }

    [Fact]
    public void OriginalUrl_IsNvarchar2048()
    {
        var property = UrlMappingType.FindProperty(nameof(UrlMapping.OriginalUrl))!;

        Assert.Equal("nvarchar(2048)", property.GetColumnType());
        Assert.False(property.IsNullable);
    }

    [Fact]
    public void OriginalUrlHash_IsBinary32WithNonUniqueIndex()
    {
        var property = UrlMappingType.FindProperty(nameof(UrlMapping.OriginalUrlHash))!;
        var index = Assert.Single(
            UrlMappingType.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(UrlMapping.OriginalUrlHash) }));

        Assert.Equal("binary(32)", property.GetColumnType());
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void RowVersion_IsConcurrencyToken()
    {
        var property = UrlMappingType.FindProperty(nameof(UrlMapping.RowVersion))!;

        Assert.Equal("rowversion", property.GetColumnType());
        Assert.True(property.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
    }

    [Fact]
    public void ClickEvents_RequireUrlMapping_WithRestrictDelete()
    {
        var foreignKey = Assert.Single(ClickEventType.GetForeignKeys());

        Assert.Equal(typeof(UrlMapping), foreignKey.PrincipalEntityType.ClrType);
        Assert.True(foreignKey.IsRequired);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void ClickEvents_HaveSingleCompositeIndex_OnUrlMappingIdAndClickedAt()
    {
        var index = Assert.Single(ClickEventType.GetIndexes());

        Assert.Equal(
            new[] { nameof(ClickEvent.UrlMappingId), nameof(ClickEvent.ClickedAtUtc) },
            index.Properties.Select(p => p.Name));
        Assert.False(index.IsUnique);
    }

    [Fact]
    public void ClickEvent_OptionalColumns_AreNullable()
    {
        Assert.True(ClickEventType.FindProperty(nameof(ClickEvent.VisitorHash))!.IsNullable);
        Assert.True(ClickEventType.FindProperty(nameof(ClickEvent.CountryCode))!.IsNullable);
        Assert.True(ClickEventType.FindProperty(nameof(ClickEvent.Browser))!.IsNullable);
        Assert.Equal("binary(32)", ClickEventType.FindProperty(nameof(ClickEvent.VisitorHash))!.GetColumnType());
        Assert.Equal("char(2)", ClickEventType.FindProperty(nameof(ClickEvent.CountryCode))!.GetColumnType());
    }

    [Theory]
    [InlineData(typeof(UrlMapping), nameof(UrlMapping.CreatedAtUtc))]
    [InlineData(typeof(UrlMapping), nameof(UrlMapping.UpdatedAtUtc))]
    [InlineData(typeof(UrlMapping), nameof(UrlMapping.ExpiresAtUtc))]
    [InlineData(typeof(ClickEvent), nameof(ClickEvent.ClickedAtUtc))]
    public void DateTimeProperties_UseUtcConverter(Type entityType, string propertyName)
    {
        var property = _model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        Assert.IsType<UtcDateTimeConverter>(property.GetValueConverter());
        Assert.Equal("datetime2(3)", property.GetColumnType());
    }
}
