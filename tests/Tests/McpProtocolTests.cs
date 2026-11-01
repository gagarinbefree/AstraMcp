using System.Text.Json;
using AstraMcp.Core;
using AstraMcp.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AstraMcp.Tests;

public sealed class McpProtocolTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "astra-mcp-tests", Guid.NewGuid().ToString("N"));
    private McpClient client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var configuration = Path.Combine(directory, "config.json");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new
        {
            Astra = new AdapterOptions { ExecutablePath = Path.Combine(directory, "MissingAstra.exe"), DataDirectory = directory, ExportDirectory = Path.Combine(directory, "exchange"), ProfileReady = false }
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "Astra protocol tests",
            Command = "dotnet",
            Arguments = [typeof(AstraMcp.Server.AstraTools).Assembly.Location, "--config", configuration],
            StandardErrorLines = line => File.AppendAllText(Path.Combine(directory, "server-stderr.log"), line + "\n")
        }), cancellationToken: timeout.Token);
    }

    [Fact]
    public async Task Discovers_six_documented_tools_with_parameter_descriptions_and_annotations()
    {
        var tools = await client.ListToolsAsync();
        Assert.Equal(new[] { "cancel_job", "get_capabilities", "get_job", "get_result", "submit_order", "validate_order" }, tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.Description?.Length > 40));
        var submit = tools.Single(t => t.Name == "submit_order").ProtocolTool;
        Assert.True(submit.Annotations!.IdempotentHint);
        Assert.True(submit.Annotations.DestructiveHint);
        Assert.Contains("description", submit.InputSchema.GetRawText());
        Assert.Contains("requestKey", submit.InputSchema.GetRawText());
    }

    [Fact]
    public async Task Validation_returns_readable_summary_and_actionable_tool_errors()
    {
        var valid = await client.CallToolAsync("validate_order", new Dictionary<string, object?> { ["xml"] = OrderXmlTests.Source });
        Assert.False(valid.IsError ?? false);
        var text = Assert.Single(valid.Content.OfType<TextContentBlock>()).Text;
        Assert.Contains("Requested parts: 4", text);
        Assert.True(text.Length < 1200);
        var invalid = await client.CallToolAsync("validate_order", new Dictionary<string, object?> { ["xml"] = "<wrong/>" });
        Assert.True(invalid.IsError);
        Assert.Contains("exactly one data_order", Assert.Single(invalid.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public async Task Missing_executable_is_visible_in_capabilities_and_prevents_submission()
    {
        var capabilities = await client.CallToolAsync("get_capabilities");
        Assert.Contains("Executable found: no", Assert.Single(capabilities.Content.OfType<TextContentBlock>()).Text);
        var submit = await client.CallToolAsync("submit_order", new Dictionary<string, object?> { ["xml"] = OrderXmlTests.Source, ["requestKey"] = "not-ready" });
        Assert.True(submit.IsError);
        Assert.Contains("ExecutablePath", Assert.Single(submit.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public async Task Capabilities_describe_optional_native_coordinates_and_integer_export_precision()
    {
        var capabilities = await client.CallToolAsync("get_capabilities");
        var text = Assert.Single(capabilities.Content.OfType<TextContentBlock>()).Text;
        Assert.Contains("optional part x/y and list_cuts", text);
        Assert.Contains("whole-millimetre rounding is accepted", text);
        Assert.DoesNotContain("Geometry: not available", text);
    }

    [Fact]
    public async Task Capabilities_explain_default_background_desktop_execution()
    {
        var capabilities = await client.CallToolAsync("get_capabilities");
        Assert.Contains("Execution mode: isolated desktop (background)", Assert.Single(capabilities.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public async Task Result_is_a_summary_and_resource_link_while_xml_is_read_explicitly()
    {
        var options = new AdapterOptions { DataDirectory = directory, ExecutablePath = Path.Combine(directory, "MissingAstra.exe"), ExportDirectory = Path.Combine(directory, "exchange") };
        var store = new SqliteJobStore(options);
        var job = store.Enqueue(OrderXmlTests.Source, "existing-result");
        store.TryClaim();
        store.Finish(job.Id, JobStatus.Succeeded, result: new(1, 1, 3));
        var artifacts = new Artifacts(options);
        Directory.CreateDirectory(artifacts.DirectoryFor(job.Id));
        var xml = OrderXmlTests.Result(job.ExecutionName, 1);
        File.WriteAllText(artifacts.PathFor(job.Id, "result"), xml);
        var result = await client.CallToolAsync("get_result", new Dictionary<string, object?> { ["jobId"] = job.Id.ToString() });
        Assert.False(result.IsError ?? false);
        Assert.DoesNotContain("<data>", string.Join("", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        Assert.Equal("application/xml", link.MimeType);
        var resource = await client.ReadResourceAsync(link.Uri);
        Assert.Equal(xml, Assert.Single(resource.Contents.OfType<TextResourceContents>()).Text);
        var templates = await client.ListResourceTemplatesAsync();
        Assert.Contains(templates, t => t.UriTemplate == "astra://jobs/{jobId}/{kind}");
    }

    [Fact]
    public async Task Rejects_invalid_job_identifiers_without_reading_arbitrary_paths()
    {
        var result = await client.CallToolAsync("get_job", new Dictionary<string, object?> { ["jobId"] = "../../outside" });
        Assert.True(result.IsError);
        Assert.Contains("GUID", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    public async Task DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
