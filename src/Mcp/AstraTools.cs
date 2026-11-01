using System.ComponentModel;
using System.Diagnostics;
using AstraMcp.Core;
using AstraMcp.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AstraMcp.Server;

/// <summary>Exposes the local Astra adapter to MCP clients.</summary>
/// <param name="options">Validated adapter configuration.</param>
/// <param name="store">Shared durable queue.</param>
/// <param name="artifacts">Job-scoped artifact access.</param>
[McpServerToolType]
public sealed class AstraTools(AdapterOptions options, IJobStore store, Artifacts artifacts)
{
    /// <summary>Reports the configured installation and execution requirements.</summary>
    /// <returns>A short, readable capability report.</returns>
    [McpServerTool(Name = "get_capabilities", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Inspect the configured Astra executable, version, export directory and launch-profile readiness before submitting orders. Does not launch or stop Astra. This adapter uses a local interactive Windows session and stdio MCP.")]
    public string GetCapabilities()
    {
        var exists = File.Exists(options.ExecutablePath);
        var version = exists ? FileVersionInfo.GetVersionInfo(options.ExecutablePath).FileVersion ?? "unknown" : "unavailable";
        return $"""
            Astra adapter
            Executable: {options.ExecutablePath}
            Executable found: {(exists ? "yes" : "no")}
            File version: {version}
            Execution mode: {(options.RunInBackground ? "isolated desktop (background)" : "visible")}
            Profile ready: {(options.ProfileReady ? "yes (configuration assertion; verify with a real smoke run)" : "no; configure command-line startup and XML export on exit")}
            Export directory: {options.ExportDirectory}
            Deadline: {options.TimeoutSeconds} seconds
            Parallel calculations: 1 per shared export profile
            Before each run: stop matching existing Astra processes by PID, then launch -i -n -q
            Dimension precision: exported whole-millimetre rounding is accepted; input and native XML stay unchanged
            Geometry: optional part x/y and list_cuts are preserved in XML; geometry is not inferred or validated
            """;
    }

    /// <summary>Checks a source order without invoking the native application.</summary>
    /// <param name="xml">One complete source order in Astra XML format.</param>
    /// <returns>The validation summary or an actionable MCP tool error.</returns>
    [McpServerTool(Name = "validate_order", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Validate one rectangular source order without starting Astra. Requires named material groups, source list_parts and explicit list_sheets; dimensions are millimetres with a decimal dot. This validates the adapter's supported subset, not Astra's complete schema.")]
    public CallToolResult ValidateOrder([Description("Complete XML text: data containing exactly one data_order, material groups, requested parts and finite positive sheet quantities. Maximum 4,000,000 characters. Do not send precomputed layouts.")] string xml) => Guard(() =>
    {
        var order = OrderXml.Validate(xml);
        return Text($"Validation: passed\nOrder: {Short(order.Name)}\nMaterials: {order.MaterialCount}\nRequested parts: {order.PartCount}\nNative calculation: not started");
    });

    /// <summary>Accepts an idempotent calculation request.</summary>
    /// <param name="xml">Complete source order XML.</param>
    /// <param name="requestKey">Stable retry key for this exact XML text.</param>
    /// <returns>A job identifier and instructions for polling.</returns>
    [McpServerTool(Name = "submit_order", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Queue a source order for Astra calculation and return immediately with jobId. Reuse the same requestKey and identical XML when retrying; changed XML requires a new key. Execution stops existing matching Astra processes by PID and may discard their unsaved work. Poll get_job, then use get_result. Keep the MCP connection open while calculation runs.")]
    public CallToolResult SubmitOrder(
        [Description("Complete source XML order, not a calculated solution. Use validate_order first. Dimensions and thickness are in millimetres.")] string xml,
        [Description("Required stable idempotency key, 1 to 100 characters. Repeating this key with identical XML returns the original job instead of calculating twice.")] string requestKey) => Guard(() =>
    {
        OrderXml.Validate(xml);
        if (!File.Exists(options.ExecutablePath)) throw new InvalidOperationException("Configured Astra:ExecutablePath does not exist. Correct the configuration file and restart MCP.");
        if (!options.ProfileReady) throw new InvalidOperationException("Astra profile is not ready. Configure command-line startup, XML exit export named *.xml and ExportDirectory, then set Astra:ProfileReady=true and restart MCP.");
        var job = store.Enqueue(xml, requestKey);
        return Text(Describe(job) + "\nNext: call get_job with this jobId. A queued job survives reconnection; shutdown interrupts an active calculation.");
    });

    /// <summary>Reads a persisted job status.</summary>
    /// <param name="jobId">Identifier returned by submit_order.</param>
    /// <returns>A bounded job summary.</returns>
    [McpServerTool(Name = "get_job", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Read a job's current state, requested and placed quantities, timestamps, owned PID and actionable failure message. Queued and Running are nonterminal. Succeeded can still contain unplaced parts; use get_result for the saved XML link.")]
    public CallToolResult GetJob([Description("Exact GUID returned by submit_order. This is an identifier, not a filesystem path.")] string jobId) => Guard(() => Text(Describe(Find(jobId))));

    /// <summary>Returns a compact result and an explicit native XML resource link.</summary>
    /// <param name="jobId">Identifier of a successful job.</param>
    /// <returns>Human-readable counts and a resource link without embedding the XML.</returns>
    [McpServerTool(Name = "get_result", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the validated result summary and a resource link to the original Astra XML. Available only after Succeeded. Does not embed a large XML or raw JSON response. Read the returned resource explicitly when detailed comparison or XML is needed; coordinates and cut trees are not inferred.")]
    public CallToolResult GetResult([Description("GUID of a job whose get_job state is Succeeded.")] string jobId) => Guard<CallToolResult>(() =>
    {
        var job = Find(jobId);
        if (job.Status != JobStatus.Succeeded) throw new InvalidOperationException($"Result is not available: status is {job.Status}. Read get_job for details.");
        if (!File.Exists(artifacts.PathFor(job.Id, "result"))) throw new IOException("The saved result XML is missing. Inspect the job artifact directory.");
        return new()
        {
            Content =
            [
                new TextContentBlock { Text = Describe(job) + "\nXML: use the attached resource link; native output is preserved unchanged." },
                new ResourceLinkBlock { Uri = $"astra://jobs/{job.Id:D}/result", Name = "result.astra.xml", MimeType = "application/xml", Description = "Original validated Astra XML; read explicitly for detailed output comparison." }
            ]
        };
    });

    /// <summary>Requests cancellation without removing persisted evidence.</summary>
    /// <param name="jobId">Identifier of the queued or running order.</param>
    /// <returns>The updated lifecycle state.</returns>
    [McpServerTool(Name = "cancel_job", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Cancel a queued order immediately or request stopping the child PID owned by a running calculation. Repeating cancellation is safe. Completed jobs and their saved XML remain available; cancellation does not delete artifacts. Poll get_job until terminal.")]
    public CallToolResult CancelJob([Description("Exact GUID of the job to cancel; no process identifier or path is accepted.")] string jobId) => Guard(() =>
    {
        var job = store.Cancel(Find(jobId).Id);
        return Text(Describe(job));
    });

    private Job Find(string id) => store.Get(ParseId(id)) ?? throw new KeyNotFoundException("Job not found. Use the GUID returned by submit_order.");

    internal static Guid ParseId(string id) => Guid.TryParseExact(id, "D", out var guid) ? guid : throw new ArgumentException("jobId must be a GUID in xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx format.");

    private static string Describe(Job job)
    {
        var order = OrderXml.Validate(job.SourceXml);
        return $"Job: {job.Id:D}\nStatus: {job.Status}\nOrder: {Short(order.Name)}\nRequested parts: {order.PartCount}\nCreated UTC: {job.CreatedAt:O}"
            + (job.ProcessId is { } pid ? $"\nAstra PID: {pid}" : "")
            + (job.CancellationRequested ? "\nCancellation requested: yes" : "")
            + (job.Result is { } result ? $"\nSheets: {result.SheetCount}\nPlaced parts: {result.PlacedParts}\nUnplaced parts: {result.UnplacedParts}" : "")
            + (job.Error is { } error ? "\nError: " + Short(error, 1200) : "");
    }

    internal static T Guard<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        { throw new McpException(Short(e.Message, 1600)); }
    }

    private static string Short(string value, int limit = 200) => value.Length <= limit ? value : value[..limit] + "…";

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
}
