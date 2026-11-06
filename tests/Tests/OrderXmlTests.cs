using AstraMcp.Core;

namespace AstraMcp.Tests;

public sealed class OrderXmlTests
{
    internal const string Source = """
        <data><data_order name="Шкаф"><list_materials><material name="ДСП">
          <list_parts><part number="1" length="600.1" width="400.2" thick="16" quantity="4" rotate="1"><edge>ABS</edge></part></list_parts>
          <list_sheets><sheet length="2800" width="2070" thick="16" quantity="2" /></list_sheets>
        </material></list_materials></data_order></data>
        """;

    [Fact]
    public void Validates_decimal_dimensions_and_physical_demand()
    {
        var order = OrderXml.Validate(Source);
        Assert.Equal("Шкаф", order.Name);
        Assert.Equal(4, order.PartCount);
        Assert.Equal(1, order.MaterialCount);
    }

    [Theory]
    [InlineData("<wrong/>")]
    [InlineData("<!DOCTYPE data [<!ENTITY x SYSTEM 'file:///C:/secret'>]><data>&x;</data>")]
    [InlineData("<data><data_order name='empty'/></data>")]
    public void Rejects_invalid_or_external_entity_documents(string xml) =>
        Assert.Throws<InvalidDataException>(() => OrderXml.Validate(xml));

    [Theory]
    [InlineData("600.1", "0")]
    [InlineData("600.1", "NaN")]
    [InlineData("600.1", "600,1")]
    [InlineData("quantity=\"4\"", "quantity=\"-1\"")]
    public void Rejects_invalid_dimensions_and_quantities(string oldValue, string newValue) =>
        Assert.Throws<InvalidDataException>(() => OrderXml.Validate(Source.Replace(oldValue, newValue)));

    [Fact]
    public void Rejects_multiple_orders_in_one_job() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.Validate(Source.Replace("</data>", Source.Replace("<data>", "").Replace("</data>", "") + "</data>")));

    [Fact]
    public void Multiplies_placed_parts_by_plan_quantity_and_reports_partial_result()
    {
        var result = OrderXml.ReadResult(Result("run-1", 2, "400.2", "600.1", "1"), OrderXml.Validate(Source), "run-1");
        Assert.Equal(new ResultInfo(2, 2, 2), result);
    }

    [Fact]
    public void Rejects_stale_export_of_another_order() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.ReadResult(Result("old", 1), OrderXml.Validate(Source), "run-1"));

    [Fact]
    public void Rejects_more_placed_copies_than_requested() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.ReadResult(Result("run-1", 5), OrderXml.Validate(Source), "run-1"));

    [Fact]
    public void Rejects_changed_part_dimensions() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.ReadResult(Result("run-1", 1, "601"), OrderXml.Validate(Source), "run-1"));

    [Theory]
    [InlineData("600", "400", "0")]
    [InlineData("400", "600", "1")]
    [InlineData("600.00", "400.00", "0")]
    public void Accepts_whole_millimetre_rounding_and_preserves_rotation_and_counts(string length, string width, string rotate)
    {
        Assert.Equal(new ResultInfo(1, 1, 3),
            OrderXml.ReadResult(Result("run", 1, length, width, rotate), OrderXml.Validate(Source), "run"));
    }

    [Fact]
    public void Accepts_whole_millimetre_sheet_dimensions_from_decimal_stock()
    {
        var source = Source.Replace("2800", "2800.1").Replace("2070", "2070.2");
        Assert.Equal(new ResultInfo(1, 1, 3),
            OrderXml.ReadResult(Result("run", 1), OrderXml.Validate(source), "run"));
    }

    [Theory]
    [InlineData("600.01", "400.2")]
    [InlineData("600.2", "400.2")]
    [InlineData("600", "401")]
    public void Rejects_changed_fractional_values_and_integer_values_beyond_rounding(string length, string width) =>
        Assert.Throws<InvalidDataException>(() =>
            OrderXml.ReadResult(Result("run", 1, length, width), OrderXml.Validate(Source), "run"));

    [Fact]
    public void Rejects_changed_integer_demand_without_applying_a_general_tolerance()
    {
        var source = Source.Replace("600.1", "600").Replace("400.2", "400");
        Assert.Throws<InvalidDataException>(() =>
            OrderXml.ReadResult(Result("run", 1, "601", "400"), OrderXml.Validate(source), "run"));
    }

    [Fact]
    public void Still_rejects_excess_copies_when_dimensions_are_rounded() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.ReadResult(Result("run", 5, "600", "400"), OrderXml.Validate(Source), "run"));

    [Fact]
    public void Still_rejects_forbidden_rotation_when_dimensions_are_rounded() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.ReadResult(Result("run", 1, "400", "600", "1"), OrderXml.Validate(Source.Replace("rotate=\"1\"", "rotate=\"0\"")), "run"));

    [Fact]
    public void Rejects_precomputed_maps_as_source_input() => Assert.Throws<InvalidDataException>(() =>
        OrderXml.Validate(Source.Replace("</material>", "<list_nesting_plans /></material>")));

    [Fact]
    public void Matches_repeated_numbers_without_consuming_rotatable_demand_for_unrotated_copies()
    {
        var source = Source.Replace("quantity=\"4\"", "quantity=\"1\"")
            .Replace("</list_parts>", "<part number=\"1\" length=\"600.1\" width=\"400.2\" thick=\"16\" quantity=\"1\" rotate=\"0\" /></list_parts>");
        var result = Result("run", 1).Replace("</list_parts>", "<part number=\"1\" length=\"400.2\" width=\"600.1\" rotate=\"1\" /></list_parts>");
        Assert.Equal(new ResultInfo(1, 2, 0), OrderXml.ReadResult(result, OrderXml.Validate(source), "run"));
    }

    [Fact]
    public void Accepts_material_split_into_groups_by_thickness()
    {
        var source = Source.Replace("</list_parts>", "<part number=\"2\" length=\"300\" width=\"200\" thick=\"8\" quantity=\"1\" /></list_parts>")
            .Replace("</list_sheets>", "<sheet length=\"2800\" width=\"2070\" thick=\"8\" quantity=\"1\" /></list_sheets>");
        var export = Result("run", 1).Replace("</material>", "</material><material name=\"ДСП\"><list_nesting_plans><nesting_plan length=\"2800\" width=\"2070\" thick=\"8\" quantity=\"1\"><list_parts><part number=\"2\" length=\"300\" width=\"200\" rotate=\"0\" /></list_parts></nesting_plan></list_nesting_plans></material>");
        Assert.Equal(new ResultInfo(2, 2, 3), OrderXml.ReadResult(export, OrderXml.Validate(source), "run"));
    }

    [Fact]
    public void Still_rejects_excess_quantity_across_split_material_groups()
    {
        var export = Result("run", 1).Replace("</material>", "</material>" + Result("run", 2).Split("<list_materials>")[1].Split("</list_materials>")[0]);
        Assert.Throws<InvalidDataException>(() => OrderXml.ReadResult(export, OrderXml.Validate(Source.Replace("quantity=\"4\"", "quantity=\"2\"")), "run"));
    }

    internal static string Result(string name, int quantity, string length = "600.1", string width = "400.2", string rotate = "0") => $$"""
        <data><data_order name="{{name}}"><list_materials><material name="ДСП"><list_nesting_plans>
        <nesting_plan number="1" length="2800" width="2070" thick="16" quantity="{{quantity}}">
        <list_parts><part number="1" length="{{length}}" width="{{width}}" rotate="{{rotate}}" /></list_parts>
        </nesting_plan></list_nesting_plans></material></list_materials></data_order></data>
        """;
}
