using AstraMcp.Core;
using AstraMcp.Infrastructure;
using System.Diagnostics;

namespace AstraMcp.Tests;

public sealed class NativeProcessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "astra-mcp-tests", Guid.NewGuid().ToString("N"));
    private readonly string executable;

    public NativeProcessTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AstraMcp.sln"))) root = root.Parent!;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        executable = Path.Combine(root.FullName, "tests", "NativeFixture", "bin", configuration, "net10.0", "NativeFixture.exe");
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public void Passes_input_as_single_argument_and_never_transfers_to_production()
    {
        var request = new ProcessRequest(executable, Path.Combine(directory, "order with spaces.xml"), directory);
        var start = AstraProcess.CreateStartInfo(request);
        Assert.False(start.UseShellExecute);
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
        Assert.Equal(new[] { request.InputPath, "-i", "-n", "-q" }, start.ArgumentList);
    }

    [Fact]
    public async Task Captures_real_child_output_without_contaminating_mcp_stdout()
    {
        var input = Path.Combine(directory, "order with spaces.xml");
        File.WriteAllText(input, "normal");
        int? pid = null;
        var exit = await new AstraProcess().RunAsync(new(executable, input, directory), (id, _) => pid = id, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.NotNull(pid);
        Assert.Contains(input, File.ReadAllText(Path.Combine(directory, "native-stdout.log")));
        Assert.Contains("fixture stderr", File.ReadAllText(Path.Combine(directory, "native-stderr.log")));
    }

    [Fact]
    public async Task Stops_existing_matching_process_by_pid_before_next_execution()
    {
        var input = Path.Combine(directory, "hang.xml");
        File.WriteAllText(input, "hang");
        using var child = Process.Start(AstraProcess.CreateStartInfo(new(executable, input, directory)))!;
        try
        {
            var stopped = await new AstraProcess().StopExistingAsync(executable, CancellationToken.None);
            Assert.Contains(child.Id, stopped);
            Assert.True(child.HasExited);
        }
        finally { if (!child.HasExited) child.Kill(true); }
    }

    [Fact]
    public async Task Cancellation_stops_only_the_owned_child_and_preserves_other_processes()
    {
        var input = Path.Combine(directory, "hang.xml");
        File.WriteAllText(input, "hang");
        using var other = Process.Start(AstraProcess.CreateStartInfo(new(executable, input, directory)))!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        int? ownedPid = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AstraProcess().RunAsync(new(executable, input, directory), (pid, _) => ownedPid = pid, cancellation.Token));
            Assert.NotNull(ownedPid);
            Assert.False(other.HasExited);
            Assert.DoesNotContain(Process.GetProcessesByName("NativeFixture"), p => p.Id == ownedPid);
        }
        finally { if (!other.HasExited) { other.Kill(true); await other.WaitForExitAsync(); } }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
