using System.Globalization;
using System.Text;
using System.Text.Json;
using AstraMcp.Core;
using AstraMcp.Infrastructure;
using AstraMcp.Verification;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Explicitly invoked native verification client. Ordinary automated tests never execute this program.
if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: Verification <repository> <configuration> [case-id]");
var root = Path.GetFullPath(args[0]);
var configuration = Path.GetFullPath(args[1], root);
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
using var configJson = JsonDocument.Parse(File.ReadAllText(configuration));
var options = configJson.RootElement.GetProperty("Astra").Deserialize<AdapterOptions>(jsonOptions)!;
options.Normalize(Path.GetDirectoryName(configuration)!);
if (!options.ProfileReady) throw new InvalidOperationException("The explicit native profile must have ProfileReady=true.");
var inputDirectory = Path.Combine(root, "examples", "batch");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(inputDirectory, "manifest.json")));
var cases = manifest.RootElement.GetProperty("cases").EnumerateArray()
    .Where(c => args.Length < 3 || c.GetProperty("id").GetString() == args[2]).Select(c => c.Clone()).ToArray();
if (cases.Length == 0) throw new ArgumentException("Unknown case id.");
var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
var outputDirectory = Path.Combine(inputDirectory, "results", runId);
Directory.CreateDirectory(outputDirectory);
Console.WriteLine("Native verification evidence: " + outputDirectory);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds((options.TimeoutSeconds + 10) * cases.Length + 30));
await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
{
    Name = "Astra native verification with official .NET MCP client",
    Command = Path.Combine(root, "publish", "win-x86", "AstraMcp.exe"),
    Arguments = ["--config", configuration],
    StandardErrorLines = line => File.AppendAllText(Path.Combine(outputDirectory, "mcp-stderr.log"), line + "\n")
}), cancellationToken: deadline.Token);
var jobs = new List<NativeJob>();
var outcomes = new List<object>();
try
{
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "capabilities.txt"), Text(await Call("get_capabilities", [])));
    foreach (var testCase in cases)
    {
        var id = testCase.GetProperty("id").GetString()!;
        var xml = File.ReadAllText(Path.Combine(inputDirectory, testCase.GetProperty("file").GetString()!));
        var caseDirectory = Path.Combine(outputDirectory, id);
        Directory.CreateDirectory(caseDirectory);
        File.WriteAllText(Path.Combine(caseDirectory, "input.xml"), xml, new UTF8Encoding(false));
        await Call("validate_order", new() { ["xml"] = xml });
        var request = new Dictionary<string, object?> { ["xml"] = xml, ["requestKey"] = runId + "-" + id };
        var submitted = Text(await Call("submit_order", request));
        var jobId = submitted.Split('\n')[0]["Job: ".Length..].Trim();
        if (!Guid.TryParseExact(jobId, "D", out _)) throw new InvalidDataException("Missing job GUID.");
        jobs.Add(new(id, jobId, xml, testCase, caseDirectory));
        var retry = Text(await Call("submit_order", request));
        if (!retry.Contains(jobId, StringComparison.Ordinal)) throw new InvalidDataException("Idempotent retry returned another job.");
        File.WriteAllText(Path.Combine(caseDirectory, "submitted.txt"), submitted);
        Console.WriteLine($"Submitted {id}: {jobId}");
    }
    foreach (var job in jobs)
    {
        string status;
        string snapshot;
        do
        {
            snapshot = Text(await Call("get_job", new() { ["jobId"] = job.JobId }));
            status = Field(snapshot, "Status");
            File.WriteAllText(Path.Combine(job.Directory, "status.txt"), snapshot);
            if (status is "Queued" or "Running") await Task.Delay(250, deadline.Token);
        } while (status is "Queued" or "Running");
        try
        {
            var savedManifest = await client.ReadResourceAsync($"astra://jobs/{job.JobId}/manifest", cancellationToken: deadline.Token);
            File.WriteAllText(Path.Combine(job.Directory, "manifest.json"), savedManifest.Contents.OfType<TextResourceContents>().Single().Text);
            var log = await client.ReadResourceAsync($"astra://jobs/{job.JobId}/log", cancellationToken: deadline.Token);
            File.WriteAllText(Path.Combine(job.Directory, "execution.log"), log.Contents.OfType<TextResourceContents>().Single().Text);
            if (status != "Succeeded") throw new InvalidDataException(snapshot);
            var response = await Call("get_result", new() { ["jobId"] = job.JobId });
            var summary = Text(response);
            File.WriteAllText(Path.Combine(job.Directory, "result-summary.txt"), summary);
            var link = response.Content.OfType<ResourceLinkBlock>().Single();
            var resource = await client.ReadResourceAsync(link.Uri, cancellationToken: deadline.Token);
            var xml = resource.Contents.OfType<TextResourceContents>().Single().Text;
            var native = Path.Combine(options.DataDirectory, "jobs", job.JobId, "result.astra.xml");
            if (xml != Artifacts.ReadXml(native)) throw new InvalidDataException("XML resource differs from saved native export.");
            File.Copy(native, Path.Combine(job.Directory, "result.astra.xml"));
            var actual = OrderXml.ReadResult(xml, OrderXml.Validate(job.Source), "PC-" + Guid.Parse(job.JobId).ToString("N"));
            if (actual.PlacedParts != job.Case.GetProperty("expectedPlacedParts").GetInt32() || actual.UnplacedParts != job.Case.GetProperty("expectedUnplacedParts").GetInt32())
                throw new InvalidDataException($"Unexpected counts: {actual.PlacedParts} placed, {actual.UnplacedParts} unplaced.");
            var geometry = NativeGeometryVerifier.Verify(xml);
            File.WriteAllText(Path.Combine(job.Directory, "geometry.json"), JsonSerializer.Serialize(geometry, jsonOptions));
            outcomes.Add(new { job.Id, job.JobId, Status = status, Passed = true, Result = actual, Geometry = geometry });
            Console.WriteLine($"PASS {job.Id}: {actual.SheetCount} sheets, {actual.PlacedParts} placed, {actual.UnplacedParts} unplaced");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outcomes.Add(new { job.Id, job.JobId, Status = status, Passed = false, Error = error.Message });
            File.WriteAllText(Path.Combine(job.Directory, "failure.txt"), error.ToString());
            Console.WriteLine($"FAIL {job.Id}: {error.Message}");
        }
        SaveReport();
    }
}
finally
{
    SaveReport();
    foreach (var job in jobs)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await client.CallToolAsync("cancel_job", new Dictionary<string, object?> { ["jobId"] = job.JobId }, cancellationToken: cleanup.Token); }
        catch (Exception error) { Console.Error.WriteLine("Cleanup: " + error.Message); }
    }
}
var failures = outcomes.Count(o => JsonSerializer.SerializeToElement(o).GetProperty("Passed").GetBoolean() == false);
Console.WriteLine($"Native verification: {outcomes.Count - failures}/{cases.Length} passed. Evidence: {outputDirectory}");
return failures == 0 && outcomes.Count == cases.Length ? 0 : 1;

