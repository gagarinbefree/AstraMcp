namespace AstraMcp.Core;

/// <summary>Describes a shell-free native invocation owned by one job.</summary>
/// <param name="ExecutablePath">Configured executable path.</param>
/// <param name="InputPath">Generated execution input path.</param>
/// <param name="LogDirectory">Directory for native stdout and stderr.</param>
public sealed record ProcessRequest(string ExecutablePath, string InputPath, string LogDirectory);

/// <summary>Provides isolated process control for the native application.</summary>
public interface IAstraProcess
{
    /// <summary>Stops existing matching executables by PID before a new calculation.</summary>
    /// <param name="executablePath">Exact executable path used to filter matching processes.</param>
    /// <param name="cancellationToken">Cancellation token for waiting.</param>
    /// <returns>The identifiers of processes stopped by this call.</returns>
    Task<IReadOnlyList<int>> StopExistingAsync(string executablePath, CancellationToken cancellationToken);

    /// <summary>Runs the native import, calculation and exit workflow without a command shell.</summary>
    /// <param name="request">Validated process invocation.</param>
    /// <param name="onStarted">Callback persisting the child PID and UTC start time.</param>
    /// <param name="cancellationToken">Cancellation token that stops only the owned calculation.</param>
    /// <returns>The native process exit code.</returns>
    Task<int> RunAsync(ProcessRequest request, Action<int, DateTimeOffset> onStarted, CancellationToken cancellationToken);
}
