using AstraMcp.Verification;

namespace AstraMcp.Tests;

public sealed class NativeGeometryTests
{
    private const string Source = """
        <data><data_order><list_materials><material wid_cut="3.2"><list_nesting_plans>
        <nesting_plan length="2800" width="2070" quantity="2" cut_length="999">
          <list_parts>
            <part length="600" width="400" x="5" y="5" />
            <part length="600" width="400" x="608.2" y="5" />
          </list_parts>
          <list_cuts><cut x1="0" y1="406.6" x2="2800" y2="406.6" /></list_cuts>
        </nesting_plan></list_nesting_plans></material></list_materials></data_order></data>
        """;

    [Fact]
    public void Measures_physical_copies_and_cut_lengths_without_trusting_cut_length()
    {
        var report = NativeGeometryVerifier.Verify(Source);
        Assert.Equal(new GeometryReport(4, 4, 2, 5600m), report);
        Assert.True(report.Complete);
    }

    [Theory]
    [InlineData("608.2", "600", "overlap")]
    [InlineData("608.2", "2500", "bounds")]
    [InlineData("608.2", "606", "kerf")]
    [InlineData("x1=\"0\"", "x1=\"2801\"", "bounds")]
    [InlineData("x1=\"0\"", "x1=\"-1\"", "bounds")]
    [InlineData("y2=\"406.6\"", "y2=\"407\"", "axis-aligned")]
    public void Rejects_geometrically_invalid_exports(string oldValue, string newValue, string error)
    {
        var exception = Assert.Throws<InvalidDataException>(() => NativeGeometryVerifier.Verify(Source.Replace(oldValue, newValue)));
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public void Reports_missing_coordinates_without_inventing_positions()
    {
        var report = NativeGeometryVerifier.Verify(Source.Replace("x=\"608.2\"", ""));
        Assert.Equal(4, report.PlacedParts);
        Assert.Equal(2, report.CheckedParts);
        Assert.False(report.Complete);
    }
}