async Task<CallToolResult> Call(string name, Dictionary<string, object?> arguments)
{
    var result = await client.CallToolAsync(name, arguments, cancellationToken: deadline.Token);
    if (result.IsError == true) throw new InvalidOperationException(Text(result));
    return result;
}

void SaveReport() => File.WriteAllText(Path.Combine(outputDirectory, "report.json"), JsonSerializer.Serialize(new
{
    RunId = runId,
    CheckedAtUtc = DateTimeOffset.UtcNow,
    Client = "Official .NET MCP SDK 2.3.0",
    Engine = options.ExecutablePath,
    Cases = cases.Length,
    Outcomes = outcomes
}, jsonOptions));

static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
static string Field(string text, string label) => text.Split('\n').Single(line => line.StartsWith(label + ": ", StringComparison.Ordinal))[(label.Length + 2)..].Trim();

/// <summary>Tracks one real calculation submitted through MCP.</summary>
/// <param name="Id">Human-readable case identifier.</param>
/// <param name="JobId">Durable MCP job identifier.</param>
/// <param name="Source">Original source XML.</param>
/// <param name="Case">Expected result counts.</param>
/// <param name="Directory">Evidence directory.</param>
internal sealed record NativeJob(string Id, string JobId, string Source, JsonElement Case, string Directory);
