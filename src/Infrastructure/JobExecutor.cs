using AstraMcp.Core;
using System.Text;
using System.Security.Cryptography;

namespace AstraMcp.Infrastructure;

/// <summary>Executes one durable order while exclusively owning the shared export profile.</summary>
/// <param name="options">Validated adapter configuration.</param>
/// <param name="store">Durable queue.</param>
/// <param name="process">Native process abstraction.</param>
public sealed class JobExecutor(AdapterOptions options, IJobStore store, IAstraProcess process)
{
    /// <summary>Attempts one queued calculation and commits its terminal state.</summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>True if a queued order was processed; otherwise false.</returns>
    public async Task<bool> ExecuteNextAsync(CancellationToken cancellationToken)
    {
        if (!options.ProfileReady || !File.Exists(options.ExecutablePath)) return false;
        Directory.CreateDirectory(options.ExportDirectory);
        using var profileLock = TryLock();
        if (profileLock is null) return false;
        store.RecoverInterrupted();
        var job = store.TryClaim();
        if (job is null) return false;
        var artifacts = new Artifacts(options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var requestedCancellation = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, requestedCancellation.Token);
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorCancellationAsync(job.Id, requestedCancellation, monitorStop.Token);
        try
        {
            var source = OrderXml.Validate(job.SourceXml);
            var input = artifacts.WriteInputs(job);
            var expectedExport = Path.Combine(options.ExportDirectory, job.ExecutionName + ".xml");
            if (File.Exists(expectedExport)) throw new InvalidDataException("The unique export filename already exists; refusing a potentially stale result.");
            var stopped = await process.StopExistingAsync(options.ExecutablePath, linked.Token);
            Log("Stopped existing Astra PIDs: " + (stopped.Count == 0 ? "none" : string.Join(", ", stopped)));
            var exit = await process.RunAsync(new(options.ExecutablePath, input, artifacts.DirectoryFor(job.Id)),
                (id, started) => { store.SetProcess(job.Id, id, started); Log("Started Astra PID " + id); }, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (exit != 0) throw new IOException("Astra exited with code " + exit + ". Inspect the execution logs.");
            if (!File.Exists(expectedExport)) throw new IOException("Astra exited without the expected XML export. Check startup command-line mode, exit export, *.xml filename and export directory.");
            var result = OrderXml.ReadResult(Artifacts.ReadXml(expectedExport), source, job.ExecutionName);
            File.Copy(expectedExport, artifacts.PathFor(job.Id, "result"), false);
            store.Finish(job.Id, JobStatus.Succeeded, result: result);
            Log($"Validated result: {result.SheetCount} sheets, {result.PlacedParts} placed, {result.UnplacedParts} unplaced.");
        }
        catch (OperationCanceledException)
        {
            var cancelled = store.Get(job.Id)!.CancellationRequested;
            var status = cancelled ? JobStatus.Cancelled : deadline.IsCancellationRequested ? JobStatus.TimedOut : JobStatus.Failed;
            var error = cancelled ? null : deadline.IsCancellationRequested
                ? $"Astra exceeded {options.TimeoutSeconds} seconds. Its owned process was stopped; a modal dialog or incomplete launch profile may be responsible."
                : "Host shutdown interrupted the calculation. Its owned process was stopped.";
            store.Finish(job.Id, status, error);
            Log(status + ": " + error);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            store.Finish(job.Id, JobStatus.Failed, e.Message);
            Log("Failed: " + e.Message);
        }
        finally
        {
            await monitorStop.CancelAsync();
            try { await monitor; } catch (OperationCanceledException) { }
        }
        return true;

        void Log(string message)
        {
            Directory.CreateDirectory(artifacts.DirectoryFor(job.Id));
            File.AppendAllText(artifacts.PathFor(job.Id, "log"), $"{DateTimeOffset.UtcNow:O} {message}\n", Encoding.UTF8);
        }
    }

    private FileStream? TryLock()
    {
        // The executable, rather than export directory, scopes stopping existing PIDs.
        var directory = Path.Combine(Path.GetTempPath(), "AstraMcp", "execution-locks");
        Directory.CreateDirectory(directory);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(options.ExecutablePath).ToUpperInvariant())));
        try { return new(Path.Combine(directory, identity + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private async Task MonitorCancellationAsync(Guid id, CancellationTokenSource cancellation, CancellationToken stop)
    {
        while (true)
        {
            await Task.Delay(options.PollIntervalMilliseconds, stop);
            if (store.Get(id)?.CancellationRequested == true) { await cancellation.CancelAsync(); return; }
        }
    }
}
