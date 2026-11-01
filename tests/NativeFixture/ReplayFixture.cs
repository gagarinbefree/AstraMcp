using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

/// <summary>Replays known native XML to exercise the MCP pipeline without running a nesting algorithm.</summary>
internal static class ReplayFixture
{
    /// <summary>Writes a configured fixture result or simulates a controlled execution failure.</summary>
    /// <param name="inputPath">Prepared source order containing the unique execution name.</param>
    /// <param name="configurationPath">Test-only replay profile beside the isolated fixture executable.</param>
    /// <returns>The child exit code.</returns>
    internal static async Task<int> RunAsync(string inputPath, string configurationPath)
    {
        using var profile = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var input = XDocument.Load(inputPath);
        var order = input.Root!.Element("data_order")!;
        var key = order.Attribute("note")!.Value;
        var settings = profile.RootElement.GetProperty("Cases").GetProperty(key);
        if (settings.GetProperty("Mode").GetString() == "hang") await Task.Delay(Timeout.Infinite);
        await Task.Delay(settings.GetProperty("DelayMilliseconds").GetInt32());
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var result = XDocument.Load(settings.GetProperty("Result").GetString()!);
        var name = order.Attribute("name")!.Value;
        if (settings.GetProperty("Mode").GetString() != "stale")
            result.Root!.Element("data_order")!.SetAttributeValue("name", name);
        if (settings.GetProperty("Mode").GetString() == "bad-size")
            result.Descendants("nesting_plan").First().Element("list_parts")!.Elements("part").First().SetAttributeValue("length", "9999");
        var directory = profile.RootElement.GetProperty("ExportDirectory").GetString()!;
        Directory.CreateDirectory(directory);
        using var writer = XmlWriter.Create(Path.Combine(directory, name + ".xml"), new() { Encoding = Encoding.GetEncoding(1251), Indent = false });
        result.Save(writer);
        return 0;
    }
}
