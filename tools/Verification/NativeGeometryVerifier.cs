using System.Runtime.CompilerServices;
using System.Globalization;
using System.Xml.Linq;

[assembly: InternalsVisibleTo("Tests")]

namespace AstraMcp.Verification;

/// <summary>Independently checks exported coordinates without inferring missing geometry.</summary>
internal static class NativeGeometryVerifier
{
    /// <summary>Checks bounds, pairwise intersections, adjacent kerf gaps and axis-aligned cut segments.</summary>
    /// <param name="xml">Native XML previously accepted by the order identity validator.</param>
    /// <returns>Coordinate coverage and measured cut-segment statistics.</returns>
    internal static GeometryReport Verify(string xml)
    {
        var document = XDocument.Parse(xml);
        int placed = 0, positioned = 0, cutSegments = 0;
        decimal cutLength = 0;
        foreach (var material in document.Descendants("material"))
        {
            var kerf = material.Attribute("wid_cut") is null ? 0 : Number(material, "wid_cut");
            if (kerf < 0) throw new InvalidDataException("Negative kerf.");
            foreach (var plan in material.Element("list_nesting_plans")?.Elements("nesting_plan") ?? [])
            {
                var length = Number(plan, "length");
                var width = Number(plan, "width");
                var quantity = int.Parse(plan.Attribute("quantity")!.Value, CultureInfo.InvariantCulture);
                if (length <= 0 || width <= 0 || quantity <= 0) throw new InvalidDataException("Invalid sheet dimensions or quantity.");
                var rectangles = new List<Rectangle>();
                foreach (var part in plan.Element("list_parts")?.Elements("part") ?? [])
                {
                    placed = checked(placed + quantity);
                    if (part.Attribute("x") is null || part.Attribute("y") is null) continue;
                    var rectangle = new Rectangle(Number(part, "x"), Number(part, "y"), Number(part, "length"), Number(part, "width"));
                    if (rectangle.Length <= 0 || rectangle.Width <= 0 || rectangle.X < 0 || rectangle.Y < 0 || rectangle.Right > length || rectangle.Top > width)
                        throw new InvalidDataException("Part coordinates exceed sheet bounds.");
                    foreach (var other in rectangles)
                    {
                        var sharedX = rectangle.X < other.Right && other.X < rectangle.Right;
                        var sharedY = rectangle.Y < other.Top && other.Y < rectangle.Top;
                        if (sharedX && sharedY) throw new InvalidDataException("Placed parts overlap.");
                        var gapX = Math.Max(rectangle.X - other.Right, other.X - rectangle.Right);
                        var gapY = Math.Max(rectangle.Y - other.Top, other.Y - rectangle.Top);
                        if ((sharedY && gapX < kerf) || (sharedX && gapY < kerf))
                            throw new InvalidDataException("Adjacent parts have less clearance than the kerf.");
                    }
                    rectangles.Add(rectangle);
                    positioned = checked(positioned + quantity);
                }
                foreach (var cut in plan.Element("list_cuts")?.Elements("cut") ?? [])
                {
                    var x1 = Number(cut, "x1");
                    var x2 = Number(cut, "x2");
                    var y1 = Number(cut, "y1");
                    var y2 = Number(cut, "y2");
                    if (x1 < 0 || x2 < 0 || y1 < 0 || y2 < 0 || x1 > length || x2 > length || y1 > width || y2 > width)
                        throw new InvalidDataException("Cut segment exceeds sheet bounds.");
                    if (x1 != x2 && y1 != y2) throw new InvalidDataException("Cut segment must be axis-aligned.");
                    cutSegments = checked(cutSegments + quantity);
                    cutLength += (Math.Abs(x2 - x1) + Math.Abs(y2 - y1)) * quantity;
                }
            }
        }
        return new(placed, positioned, cutSegments, cutLength);
    }

    /// <summary>Reads an explicit invariant decimal without rounding.</summary>
    /// <param name="element">Element carrying the numeric attribute.</param>
    /// <param name="attribute">Required attribute name.</param>
    /// <returns>The parsed value in millimetres.</returns>
    private static decimal Number(XElement element, string attribute) =>
        decimal.TryParse((string?)element.Attribute(attribute), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value : throw new InvalidDataException($"Invalid {element.Name}/@{attribute} in native geometry.");

    /// <summary>Represents the exported, already oriented footprint of one placed part.</summary>
    /// <param name="X">Horizontal origin in millimetres.</param>
    /// <param name="Y">Vertical origin in millimetres.</param>
    /// <param name="Length">Horizontal extent in millimetres.</param>
    /// <param name="Width">Vertical extent in millimetres.</param>
    private sealed record Rectangle(decimal X, decimal Y, decimal Length, decimal Width)
    {
        internal decimal Right => X + Length;
        internal decimal Top => Y + Width;
    }
}

/// <summary>Describes only the geometry explicitly present in native XML.</summary>
/// <param name="PlacedParts">Physical placed copies, including repeated plans.</param>
/// <param name="CheckedParts">Physical copies with explicit, verified coordinates.</param>
/// <param name="CutSegments">Physical cut segments, including repeated plans.</param>
/// <param name="MeasuredCutLengthMillimetres">Sum of explicit segment lengths, independent of cut_length.</param>
internal sealed record GeometryReport(int PlacedParts, int CheckedParts, int CutSegments, decimal MeasuredCutLengthMillimetres)
{
    /// <summary>Gets whether every placed part has explicit, verified coordinates.</summary>
    public bool Complete => CheckedParts == PlacedParts;
}
