using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AstraMcp.Core;
using Microsoft.Win32.SafeHandles;

namespace AstraMcp.Infrastructure;

// A private desktop keeps all native GUI windows away from the input desktop.
// It is never made interactive, and failure never falls back to a visible launch.
internal static class NativeDesktopProcess
{
    internal static async Task<int> RunAsync(ProcessRequest request, Action<int, DateTimeOffset> onStarted, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Background desktop execution requires Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        var desktopName = "AstraMcp-" + Guid.NewGuid().ToString("N");
        using var desktop = new DesktopHandle(CreateDesktopW(desktopName, 0, 0, 0, 0x0083, 0));
        if (desktop.IsInvalid) throw Error("Cannot create the background desktop. No visible fallback was started");
        File.AppendAllText(Path.Combine(request.LogDirectory, "execution.log"), "Native desktop: " + desktopName + "\n");

        // Only these three explicit handles are inherited; MCP stdin is never passed to Astra.
        using var input = OpenFile("NUL", 0x80000000, 3);
        using var output = OpenFile(Path.Combine(request.LogDirectory, "native-stdout.log"), 0x40000000, 2);
        using var error = OpenFile(Path.Combine(request.LogDirectory, "native-stderr.log"), 0x40000000, 2);
        using var attributes = new HandleList(input, output, error);
        var startup = new StartupInfoEx
        {
            StartupInfo = new()
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Desktop = desktopName,
                Flags = 0x00000180, // STARTF_USESTDHANDLES | STARTF_FORCEOFFFEEDBACK
                StandardInput = input.DangerousGetHandle(),
                StandardOutput = output.DangerousGetHandle(),
                StandardError = error.DangerousGetHandle()
            },
            AttributeList = attributes.Pointer
        };
        var command = new StringBuilder(string.Join(" ", new[] { request.ExecutablePath, request.InputPath, "-i", "-n", "-q" }.Select(Quote)));
        if (command.Length >= 32767) throw new ArgumentException("The native command line exceeds the Windows limit.");
        // Suspend until the managed process owns a handle and its PID has been persisted.
        const uint flags = 0x08080004; // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED
        if (!CreateProcessW(request.ExecutablePath, command, 0, 0, true, flags, 0,
            Path.GetDirectoryName(request.ExecutablePath)!, ref startup, out var information))
            throw Error("Cannot start Astra on the background desktop");
        using var nativeProcess = new SafeProcessHandle(information.Process, true);
        using var primaryThread = new SafeWaitHandle(information.Thread, true);
        Process? process = null;
        try
        {
            process = Process.GetProcessById(checked((int)information.ProcessId));
            _ = process.Handle;
            onStarted(process.Id, process.StartTime.ToUniversalTime());
            cancellationToken.ThrowIfCancellationRequested();
            if (ResumeThread(primaryThread) == uint.MaxValue) throw Error("Cannot resume the isolated Astra process");
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        finally
        {
            try
            {
                if (process is null)
                {
                    if (!TerminateProcess(nativeProcess, 1)) throw Error("Cannot stop the suspended Astra process");
                }
                else if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally { process?.Dispose(); }
        }
    }

    private static SafeFileHandle OpenFile(string path, uint access, uint disposition)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        var handle = CreateFileW(path, access, 3, ref security, disposition, 0x80, 0);
        if (!handle.IsInvalid) return handle;
        var exception = Error("Cannot open a native standard stream");
        handle.Dispose();
        throw exception;
    }

    // Windows CRT quoting is required even without a shell, including trailing backslashes.
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Native arguments must not contain null characters.");
        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            builder.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            builder.Append(character);
            slashes = 0;
        }
        builder.Append('\\', slashes * 2);
        return builder.Append('"').ToString();
    }

    private static Win32Exception Error(string message)
    {
        var code = Marshal.GetLastWin32Error();
        return new(code, message + ": " + new Win32Exception(code).Message);
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal DesktopHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseDesktop(handle);
    }

    private sealed class HandleList : IDisposable
    {
        internal nint Pointer { get; private set; }
        private nint handles;
        private bool initialized;

        internal HandleList(params SafeFileHandle[] files)
        {
            nuint length = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref length);
            if (length == 0) throw Error("Cannot size the process attribute list");
            Pointer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref length)) throw Error("Cannot initialize the process attribute list");
                initialized = true;
                handles = Marshal.AllocHGlobal(files.Length * nint.Size);
                for (var i = 0; i < files.Length; i++) Marshal.WriteIntPtr(handles, i * nint.Size, files[i].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, 0x00020002, handles, (nuint)(files.Length * nint.Size), 0, 0))
                    throw Error("Cannot restrict inherited process handles");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Pointer != 0)
            {
                if (initialized) DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer);
                Pointer = 0;
            }
            if (handles != 0) { Marshal.FreeHGlobal(handles); handles = 0; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal nint ReservedPointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes security, uint disposition, uint attributes, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string executable, StringBuilder command, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
