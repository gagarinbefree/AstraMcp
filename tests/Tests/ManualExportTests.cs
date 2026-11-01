using System.Xml.Linq;
using AstraMcp.Core;
using AstraMcp.Infrastructure;

namespace AstraMcp.Tests;

public sealed class ManualExportTests
{
    [Theory]
    [InlineData("astra-manual.xml")]
    [InlineData("astra-coordinates.xml")]
    public void Accepts_native_integer_export_of_decimal_source(string resultFile)
    {
        var source = OrderXml.Validate(Read("order.xml"));

        Assert.Equal(new ResultInfo(1, 2, 0),
            OrderXml.ReadResult(Read(resultFile), source, source.Name));
    }

    [Theory]
    [InlineData("astra-manual.xml")]
    [InlineData("astra-coordinates.xml")]
    public void Accepts_native_map_structure_when_demand_matches_its_exported_integer_dimensions(string resultFile)
    {
        // This controlled baseline checks the map structure, not compatibility with the decimal source.
        var source = XDocument.Parse(Read("order.xml"));
        var part = source.Descendants("part").Single();
        part.SetAttributeValue("length", "600");
        part.SetAttributeValue("width", "400");
        var demand = OrderXml.Validate(source.ToString());

        var result = OrderXml.ReadResult(Read(resultFile), demand, demand.Name);

        Assert.Equal(new ResultInfo(1, 2, 0), result);
    }

    [Fact]
    public void Accepts_native_dot_probe_with_dimensions_rounded_up_and_down()
    {
        var demand = OrderXml.Validate(Read("precision-dot.xml"));

        Assert.Equal(new ResultInfo(1, 2, 0),
            OrderXml.ReadResult(Read("astra-dot.xml"), demand, demand.Name));
    }

    [Fact]
    public void Rejects_comma_import_that_truncated_instead_of_rounding_decimal_demand()
    {
        // The comma import is an Astra-only probe of the same physical decimal demand.
        var demand = OrderXml.Validate(Read("precision-dot.xml"));

        var error = Assert.Throws<InvalidDataException>(() =>
            OrderXml.ReadResult(Read("astra-comma.xml"), demand, "MCP precision comma"));

        Assert.Contains("D2", error.Message);
        Assert.Contains("600 x 400", error.Message);
        Assert.Contains("600.8 x 400.9", error.Message);
    }

    private static string Read(string name) => Artifacts.ReadXml(Path.Combine(AppContext.BaseDirectory, "TestData", name));
}
