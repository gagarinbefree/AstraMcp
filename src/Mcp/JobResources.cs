using System.ComponentModel;
using AstraMcp.Core;
using AstraMcp.Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AstraMcp.Server;

/// <summary>Provides explicit, job-scoped access to detailed artifacts.</summary>
/// <param name="store">Durable job metadata.</param>
/// <param name="artifacts">Fixed artifact resolver.</param>
[McpServerResourceType]
public sealed class JobResources(IJobStore store, Artifacts artifacts)
{
    /// <summary>Reads a fixed artifact belonging to a known job.</summary>
    /// <param name="jobId">Adapter-generated job identifier.</param>
    /// <param name="kind">Artifact selector.</param>
    /// <returns>The explicitly requested resource text.</returns>
    [McpServerResource(UriTemplate = "astra://jobs/{jobId}/{kind}", Name = "Astra job artifact")]
    [Description("Read a saved job artifact explicitly. kind is input, execution_input, result, manifest or log. XML artifacts may be large; prefer get_job/get_result summaries first. Native result is available only for Succeeded jobs. No arbitrary filesystem paths are accepted.")]
    public TextResourceContents Read(
        [Description("Exact GUID returned by submit_order.")] string jobId,
        [Description("One fixed artifact name: input, execution_input, result, manifest or log.")] string kind) => AstraTools.Guard(() =>
    {
        var id = AstraTools.ParseId(jobId);
        var job = store.Get(id) ?? throw new KeyNotFoundException("Job not found.");
        if (kind == "result" && job.Status != JobStatus.Succeeded) throw new InvalidOperationException("Result is available only for Succeeded jobs.");
        var path = artifacts.PathFor(id, kind);
        if (!File.Exists(path)) throw new FileNotFoundException("Artifact is not available yet: " + kind);
        string text;
        if (kind is "input" or "execution_input" or "result") text = Artifacts.ReadXml(path);
        else
        {
            using var stream = File.OpenRead(path);
            if (kind == "log" && stream.Length > 32_000) stream.Seek(-32_000, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        return new TextResourceContents { Uri = $"astra://jobs/{id:D}/{kind}", MimeType = kind == "manifest" ? "application/json" : kind == "log" ? "text/plain" : "application/xml", Text = text };
    });
}
