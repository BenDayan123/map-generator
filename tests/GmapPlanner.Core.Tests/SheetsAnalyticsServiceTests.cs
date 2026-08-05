using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class SheetsAnalyticsServiceTests
{
    [Theory]
    [InlineData("1AbC_dEf-123", "1AbC_dEf-123")]                                                   // bare id
    [InlineData("  1AbC_dEf-123  ", "1AbC_dEf-123")]                                                // trimmed
    [InlineData("https://docs.google.com/spreadsheets/d/1AbC_dEf-123/edit#gid=0", "1AbC_dEf-123")] // full URL
    [InlineData("https://docs.google.com/spreadsheets/d/1AbC_dEf-123", "1AbC_dEf-123")]            // URL, no suffix
    [InlineData("", "")]
    public void SheetIdOf_extracts_id_from_bare_or_url(string raw, string expected) =>
        Assert.Equal(expected, SheetsAnalyticsService.SheetIdOf(raw));

    [Theory]
    [InlineData("", "id", false)]        // no service account
    [InlineData("{sa}", "", false)]      // no sheet
    [InlineData("{sa}", "  ", false)]    // blank sheet
    [InlineData("{sa}", "id", true)]
    public void IsConfigured_requires_both(string sa, string sheet, bool expected) =>
        Assert.Equal(expected, SheetsAnalyticsService.IsConfigured(sa, sheet));
}
