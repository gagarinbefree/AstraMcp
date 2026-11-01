using AstraMcp.Core;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AstraMcp.Infrastructure;

/// <summary>Persists the shared queue in a local SQLite database.</summary>
public sealed class SqliteJobStore : IJobStore
{
    private readonly string connectionString;
    private readonly Artifacts artifacts;

    /// <summary>Creates or opens the durable local queue.</summary>
    /// <param name="options">Local storage configuration.</param>
    public SqliteJobStore(AdapterOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        artifacts = new(options);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DataDirectory, "jobs.sqlite"),
            DefaultTimeout = 10,
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = Command(connection, null, """
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY, request_key TEXT NOT NULL UNIQUE,
                state INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS jobs_state ON jobs(state);
            CREATE TABLE IF NOT EXISTS profile (id INTEGER PRIMARY KEY CHECK(id=1), signature TEXT NOT NULL);
            """);
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction(deferred: false);
        var signature = Path.GetFullPath(options.ExecutablePath).ToUpperInvariant() + "\n" + Path.GetFullPath(options.ExportDirectory).ToUpperInvariant();
        using var insertProfile = Command(connection, transaction, "INSERT OR IGNORE INTO profile VALUES(1,$signature)", ("$signature", signature));
        insertProfile.ExecuteNonQuery();
        using var readProfile = Command(connection, transaction, "SELECT signature FROM profile WHERE id=1");
        if ((string)readProfile.ExecuteScalar()! != signature)
            throw new InvalidOperationException("This DataDirectory belongs to another Astra executable or export profile. Use its original configuration or a separate DataDirectory.");
        transaction.Commit();
    }

    /// <inheritdoc />
    public Job Enqueue(string xml, string requestKey)
    {
        if (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 100)
            throw new ArgumentException("requestKey must contain 1 to 100 characters.", nameof(requestKey));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));
        return Write((connection, transaction) =>
        {
            var existing = Read(connection, transaction, "SELECT payload FROM jobs WHERE request_key=$key", ("$key", requestKey));
            if (existing is not null)
            {
                if (existing.InputHash != hash) throw new InvalidOperationException("requestKey was already used for different XML. Use a new key for a changed order.");
                return existing;
            }
            var job = new Job(Guid.NewGuid(), requestKey, hash, xml, JobStatus.Queued, DateTimeOffset.UtcNow);
            // Publish source evidence before committing a job visible to competing workers.
            artifacts.WriteInputs(job);
            artifacts.WriteManifest(job);
            using var command = Command(connection, transaction, "INSERT INTO jobs VALUES ($id,$key,$state,$payload)",
                ("$id", job.Id.ToString("D")), ("$key", requestKey), ("$state", (int)job.Status), ("$payload", JsonSerializer.Serialize(job)));
            command.ExecuteNonQuery();
            return job;
        });
    }
    /// <inheritdoc />
    public Job? Get(Guid id)
    {
        using var connection = Open();
        return Read(connection, null, "SELECT payload FROM jobs WHERE id=$id", ("$id", id.ToString("D")));
    }
    /// <inheritdoc />
    public Job? TryClaim() => Write((connection, transaction) =>
    {
        if (Read(connection, transaction, "SELECT payload FROM jobs WHERE state=1 LIMIT 1") is not null) return null;
        var next = Read(connection, transaction, "SELECT payload FROM jobs WHERE state=0 ORDER BY rowid LIMIT 1");
        if (next is null) return null;
        return Save(connection, transaction, next with { Status = JobStatus.Running, StartedAt = DateTimeOffset.UtcNow });
    });
    /// <inheritdoc />
    public Job Cancel(Guid id) => Mutate(id, job => job.Status switch
    {
        JobStatus.Queued => job with { Status = JobStatus.Cancelled, CancellationRequested = true, FinishedAt = DateTimeOffset.UtcNow },
        JobStatus.Running => job with { CancellationRequested = true },
        _ => job
    });
    /// <inheritdoc />
    public void SetProcess(Guid id, int processId, DateTimeOffset startedAt) => Mutate(id, job =>
        job.Status == JobStatus.Running ? job with { ProcessId = processId, ProcessStartedAt = startedAt }
        : throw new InvalidOperationException("Only a running job can own a process."));
    /// <inheritdoc />
    public Job Finish(Guid id, JobStatus status, string? error = null, ResultInfo? result = null)
    {
        if (status is JobStatus.Queued or JobStatus.Running) throw new ArgumentException("A terminal state is required.", nameof(status));
        if (status == JobStatus.Succeeded && result is null) throw new ArgumentException("Success requires a validated result.", nameof(result));
        return Mutate(id, job => job.Status != JobStatus.Running ? throw new InvalidOperationException("Only running jobs can finish.")
            : job with
            {
                Status = job.CancellationRequested ? JobStatus.Cancelled : status,
                FinishedAt = DateTimeOffset.UtcNow,
                Error = job.CancellationRequested ? null : error,
                Result = !job.CancellationRequested && status == JobStatus.Succeeded ? result : null
            });
    }
    /// <inheritdoc />
    public int RecoverInterrupted() => Write((connection, transaction) =>
    {
        var job = Read(connection, transaction, "SELECT payload FROM jobs WHERE state=1 LIMIT 1");
        if (job is null) return 0;
        Save(connection, transaction, job with
        {
            Status = job.CancellationRequested ? JobStatus.Cancelled : JobStatus.Failed,
            FinishedAt = DateTimeOffset.UtcNow,
            Error = "Calculation interrupted by the previous MCP process. No automatic retry was performed. Check whether its recorded Astra process is still running."
        });
        return 1;
    });

    private Job Mutate(Guid id, Func<Job, Job> change) => Write((connection, transaction) =>
    {
        var job = Read(connection, transaction, "SELECT payload FROM jobs WHERE id=$id", ("$id", id.ToString("D")))
            ?? throw new KeyNotFoundException("Job not found: " + id);
        return Save(connection, transaction, change(job));
    });

    private T Write<T>(Func<SqliteConnection, SqliteTransaction, T> operation)
    {
        using var connection = Open();
        // IMMEDIATE transactions serialize read/modify/write operations across MCP processes.
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = operation(connection, transaction);
        transaction.Commit();
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private Job Save(SqliteConnection connection, SqliteTransaction transaction, Job job)
    {
        using var command = Command(connection, transaction, "UPDATE jobs SET state=$state,payload=$payload WHERE id=$id",
            ("$state", (int)job.Status), ("$payload", JsonSerializer.Serialize(job)), ("$id", job.Id.ToString("D")));
        command.ExecuteNonQuery();
        artifacts.WriteManifest(job);
        return job;
    }

    private static Job? Read(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Job>(json) : null;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
}
