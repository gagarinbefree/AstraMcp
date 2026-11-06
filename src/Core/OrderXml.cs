using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AstraMcp.Core;

/// <summary>Validates source orders and summarizes native Astra results.</summary>
public static class OrderXml
{
    /// <summary>Validates a single rectangular order without executing Astra.</summary>
    /// <param name="xml">Source XML text.</param>
    /// <returns>The validated source demand.</returns>
    public static OrderInfo Validate(string xml)
    {
        var order = GetOrder(Load(xml));
        var parts = new List<PartDemand>();
        var sheets = new List<SheetDemand>();
        var materials = order.Element("list_materials")?.Elements("material").ToArray() ?? [];
        if (materials.Length == 0) throw Invalid("At least one material is required.");
        foreach (var material in materials)
        {
            if (material.Element("list_nesting_plans") is not null)
                throw Invalid("Source input must not contain precomputed list_nesting_plans.");
            var name = Required(material, "name");
            if (materials.Count(m => (string?)m.Attribute("name") == name) != 1)
                throw Invalid("Material names must be unique within this adapter's single-order input.");
            foreach (var part in material.Element("list_parts")?.Elements("part") ?? [])
            {
                if (part.Attribute("contour") is { Value.Length: > 0 })
                    throw Invalid("Only rectangular parts are supported; contour must be empty.");
                parts.Add(new(name, Required(part, "number"), Dimension(part, "length"), Dimension(part, "width"),
                    Dimension(part, "thick"), Quantity(part), Flag(part, "rotate", true), Assembly(part)));
            }
            foreach (var sheet in material.Element("list_sheets")?.Elements("sheet") ?? [])
                sheets.Add(new(name, Dimension(sheet, "length"), Dimension(sheet, "width"), Dimension(sheet, "thick"), Quantity(sheet)));
            if (!parts.Any(p => p.Material == name) || !sheets.Any(s => s.Material == name))
                throw Invalid("Each material requires source parts and explicitly supplied sheets.");
        }
        var count = parts.Sum(p => (long)p.Quantity);
        if (count > 1_000_000) throw Invalid("The order exceeds 1,000,000 physical parts.");
        return new(Required(order, "name"), (int)count, materials.Length) { Parts = parts, Sheets = sheets };
    }

    /// <summary>Validates a native result, allowing length and width to be exported as rounded whole millimetres.</summary>
    /// <param name="xml">Exported XML text.</param>
    /// <param name="source">Original demand.</param>
    /// <param name="expectedName">Unique execution order name.</param>
    /// <returns>The physical sheet and part counts.</returns>
    public static ResultInfo ReadResult(string xml, OrderInfo source, string expectedName)
    {
        var order = GetOrder(Load(xml));
        if (Required(order, "name") != expectedName) throw Invalid("The export belongs to a different order.");
        var remaining = source.Parts.Select(p => p.Quantity).ToArray();
        var availableSheets = source.Sheets.Select(s => s.Quantity).ToArray();
        int sheets = 0, placed = 0;
        var materials = order.Element("list_materials")?.Elements("material").ToArray() ?? [];
        var expectedMaterials = source.Parts.Select(p => p.Material).ToHashSet(StringComparer.Ordinal);
        var exportedMaterials = materials.Select(m => Required(m, "name")).ToHashSet(StringComparer.Ordinal);
        if (!expectedMaterials.SetEquals(exportedMaterials)) throw Invalid("Exported material groups differ from the source order.");
        foreach (var material in materials)
        {
            var name = Required(material, "name");
            var plans = material.Element("list_nesting_plans") ?? throw Invalid("Missing list_nesting_plans in result.");
            foreach (var plan in plans.Elements("nesting_plan"))
            {
                var multiplicity = Quantity(plan);
                var thick = Dimension(plan, "thick");
                var length = Dimension(plan, "length");
                var width = Dimension(plan, "width");
                var need = multiplicity;
                for (var i = 0; i < source.Sheets.Count && need > 0; i++)
                {
                    var s = source.Sheets[i];
                    if (s.Material != name || s.Thick != thick || !MatchesExportDimension(s.Length, length) || !MatchesExportDimension(s.Width, width)) continue;
                    var take = Math.Min(need, availableSheets[i]);
                    availableSheets[i] -= take;
                    need -= take;
                }
                if (need > 0) throw Invalid("Result uses a sheet size, thickness or quantity absent from the input.");
                sheets = checked(sheets + multiplicity);
                foreach (var part in plan.Element("list_parts")?.Elements("part") ?? [])
                {
                    var rotated = Flag(part, "rotate", false);
                    var a = Dimension(part, "length");
                    var b = Dimension(part, "width");
                    var number = Required(part, "number");
                    var assembly = Assembly(part);
                    var copies = multiplicity;
                    foreach (var i in Enumerable.Range(0, source.Parts.Count).OrderBy(i => source.Parts[i].CanRotate))
                    {
                        if (copies == 0) break;
                        var p = source.Parts[i];
                        if (p.Material != name || p.Number != number || p.Assembly != assembly || p.Thick != thick ||
                            !MatchesExportDimension(p.Length, rotated ? b : a) || !MatchesExportDimension(p.Width, rotated ? a : b) || (rotated && !p.CanRotate)) continue;
                        var take = Math.Min(copies, remaining[i]);
                        remaining[i] -= take;
                        copies -= take;
                    }
                    if (copies > 0)
                    {
                        var expectedSizes = source.Parts
                            .Where(p => p.Material == name && p.Number == number && p.Assembly == assembly)
                            .Select(p => Size(p.Length, p.Width, p.Thick)).Distinct().Take(3);
                        throw Invalid($"Unexpected part dimensions, assembly, rotation or excess quantity in result. " +
                            $"Part '{number}', material '{name}', assembly '{assembly}'. " +
                            $"Unrotated result size: {Size(rotated ? b : a, rotated ? a : b, thick)} mm. " +
                            $"Requested sizes: {string.Join("; ", expectedSizes)} mm.");
                    }
                    placed = checked(placed + multiplicity);
                }
            }
        }
        return new(sheets, placed, source.PartCount - placed);
    }

