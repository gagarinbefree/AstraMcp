using AstraMcp.Core;
using AstraMcp.Infrastructure;

namespace AstraMcp.Tests;

public sealed class ExecutionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "astra-mcp-tests", Guid.NewGuid().ToString("N"));
    private readonly AdapterOptions options;
    private readonly SqliteJobStore store;
    private readonly FakeAstra process = new();

    public ExecutionTests()
    {
        Directory.CreateDirectory(directory);
        options = new() { DataDirectory = directory, ExportDirectory = Path.Combine(directory, "exchange"), ExecutablePath = Path.Combine(directory, "Astra.exe"), ProfileReady = true, TimeoutSeconds = 1, PollIntervalMilliseconds = 25 };
        File.WriteAllText(options.ExecutablePath, "test fixture, never executed");
        store = new(options);
        process.Options = options;
    }

    [Fact]
    public async Task Stops_previous_processes_then_runs_and_validates_native_xml()
    {
        var job = store.Enqueue(OrderXmlTests.Source, "success");
        Assert.True(await new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None));
        var finished = store.Get(job.Id)!;
        Assert.Equal(JobStatus.Succeeded, finished.Status);
        Assert.Equal(new ResultInfo(1, 1, 3), finished.Result);
        Assert.Equal(new[] { "stop", "run" }, process.Calls);
        Assert.Equal(234, finished.ProcessId);
        var artifacts = Path.Combine(directory, "jobs", job.Id.ToString("D"));
        Assert.True(File.Exists(Path.Combine(artifacts, "result.astra.xml")));
        Assert.True(File.Exists(Path.Combine(artifacts, "manifest.json")));
        Assert.Equal(OrderXmlTests.Source, File.ReadAllText(Path.Combine(artifacts, "input.original.xml")));
    }

    [Fact]
    public async Task Rejects_stale_result_even_if_native_exit_code_is_zero()
    {
        process.Mode = "stale";
        var job = store.Enqueue(OrderXmlTests.Source, "stale");
        await new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Failed, store.Get(job.Id)!.Status);
        Assert.Contains("different order", store.Get(job.Id)!.Error!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("exit-error")]
    [InlineData("stop-error")]
    public async Task Reports_failed_native_execution_without_claiming_success(string mode)
    {
        process.Mode = mode;
        var job = store.Enqueue(OrderXmlTests.Source, mode);
        await new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Failed, store.Get(job.Id)!.Status);
        Assert.Null(store.Get(job.Id)!.Result);
        if (mode == "stop-error") Assert.DoesNotContain("run", process.Calls);
    }

    [Fact]
    public async Task Timeout_cancels_owned_calculation_and_records_deadline()
    {
        process.Mode = "hang";
        var job = store.Enqueue(OrderXmlTests.Source, "timeout");
        await new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None);
        Assert.Equal(JobStatus.TimedOut, store.Get(job.Id)!.Status);
        Assert.True(process.CancellationObserved);
    }

    [Fact]
    public async Task Running_cancellation_is_observed_by_worker()
    {
        process.Mode = "hang";
        var job = store.Enqueue(OrderXmlTests.Source, "cancel");
        var execution = new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None);
        await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Cancel(job.Id);
        await execution;
        Assert.Equal(JobStatus.Cancelled, store.Get(job.Id)!.Status);
        Assert.True(process.CancellationObserved);
    }

    [Fact]
    public async Task Shared_profile_lock_prevents_other_servers_from_stopping_active_calculation()
    {
        process.Mode = "hang";
        var job = store.Enqueue(OrderXmlTests.Source, "one");
        var first = new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None);
        await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Enqueue(OrderXmlTests.Source, "two");
        Assert.False(await new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None));
        Assert.Equal(1, process.Calls.Count(c => c == "stop"));
        store.Cancel(job.Id);
        await first;
    }

    [Fact]
    public async Task Unconfigured_profile_leaves_orders_queued()
    {
        options.ProfileReady = false;
        var job = store.Enqueue(OrderXmlTests.Source, "not-ready");
        Assert.False(await new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None));
        Assert.Equal(JobStatus.Queued, store.Get(job.Id)!.Status);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Same_executable_with_another_export_directory_cannot_interrupt_running_process()
    {
        process.Mode = "hang";
        var job = store.Enqueue(OrderXmlTests.Source, "lock-scope");
        var first = new JobExecutor(options, store, process).ExecuteNextAsync(CancellationToken.None);
        await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = new AdapterOptions
        {
            DataDirectory = Path.Combine(directory, "other-queue"),
            ExportDirectory = Path.Combine(directory, "other-export"),
            ExecutablePath = options.ExecutablePath,
            ProfileReady = true,
            TimeoutSeconds = 1
        };
        var otherStore = new SqliteJobStore(other);
        var otherJob = otherStore.Enqueue(OrderXmlTests.Source, "other-profile");
        try
        {
            Assert.False(await new JobExecutor(other, otherStore, process).ExecuteNextAsync(CancellationToken.None));
            Assert.Equal(JobStatus.Queued, otherStore.Get(otherJob.Id)!.Status);
        }
        finally { store.Cancel(job.Id); await first; }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class FakeAstra : IAstraProcess
    {
        public AdapterOptions Options { private get; set; } = new();
        public string Mode { get; set; } = "success";
        public List<string> Calls { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public Task<IReadOnlyList<int>> StopExistingAsync(string executablePath, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            if (Mode == "stop-error") throw new IOException("Cannot stop PID 99.");
            return Task.FromResult<IReadOnlyList<int>>([99]);
        }

        public async Task<int> RunAsync(ProcessRequest request, Action<int, DateTimeOffset> onStarted, CancellationToken cancellationToken)
        {
            Calls.Add("run");
            onStarted(234, DateTimeOffset.UtcNow);
            Started.TrySetResult();
            if (Mode == "hang")
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { CancellationObserved = true; throw; }
            }
            if (Mode == "exit-error") return 7;
            if (Mode != "missing")
            {
                var name = System.Xml.Linq.XDocument.Load(request.InputPath).Root!.Element("data_order")!.Attribute("name")!.Value;
                Directory.CreateDirectory(Options.ExportDirectory);
                await File.WriteAllTextAsync(Path.Combine(Options.ExportDirectory, name + ".xml"), OrderXmlTests.Result(Mode == "stale" ? "wrong-name" : name, 1), cancellationToken);
            }
            return 0;
        }
    }
}
