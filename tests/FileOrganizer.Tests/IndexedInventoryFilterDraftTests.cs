using FileOrganizer.Application;
using FileOrganizer.Desktop;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventoryFilterDraftTests
{
    [Fact]
    public void ExactBoundsAndUtcCalendarDaysArePreserved()
    {
        var filter = new InventoryFilterDraft(" %_\\ ", MinBytes: "0", MaxBytes: "9223372036854775807",
            ModifiedFrom: "2024-02-29", ModifiedThrough: "9999-12-31",
            CaseMode: IndexedInventoryCaseMode.MatchCase, Metadata: IndexedInventoryMetadataMode.Available, Extension: ".tar.gz").Parse();
        Assert.Equal(" %_\\ ", filter.Text);
        Assert.Equal(0L, filter.MinBytes);
        Assert.Equal(long.MaxValue, filter.MaxBytes);
        Assert.Equal(new DateOnly(2024, 2, 29), filter.ModifiedFrom);
        Assert.Equal(new DateOnly(9999, 12, 31), filter.ModifiedThrough);
        Assert.Equal(IndexedInventoryCaseMode.MatchCase, filter.CaseMode);
        Assert.Equal(IndexedInventoryMetadataMode.Available, filter.Metadata);
        Assert.Equal(".tar.gz", filter.Extension);
    }

    [Theory]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1,000")]
    [InlineData("١")]
    public void ByteDraftRejectsAnythingExceptAsciiWholeNonnegativeInt64(string value)
        => Assert.Throws<InventoryIndexException>(() => new InventoryFilterDraft(MinBytes: value).Parse());

    [Theory]
    [InlineData("2024-2-29")]
    [InlineData("2023-02-29")]
    [InlineData("2024-01-01T00:00:00Z")]
    [InlineData(" 2024-01-01")]
    public void DateDraftRequiresExactUtcCalendarDate(string value)
        => Assert.Throws<InventoryIndexException>(() => new InventoryFilterDraft(ModifiedFrom: value).Parse());

    [Fact]
    public void ReversedBoundsAreRejected()
    {
        Assert.Throws<InventoryIndexException>(() => new InventoryFilterDraft(MinBytes: "2", MaxBytes: "1").Parse());
        Assert.Throws<InventoryIndexException>(() => new InventoryFilterDraft(ModifiedFrom: "2024-02-02", ModifiedThrough: "2024-02-01").Parse());
        Assert.Equal(new IndexedInventoryFilter(), new InventoryFilterDraft().Parse());
    }
}
