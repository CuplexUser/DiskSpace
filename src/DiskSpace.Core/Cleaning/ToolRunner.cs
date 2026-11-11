using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskSpace.Core.Cleaning;

/// <summary>How a tool run ended.</summary>
public enum ToolOutcome
{
    /// <summary>The tool ran to completion; <see cref="ToolResult.ExitCode"/> is its answer.</summary>
    Completed,

    /// <summary>Nothing by that name is on the PATH.</summary>
    NotFound,

    /// <summary>It was found but could not be started the way this class insists on.</summary>
    NotStarted,

    /// <summary>It ran past its time limit and was stopped.</summary>
    TimedOut,
}

public readonly record struct ToolResult(ToolOutcome Outcome, int ExitCode = -1)
{
    public bool Succeeded => Outcome == ToolOutcome.Completed && ExitCode == 0;
}

/// <summary>
/// Runs a developer tool found on the PATH: npm, pip, dotnet and the like.
///
/// The one rule that matters here is that such a tool never runs elevated. This application
/// holds an administrator token, and these tools live in folders any program running as the
/// user can write to, read configuration from the same places and, in npm's case, are scripts.
/// Starting one with this process's token would hand administrator rights to whatever
/// happened to be sitting at that path. So when the process is elevated the tool is started
/// with the desktop shell's token instead, which is the user's ordinary one, and when that
/// cannot be done the tool is not started at all.
/// </summary>
public static class ToolRunner
{
    private static readonly string[] ScriptExtensions = [".cmd", ".bat"];

    public static ToolResult Run(
        string executable,
        string arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var path = FindOnPath(executable);
        if (path is null)
            return new ToolResult(ToolOutcome.NotFound);

        // A .cmd shim is not an executable image; cmd.exe has to be the one that runs it.
        var (file, commandArguments) = ScriptExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{path}\" {arguments}\"")
            : (path, arguments);

        var limit = timeout ?? TimeSpan.FromMinutes(10);

        try
        {
            return Environment.IsPrivilegedProcess
                ? RunWithShellToken(file, commandArguments, environment, limit, cancellationToken)
                : RunDirectly(file, commandArguments, environment, limit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ToolResult(ToolOutcome.NotStarted);
        }
    }

    /// <summary>
    /// Resolves a bare tool name the way a command prompt would, minus the current directory:
    /// each PATH entry in order, each PATHEXT extension in order.
    /// </summary>
    public static string? FindOnPath(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
            return File.Exists(executable) ? executable : null;

        var extensions = Path.HasExtension(executable)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), executable + extension);
                    if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is skipped like a missing one.
                }
            }
        }

        return null;
    }

    private static string WorkingDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static ToolResult RunDirectly(
        string file,
        string arguments,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory,
        };

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;

        using var process = Process.Start(start);
        if (process is null)
            return new ToolResult(ToolOutcome.NotStarted);

        var deadline = Stopwatch.StartNew();

        while (!process.WaitForExit(200))
        {
            if (cancellationToken.IsCancellationRequested || deadline.Elapsed > timeout)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
                return new ToolResult(ToolOutcome.TimedOut);
            }
        }

        return new ToolResult(ToolOutcome.Completed, process.ExitCode);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    // ---- Starting a process as the unelevated user, from an elevated one ---------------------

    private const uint ProcessQueryInformation = 0x0400;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAccessForNewProcess = 0x018B; // query, duplicate, assign primary, adjust default and session id
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x0400;
    private const uint StartfUseShowWindow = 0x0001;
    private const uint WaitTimeout = 0x0102;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved3;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        nint token, uint access, nint attributes, int impersonationLevel, int tokenType, out nint duplicate);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        nint token,
        uint logonFlags,
        string? application,
        StringBuilder commandLine,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// Borrows the token of the process that owns the desktop, which is Explorer running as the
    /// signed-in user without elevation, and starts the tool with that.
    /// </summary>
    private static ToolResult RunWithShellToken(
        string file,
        string arguments,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        nint shellProcess = 0, shellToken = 0, userToken = 0, environmentBlock = 0;
        var started = default(ProcessInformation);

        try
        {
            var shell = GetShellWindow();
            if (shell == 0 || GetWindowThreadProcessId(shell, out var shellProcessId) == 0)
                return new ToolResult(ToolOutcome.NotStarted); // No desktop shell to borrow from.

            shellProcess = OpenProcess(ProcessQueryInformation, false, shellProcessId);
            if (shellProcess == 0
                || !OpenProcessToken(shellProcess, TokenDuplicate, out shellToken)
                || !DuplicateTokenEx(
                    shellToken, TokenAccessForNewProcess, 0, SecurityImpersonation, TokenPrimary, out userToken))
            {
                throw new Win32Exception();
            }

            environmentBlock = BuildEnvironmentBlock(environment);

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Flags = StartfUseShowWindow,
                ShowWindow = 0, // SW_HIDE: the console this call always creates stays out of sight.
            };

            var commandLine = new StringBuilder($"\"{file}\" {arguments}");

            if (!CreateProcessWithTokenW(
                    userToken, 0, file, commandLine, CreateUnicodeEnvironment, environmentBlock,
                    WorkingDirectory, ref startup, out started))
            {
                throw new Win32Exception();
            }

            var deadline = Stopwatch.StartNew();

            while (WaitForSingleObject(started.Process, 200) == WaitTimeout)
            {
                if (cancellationToken.IsCancellationRequested || deadline.Elapsed > timeout)
                {
                    TerminateProcess(started.Process, 1);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ToolResult(ToolOutcome.TimedOut);
                }
            }

            return GetExitCodeProcess(started.Process, out var exitCode)
                ? new ToolResult(ToolOutcome.Completed, unchecked((int)exitCode))
                : new ToolResult(ToolOutcome.NotStarted);
        }
        finally
        {
            if (environmentBlock != 0)
                Marshal.FreeHGlobal(environmentBlock);

            foreach (var handle in new[] { started.Thread, started.Process, userToken, shellToken, shellProcess })
            {
                if (handle != 0)
                    CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// This process's environment plus the overrides, in the double-null-terminated form
    /// CreateProcess takes. Sorted, because the loader expects it to be.
    /// </summary>
    private static nint BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.Length > 0)
                variables[name] = entry.Value as string ?? string.Empty;
        }

        foreach (var (name, value) in overrides ?? new Dictionary<string, string>())
            variables[name] = value;

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
            block.Append(name).Append('=').Append(value).Append('\0');

        block.Append('\0');

        var characters = block.ToString().ToCharArray();
        var memory = Marshal.AllocHGlobal(characters.Length * sizeof(char));
        Marshal.Copy(characters, 0, memory, characters.Length);
        return memory;
    }
}
