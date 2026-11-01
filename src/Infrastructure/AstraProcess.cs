using System.Diagnostics;
using AstraMcp.Core;

namespace AstraMcp.Infrastructure;

/// <summary>Starts Astra directly and terminates verified processes by PID.</summary>
public sealed class AstraProcess : IAstraProcess
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> StopExistingAsync(string executablePath, CancellationToken cancellationToken)
    {
        var stopped = new List<int>();
        var expected = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited) continue;
                    if (!string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    var pid = process.Id;
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    stopped.Add(pid);
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new IOException($"Cannot verify or stop Astra PID {process.Id}. No new calculation was started. {e.Message}", e);
                }
            }
        }
        return stopped;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(ProcessRequest request, Action<int, DateTimeOffset> onStarted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.LogDirectory);
        if (request.RunInBackground)
            return await NativeDesktopProcess.RunAsync(request, onStarted, cancellationToken);
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        using var output = File.Create(Path.Combine(request.LogDirectory, "native-stdout.log"));
        using var error = File.Create(Path.Combine(request.LogDirectory, "native-stderr.log"));
        if (!process.Start()) throw new IOException("Astra process could not be started.");
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
        var stderr = process.StandardError.BaseStream.CopyToAsync(error, CancellationToken.None);
        try
        {
            onStarted(process.Id, process.StartTime.ToUniversalTime());
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Builds a shell-free invocation with individually quoted arguments and no production flag.</summary>
    /// <param name="request">Configured executable, generated input and log directory.</param>
    /// <returns>The process configuration used for native execution.</returns>
    public static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(request.ExecutablePath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(request.InputPath);
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("-q");
        return start;
    }
}
