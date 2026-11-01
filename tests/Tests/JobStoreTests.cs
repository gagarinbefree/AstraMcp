using AstraMcp.Core;
using AstraMcp.Infrastructure;

namespace AstraMcp.Tests;

public sealed class JobStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "astra-mcp-tests", Guid.NewGuid().ToString("N"));
    private SqliteJobStore Store() => new(new() { DataDirectory = directory });

    [Fact]
    public void Persists_requests_across_store_instances_and_deduplicates_retries()
    {
        var job = Store().Enqueue(OrderXmlTests.Source, "retry-1");
        var reloaded = Store().Enqueue(OrderXmlTests.Source, "retry-1");
        Assert.Equal(job.Id, reloaded.Id);
        Assert.Equal(OrderXmlTests.Source, Store().Get(job.Id)!.SourceXml);
        Assert.Throws<InvalidOperationException>(() => Store().Enqueue(OrderXmlTests.Source + " ", "retry-1"));
    }

    [Fact]
    public async Task Competing_stores_can_claim_only_one_running_job()
    {
        var first = Store().Enqueue(OrderXmlTests.Source, "first");
        Store().Enqueue(OrderXmlTests.Source, "second");
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Store().TryClaim())));
        var claimed = Assert.Single(claims.OfType<Job>());
        Assert.Equal(first.Id, claimed.Id);
        Assert.Null(Store().TryClaim());
        Store().Finish(first.Id, JobStatus.Failed, "test failure");
        Assert.NotNull(Store().TryClaim());
    }

    [Fact]
    public void Queued_cancellation_is_terminal_and_running_cancellation_wins_completion_race()
    {
        var store = Store();
        var queued = store.Enqueue(OrderXmlTests.Source, "queued");
        Assert.Equal(JobStatus.Cancelled, store.Cancel(queued.Id).Status);
        Assert.Null(store.TryClaim());
        var running = store.Enqueue(OrderXmlTests.Source, "running");
        store.TryClaim();
        Assert.Equal(JobStatus.Running, store.Cancel(running.Id).Status);
        Assert.True(store.Get(running.Id)!.CancellationRequested);
        var finished = store.Finish(running.Id, JobStatus.Succeeded, result: new(1, 4, 0));
        Assert.Equal(JobStatus.Cancelled, finished.Status);
        Assert.Null(finished.Result);
        Assert.Equal(finished, store.Cancel(running.Id));
    }

    [Fact]
    public void Recovers_interrupted_calculations_without_reexecuting_them()
    {
        var store = Store();
        var first = store.Enqueue(OrderXmlTests.Source, "interrupted");
        store.TryClaim();
        store.SetProcess(first.Id, 123, DateTimeOffset.UtcNow);
        var next = store.Enqueue(OrderXmlTests.Source, "next");
        Assert.Equal(1, Store().RecoverInterrupted());
        var failed = store.Get(first.Id)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Equal(123, failed.ProcessId);
        Assert.Contains("interrupted", failed.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(next.Id, store.TryClaim()!.Id);
    }

    [Fact]
    public void Resolves_configuration_paths_against_config_file_directory()
    {
        var options = new AdapterOptions { ExecutablePath = "tools/Astra.exe", DataDirectory = "jobs", ExportDirectory = "exchange" };
        options.Normalize(directory);
        Assert.Equal(Path.Combine(directory, "tools", "Astra.exe"), options.ExecutablePath);
        Assert.Equal(Path.Combine(directory, "jobs"), options.DataDirectory);
        options.TimeoutSeconds = 0;
        Assert.Throws<ArgumentException>(() => options.Normalize(directory));
    }

    [Fact]
    public void Publishes_artifacts_before_job_becomes_visible_and_manifests_follow_committed_state()
    {
        var store = Store();
        var job = store.Enqueue(OrderXmlTests.Source, "artifacts");
        var artifacts = new Artifacts(new() { DataDirectory = directory });
        Assert.True(File.Exists(artifacts.PathFor(job.Id, "input")));
        store.Cancel(job.Id);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(artifacts.PathFor(job.Id, "manifest")));
        Assert.Equal("Cancelled", manifest.RootElement.GetProperty("Status").GetString());
        Assert.Equal(JobStatus.Cancelled, store.Get(job.Id)!.Status);
    }

    [Fact]
    public void Rejects_different_execution_profiles_sharing_one_queue_directory()
    {
        Store().Enqueue(OrderXmlTests.Source, "profile");
        Assert.Throws<InvalidOperationException>(() => new SqliteJobStore(new()
        {
            DataDirectory = directory,
            ExecutablePath = Path.Combine(directory, "another.exe")
        }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