    /// <summary>Creates a UTF-8 execution copy with a unique, filesystem-safe order name.</summary>
    /// <param name="xml">Original validated source XML.</param>
    /// <param name="executionName">Adapter-generated order name.</param>
    /// <returns>XML retaining all source content except the order name and encoding declaration.</returns>
    public static string Prepare(string xml, string executionName)
    {
        Validate(xml);
        var document = Load(xml);
        GetOrder(document).SetAttributeValue("name", executionName);
        document.Declaration = new("1.0", "utf-8", null);
        return document.Declaration + "\n" + document.ToString(SaveOptions.DisableFormatting);
    }

    private static XDocument Load(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 4_000_000) throw Invalid("XML must contain between 1 and 4,000,000 characters.");
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e) { throw new InvalidDataException("Malformed XML or prohibited DTD: " + e.Message, e); }
    }

    private static XElement GetOrder(XDocument document)
    {
        var root = document.Root;
        var orders = root?.Elements("data_order").ToArray() ?? [];
        if (root?.Name != "data" || orders.Length != 1 || root.Elements().Any(e => e.Name != "data_order"))
            throw Invalid("Expected data containing exactly one data_order and no catalog or stock mutation blocks.");
        if (root.Attribute("lib_mat_resete") is not null || root.Attribute("lib_edge_resete") is not null)
            throw Invalid("Catalog reset attributes are not supported.");
        return orders[0];
    }

    private static string Required(XElement element, string name) =>
        element.Attribute(name)?.Value is { Length: > 0 } value ? value : throw Invalid($"Missing {element.Name}/@{name}.");

    private static string Assembly(XElement element) => (string?)element.Attribute("assembly") is { Length: > 0 } value ? value : "- не в изделии -";

    private static string Size(decimal length, decimal width, decimal thick) =>
        string.Create(CultureInfo.InvariantCulture, $"{length} x {width} x {thick}");

    /// <summary>Matches an exact dimension or a nearest whole-millimetre export, accepting either midpoint convention.</summary>
    /// <param name="requested">Original dimension in millimetres.</param>
    /// <param name="exported">Dimension written by Astra using its configured precision.</param>
    /// <returns>Whether the exported value is exact or a permitted integer representation.</returns>
    private static bool MatchesExportDimension(decimal requested, decimal exported) =>
        requested == exported || (exported == decimal.Truncate(exported) && decimal.Abs(requested - exported) <= 0.5m);

    private static decimal Dimension(XElement element, string name) =>
        decimal.TryParse(Required(element, name), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= 1_000_000
            ? value : throw Invalid($"{element.Name}/@{name} must be a positive decimal in millimetres, using a dot.");

    private static int Quantity(XElement element) =>
        int.TryParse(Required(element, "quantity"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 1_000_000
            ? value : throw Invalid($"{element.Name}/@quantity must be an integer between 1 and 1,000,000.");

    private static bool Flag(XElement element, string name, bool fallback) => element.Attribute(name)?.Value switch
    {
        null => fallback,
        "0" => false,
        "1" => true,
        _ => throw Invalid($"{name} must be 0 or 1.")
    };

    private static InvalidDataException Invalid(string message) => new(message);
}

/// <summary>Describes source demand validated before scheduling.</summary>
/// <param name="Name">Source order name.</param>
/// <param name="PartCount">Number of physical requested parts.</param>
/// <param name="MaterialCount">Number of material groups.</param>
public sealed record OrderInfo(string Name, int PartCount, int MaterialCount)
{
    /// <summary>Gets the original, unrotated source demand.</summary>
    public IReadOnlyList<PartDemand> Parts { get; init; } = [];
    /// <summary>Gets the explicitly supplied stock.</summary>
    public IReadOnlyList<SheetDemand> Sheets { get; init; } = [];
}

/// <summary>Preserves the source identity and allowed orientation of a part row.</summary>
/// <param name="Material">Material name.</param>
/// <param name="Number">Display number, which may repeat.</param>
/// <param name="Length">Unrotated length in millimetres.</param>
/// <param name="Width">Unrotated width in millimetres.</param>
/// <param name="Thick">Thickness in millimetres.</param>
/// <param name="Quantity">Requested physical copies.</param>
/// <param name="CanRotate">Whether rotation is permitted.</param>
/// <param name="Assembly">Assembly identity.</param>
public sealed record PartDemand(string Material, string Number, decimal Length, decimal Width, decimal Thick, int Quantity, bool CanRotate, string Assembly);

/// <summary>Identifies available physical source sheets.</summary>
/// <param name="Material">Material name.</param>
/// <param name="Length">Length in millimetres.</param>
/// <param name="Width">Width in millimetres.</param>
/// <param name="Thick">Thickness in millimetres.</param>
/// <param name="Quantity">Available physical copies.</param>
public sealed record SheetDemand(string Material, decimal Length, decimal Width, decimal Thick, int Quantity);

/// <summary>Describes the physical native result without inventing geometry.</summary>
/// <param name="SheetCount">Physical sheet count, including plan multiplicities.</param>
/// <param name="PlacedParts">Physical placed part count.</param>
/// <param name="UnplacedParts">Demand not represented by placed parts.</param>
public sealed record ResultInfo(int SheetCount, int PlacedParts, int UnplacedParts);
