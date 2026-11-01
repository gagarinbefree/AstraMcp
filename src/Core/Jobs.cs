namespace AstraMcp.Core;

/// <summary>Defines the persisted execution lifecycle.</summary>
public enum JobStatus
{
    /// <summary>The order is waiting for the shared Astra profile.</summary>
    Queued,
    /// <summary>A worker owns the calculation.</summary>
    Running,
    /// <summary>A fresh export passed semantic validation.</summary>
    Succeeded,
    /// <summary>Execution or result validation failed.</summary>
    Failed,
    /// <summary>The calculation exceeded its deadline.</summary>
    TimedOut,
    /// <summary>The order was cancelled.</summary>
    Cancelled
}

/// <summary>Stores one durable request independently of an MCP connection.</summary>
/// <param name="Id">Adapter-generated identifier.</param>
/// <param name="RequestKey">Caller-provided retry key.</param>
/// <param name="InputHash">SHA-256 of the submitted XML text.</param>
/// <param name="SourceXml">Original XML text; never included in ordinary tool summaries.</param>
/// <param name="Status">Current execution state.</param>
/// <param name="CreatedAt">UTC acceptance time.</param>
public sealed record Job(Guid Id, string RequestKey, string InputHash, string SourceXml, JobStatus Status, DateTimeOffset CreatedAt)
{
    /// <summary>Gets the time the calculation was claimed.</summary>
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Gets the terminal state timestamp.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>Gets whether cancellation has been requested.</summary>
    public bool CancellationRequested { get; init; }
    /// <summary>Gets the actionable failure explanation, if present.</summary>
    public string? Error { get; init; }
    /// <summary>Gets validated physical result counts.</summary>
    public ResultInfo? Result { get; init; }
    /// <summary>Gets the child process identifier recorded immediately after launch.</summary>
    public int? ProcessId { get; init; }
    /// <summary>Gets the child start time used to distinguish reused process identifiers.</summary>
    public DateTimeOffset? ProcessStartedAt { get; init; }
    /// <summary>Gets the unique name used to correlate the exported order.</summary>
    public string ExecutionName => "PC-" + Id.ToString("N");
}

/// <summary>Defines atomic job persistence shared by all local MCP processes.</summary>
public interface IJobStore
{
    /// <summary>Accepts a request or returns the identical previously accepted request.</summary>
    /// <param name="xml">Validated source XML.</param>
    /// <param name="requestKey">Stable caller retry key.</param>
    /// <returns>The durable new or existing job.</returns>
    Job Enqueue(string xml, string requestKey);
    /// <summary>Reads a job without altering its lifecycle.</summary>
    /// <param name="id">Job identifier.</param>
    /// <returns>The job, or null if absent.</returns>
    Job? Get(Guid id);
    /// <summary>Claims the oldest waiting job only if no calculation is already running.</summary>
    /// <returns>The exclusively claimed job, or null.</returns>
    Job? TryClaim();
    /// <summary>Cancels a queued job or requests cancellation of a running job.</summary>
    /// <param name="id">Job identifier.</param>
    /// <returns>The updated job.</returns>
    Job Cancel(Guid id);
    /// <summary>Records the owned child process for interruption diagnostics.</summary>
    /// <param name="id">Running job identifier.</param>
    /// <param name="processId">Child process identifier.</param>
    /// <param name="startedAt">Child process start time.</param>
    void SetProcess(Guid id, int processId, DateTimeOffset startedAt);
    /// <summary>Completes a running job, honoring any concurrent cancellation request.</summary>
    /// <param name="id">Running job identifier.</param>
    /// <param name="status">Requested terminal state.</param>
    /// <param name="error">Failure explanation.</param>
    /// <param name="result">Validated counts for a successful result.</param>
    /// <returns>The committed terminal job.</returns>
    Job Finish(Guid id, JobStatus status, string? error = null, ResultInfo? result = null);
    /// <summary>Marks abandoned running jobs failed after the global execution lock is acquired.</summary>
    /// <returns>The number of interrupted jobs.</returns>
    int RecoverInterrupted();
}
