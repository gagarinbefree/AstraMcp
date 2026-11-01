using AstraMcp.Core;
using AstraMcp.Infrastructure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
    public async Task Background_child_uses_another_desktop_and_releases_it_after_exit()
    {
        var input = Path.Combine(directory, "order with spaces & (детали).xml");
        File.WriteAllText(input, "desktop");
        var originalDesktop = CurrentDesktop();
        int? pid = null;
        var exit = await new AstraProcess().RunAsync(new(executable, input, directory, true), (id, _) => pid = id, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.NotNull(pid);
        var output = File.ReadAllText(Path.Combine(directory, "native-stdout.log"));
        var desktop = output.Split('\n').Single(line => line.StartsWith("Desktop: ", StringComparison.Ordinal))[9..].Trim();
        Assert.StartsWith("AstraMcp-", desktop);
        Assert.NotEqual(originalDesktop, desktop);
        Assert.Equal(originalDesktop, CurrentDesktop());
        Assert.Contains(input, output);
        Assert.Contains("Window created: yes", output);
        Assert.Contains("Window visible on child desktop: True", output);
        Assert.Contains("fixture stderr", File.ReadAllText(Path.Combine(directory, "native-stderr.log")));
        Assert.True(SpinWait.SpinUntil(() => !DesktopExists(desktop), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Background_cancellation_stops_owned_child_and_releases_desktop()
    {
        var input = Path.Combine(directory, "hang.xml");
        File.WriteAllText(input, "desktop-hang");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new AstraProcess().RunAsync(new(executable, input, directory, true), (id, _) => started.TrySetResult(id), cancellation.Token);
        var pid = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string output = "";
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!output.Contains("Window created: yes", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                using var stream = new FileStream(Path.Combine(directory, "native-stdout.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                output = reader.ReadToEnd();
                await Task.Delay(25);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        Assert.Contains("Window created: yes", output);
        Assert.False(IsProcessRunning(pid));
        var desktop = output.Split('\n').Single(line => line.StartsWith("Desktop: ", StringComparison.Ordinal))[9..].Trim();
        Assert.True(SpinWait.SpinUntil(() => !DesktopExists(desktop), TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Background_start_failure_releases_desktop_without_visible_fallback()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new AstraProcess().RunAsync(
            new(Path.Combine(directory, "missing.exe"), "input.xml", directory, true), (_, _) => { }, CancellationToken.None));
        var log = File.ReadAllText(Path.Combine(directory, "execution.log"));
        var desktop = log.Split('\n').Single(line => line.StartsWith("Native desktop: ", StringComparison.Ordinal))[16..].Trim();
        Assert.StartsWith("AstraMcp-", desktop);
        Assert.False(DesktopExists(desktop));
    }

    [Fact]
    public async Task Visible_mode_uses_the_original_desktop()
    {
        var input = Path.Combine(directory, "visible.xml");
        File.WriteAllText(input, "desktop");
        var desktop = CurrentDesktop();
        Assert.Equal(0, await new AstraProcess().RunAsync(new(executable, input, directory, false), (_, _) => { }, CancellationToken.None));
        var output = File.ReadAllText(Path.Combine(directory, "native-stdout.log"));
        Assert.Contains("Desktop: " + desktop, output);
        Assert.Contains("Window created: no", output);
    }

    [Fact]
    public async Task Failed_pid_persistence_stops_suspended_child_and_releases_desktop()
    {
        var input = Path.Combine(directory, "suspended.xml");
        File.WriteAllText(input, "hang");
        int? pid = null;
        await Assert.ThrowsAsync<IOException>(() => new AstraProcess().RunAsync(new(executable, input, directory, true),
            (id, _) => { pid = id; throw new IOException("PID persistence failed."); }, CancellationToken.None));
        Assert.NotNull(pid);
        Assert.False(IsProcessRunning(pid.Value));
        var desktop = File.ReadAllText(Path.Combine(directory, "execution.log")).Split('\n')
            .Single(line => line.StartsWith("Native desktop: ", StringComparison.Ordinal))[16..].Trim();
        Assert.False(DesktopExists(desktop));
    }

    [Fact]
    public async Task Stops_existing_matching_process_by_pid_before_next_execution()
    {
        var input = Path.Combine(directory, "hang.xml");
        File.WriteAllText(input, "hang");
        using var child = Process.Start(AstraProcess.CreateStartInfo(new(executable, input, directory)))!;
        try
        {
            // Process.Start returns before the image and managed entry point are ready.
            Assert.Contains("fixture stdout", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var stopped = await new AstraProcess().StopExistingAsync(executable, CancellationToken.None);
            Assert.Contains(child.Id, stopped);
            Assert.True(child.HasExited);
        }
        finally { if (!child.HasExited) child.Kill(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_stops_only_the_owned_child_and_preserves_other_processes(bool background)
    {
        var input = Path.Combine(directory, "hang.xml");
        File.WriteAllText(input, "hang");
        using var other = Process.Start(AstraProcess.CreateStartInfo(new(executable, input, directory)))!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        int? ownedPid = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AstraProcess().RunAsync(new(executable, input, directory, background), (pid, _) => ownedPid = pid, cancellation.Token));
            Assert.NotNull(ownedPid);
            Assert.False(other.HasExited);
            Assert.False(IsProcessRunning(ownedPid.Value));
        }
        finally { if (!other.HasExited) { other.Kill(true); await other.WaitForExitAsync(); } }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    // Exited processes can remain enumerable while another monitor retains a process handle.
    private static bool IsProcessRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string CurrentDesktop()
    {
        var buffer = new StringBuilder(256);
        Assert.True(GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, buffer, buffer.Capacity * 2, out _));
        return buffer.ToString();
    }

    private static bool DesktopExists(string name)
    {
        var handle = OpenDesktopW(name, 0, false, 1);
        if (handle == 0) return false;
        CloseDesktop(handle);
        return true;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenDesktopW(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
}
