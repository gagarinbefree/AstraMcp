using System.Text.Json;
using System.Xml.Linq;
using AstraMcp.Core;
using AstraMcp.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AstraMcp.Tests;

public sealed class McpBatchTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "astra-mcp-batch", Guid.NewGuid().ToString("N"));
    private McpClient client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AstraMcp.sln"))) root = root.Parent!;
        var configurationName = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var fixtureOutput = Path.Combine(root.FullName, "tests", "NativeFixture", "bin", configurationName, "net10.0");
        var fixtureDirectory = Path.Combine(directory, "fixture");
        Directory.CreateDirectory(fixtureDirectory);
        foreach (var file in Directory.GetFiles(fixtureOutput)) File.Copy(file, Path.Combine(fixtureDirectory, Path.GetFileName(file)));
        var replay = Path.Combine(AppContext.BaseDirectory, "TestData", "astra-manual.xml");
        File.WriteAllText(Path.Combine(fixtureDirectory, "replay.json"), JsonSerializer.Serialize(new
        {
            ExportDirectory = Path.Combine(directory, "exchange"),
            Cases = new Dictionary<string, object>
            {
                ["sample"] = new { Result = replay, Mode = "success", DelayMilliseconds = 250 },
                ["stale"] = new { Result = replay, Mode = "stale", DelayMilliseconds = 0 },
                ["bad-size"] = new { Result = replay, Mode = "bad-size", DelayMilliseconds = 0 },
                ["hang"] = new { Result = replay, Mode = "hang", DelayMilliseconds = 0 }
            }
        }));
        File.WriteAllText(Path.Combine(directory, "config.json"), JsonSerializer.Serialize(new
        {
            Astra = new AdapterOptions
            {
                ExecutablePath = Path.Combine(fixtureDirectory, "NativeFixture.exe"),
                DataDirectory = directory,
                ExportDirectory = Path.Combine(directory, "exchange"),
                ProfileReady = true,
                TimeoutSeconds = 2,
                PollIntervalMilliseconds = 25
            }
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "Batch pipeline with replay fixture, not Astra",
            Command = "dotnet",
            Arguments = [typeof(AstraMcp.Server.AstraTools).Assembly.Location, "--config", Path.Combine(directory, "config.json")],
            StandardErrorLines = line => File.AppendAllText(Path.Combine(directory, "server-stderr.log"), line + "\n")
        }), cancellationToken: timeout.Token);
    }

    [Fact]
    public async Task Validates_all_generated_furniture_orders_through_stdio_mcp()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "batch", "manifest.json")));
        var cases = manifest.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(8, cases.Length);
        foreach (var testCase in cases)
        {
            var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "batch", testCase.GetProperty("file").GetString()!));
            var response = await Call("validate_order", new() { ["xml"] = xml });
            Assert.Contains("Requested parts: " + testCase.GetProperty("requestedParts").GetInt32(), Text(response));
            Assert.Contains("Materials: " + testCase.GetProperty("materials").GetInt32(), Text(response));
        }
    }

    [Fact]
    public async Task Processes_queued_jobs_and_retries_then_reads_original_exports_through_resources()
    {
        var jobs = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var args = new Dictionary<string, object?> { ["xml"] = Source("sample"), ["requestKey"] = "sample-" + index };
            var first = await Call("submit_order", args);
            var id = JobId(first);
            jobs.Add(id);
            Assert.Equal(id, JobId(await Call("submit_order", args)));
        }
        var conflict = await client.CallToolAsync("submit_order", new Dictionary<string, object?> { ["xml"] = Source("stale"), ["requestKey"] = "sample-0" });
        Assert.True(conflict.IsError);
        DateTimeOffset? previousFinish = null;
        foreach (var id in jobs)
        {
            Assert.Contains("Status: Succeeded", await WaitFor(id));
            var response = await Call("get_result", new() { ["jobId"] = id });
            Assert.Contains("Placed parts: 2", Text(response));
            Assert.DoesNotContain("<data", Text(response));
            var link = Assert.Single(response.Content.OfType<ResourceLinkBlock>());
            var resource = await client.ReadResourceAsync(link.Uri);
            var resourceXml = Assert.Single(resource.Contents.OfType<TextResourceContents>()).Text;
            var saved = Path.Combine(directory, "jobs", id, "result.astra.xml");
            Assert.Equal(Artifacts.ReadXml(saved), resourceXml);
            var order = OrderXml.Validate(Source("sample"));
            Assert.Equal(new ResultInfo(1, 2, 0), OrderXml.ReadResult(resourceXml, order, "PC-" + Guid.Parse(id).ToString("N")));
            var export = Path.Combine(directory, "exchange", "PC-" + Guid.Parse(id).ToString("N") + ".xml");
            Assert.Equal(File.ReadAllBytes(export), File.ReadAllBytes(saved));
            var manifestResource = await client.ReadResourceAsync($"astra://jobs/{id}/manifest");
            using var manifest = JsonDocument.Parse(Assert.Single(manifestResource.Contents.OfType<TextResourceContents>()).Text);
            var started = manifest.RootElement.GetProperty("StartedAt").GetDateTimeOffset();
            if (previousFinish is { } finished) Assert.True(started >= finished);
            previousFinish = manifest.RootElement.GetProperty("FinishedAt").GetDateTimeOffset();
            Assert.True(manifest.RootElement.GetProperty("ProcessId").GetInt32() > 0);
        }
    }

    [Theory]
    [InlineData("stale", "different order")]
    [InlineData("bad-size", "Unexpected part dimensions")]
    public async Task Rejects_invalid_native_exports_without_publishing_a_result(string mode, string error)
    {
        var id = JobId(await Call("submit_order", new() { ["xml"] = Source(mode), ["requestKey"] = mode }));
        var status = await WaitFor(id);
        Assert.Contains("Status: Failed", status);
        Assert.Contains(error, status);
        Assert.True((await client.CallToolAsync("get_result", new Dictionary<string, object?> { ["jobId"] = id })).IsError);
        Assert.False(File.Exists(Path.Combine(directory, "jobs", id, "result.astra.xml")));
    }

    [Fact]
    public async Task Cancels_running_and_queued_jobs_through_mcp()
    {
        var running = JobId(await Call("submit_order", new() { ["xml"] = Source("hang"), ["requestKey"] = "running" }));
        await WaitFor(running, runningOnly: true);
        var queued = JobId(await Call("submit_order", new() { ["xml"] = Source("sample"), ["requestKey"] = "queued" }));
        await Call("cancel_job", new() { ["jobId"] = queued });
        await Call("cancel_job", new() { ["jobId"] = running });
        Assert.Contains("Status: Cancelled", await WaitFor(running));
        Assert.Contains("Status: Cancelled", await WaitFor(queued));
        Assert.False(File.Exists(Path.Combine(directory, "jobs", queued, "native-stdout.log")));
    }

    [Fact]
    public async Task Reports_timeout_through_mcp_without_exposing_a_result()
    {
        var id = JobId(await Call("submit_order", new() { ["xml"] = Source("hang"), ["requestKey"] = "timeout" }));
        Assert.Contains("Status: TimedOut", await WaitFor(id));
        Assert.True((await client.CallToolAsync("get_result", new Dictionary<string, object?> { ["jobId"] = id })).IsError);
    }

    private async Task<CallToolResult> Call(string name, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(name, args);
        Assert.False(result.IsError ?? false, Text(result));
        return result;
    }

    private async Task<string> WaitFor(string id, bool runningOnly = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var status = Text(await client.CallToolAsync("get_job", new Dictionary<string, object?> { ["jobId"] = id }, cancellationToken: timeout.Token));
            if (runningOnly ? status.Contains("Status: Running") : !status.Contains("Status: Queued") && !status.Contains("Status: Running")) return status;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static string Source(string mode)
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "order.xml"));
        document.Root!.Element("data_order")!.SetAttributeValue("note", mode);
        return document.ToString();
    }

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    private static string JobId(CallToolResult result) => Text(result).Split('\n')[0]["Job: ".Length..].Trim();

    public async Task DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync();
        // Preserve evidence for inspection; this directory contains fixture outputs, not real Astra calculations.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AstraMcp.sln"))) root = root.Parent!;
        var target = Path.Combine(root.FullName, ".cache", "mcp-batch", Path.GetFileName(directory));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "evidence.txt"), "Replay fixture only; installed Astra was not launched.\nSource directory: " + directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "fixture" + Path.DirectorySeparatorChar)) continue;
            var destination = Path.Combine(target, Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
