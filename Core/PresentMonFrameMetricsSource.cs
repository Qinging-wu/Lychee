using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Lychee.Core;

/// <summary>
/// Reads per-present frame times from an installed PresentMon CLI. The process
/// is started only while foreground application monitoring is selected.
/// </summary>
public sealed class PresentMonFrameMetricsSource : IProcessFrameMetricsSource
{
    private readonly string? _executablePath;
    private readonly RollingFrameTimeStatistics _statistics = new(TimeSpan.FromSeconds(60));
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Process? _process;
    private Task? _readerTask;
    private string? _sessionName;
    private string? _status;
    private bool _legacyTraceCleanupAttempted;
    private long _runId;
    private int _isRunning;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastFrameAt;

    public PresentMonFrameMetricsSource(string? executablePath = null)
    {
        _executablePath = executablePath ?? FindPresentMon();
    }

    public string SourceName => "Application presents";
    public bool IsRunning
    {
        get => Volatile.Read(ref _isRunning) != 0;
        private set => Volatile.Write(ref _isRunning, value ? 1 : 0);
    }
    public int? TargetProcessId { get; set; }
    public bool IsAvailable => _executablePath is not null;

    public void Start()
    {
        if (IsRunning) return;

        _statistics.Clear();
        _startedAt = DateTimeOffset.Now;
        _lastFrameAt = null;
        if (_executablePath is null)
        {
            SetStatus("PresentMon was not found on this computer");
            return;
        }

        if (TargetProcessId is not > 0)
        {
            SetStatus("Select an application to start FPS monitoring");
            return;
        }

        if (!HasRealtimeTracePermission())
        {
            SetStatus("Administrator permission required. Open Settings and select Restart as administrator.");
            return;
        }

        if (!TryEnableSystemProfilePrivilege())
        {
            SetStatus("The 'Profile system performance' privilege could not be enabled for FPS tracing.");
            return;
        }

        if (!_legacyTraceCleanupAttempted)
        {
            CleanupLegacyLycheeTraceSessions();
            _legacyTraceCleanupAttempted = true;
        }

        // Reuse one named session so a trace left behind by a terminated
        // PresentMon process can be stopped before the next capture starts.
        var sessionName = $"Lychee_FrameMonitor_{GetCurrentUserSessionKey()}";
        var runId = Interlocked.Increment(ref _runId);
        var cancellation = new CancellationTokenSource();
        var process = new Process { EnableRaisingEvents = true };
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = $"--process_id {TargetProcessId.Value} --output_stdout " +
                        $"--session_name {sessionName} --stop_existing_session " +
                        "--v1_metrics --exclude_dropped --no_track_display " +
                        "--no_track_gpu --no_track_input --no_console_stats " +
                        "--terminate_on_proc_exit",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        process.StartInfo = startInfo;

        try
        {
            AppLog.Info($"Starting PresentMon for PID {TargetProcessId.Value}, session {sessionName}");
            if (!process.Start())
            {
                SetStatus("PresentMon could not be started");
                CleanupFailedStart(process, cancellation);
                _sessionName = null;
                return;
            }

            _cancellation = cancellation;
            _process = process;
            _sessionName = sessionName;
            IsRunning = true;
            SetStatus("Waiting for frame presents...");
            _readerTask = ReadOutputAsync(process, cancellation.Token, runId);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            SetStatus($"PresentMon failed to start: {ex.Message}");
            AppLog.Error("PresentMon start failed", ex);
            CleanupFailedStart(process, cancellation);

            if (ReferenceEquals(_process, process)) _process = null;
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            _sessionName = null;
        }
    }

    public bool TrySample(out FrameMetricsSnapshot snapshot)
    {
        var status = GetStatus();
        var now = DateTimeOffset.Now;
        if (IsRunning && now - _startedAt > TimeSpan.FromSeconds(5))
        {
            var lastFrameAt = GetLastFrameAt();
            if ((lastFrameAt is null || now - lastFrameAt > TimeSpan.FromSeconds(2)) &&
                (string.IsNullOrWhiteSpace(status) || status == "Waiting for frame presents..."))
            {
                status = "No recent frame presents detected for this application";
            }
        }

        snapshot = _statistics.CreateSnapshot(now, SourceName, status);
        return snapshot.CurrentFps.HasValue;
    }

    public void Stop()
    {
        var process = _process;
        var cancellation = _cancellation;
        var readerTask = _readerTask;
        var sessionName = _sessionName;

        IsRunning = false;
        Interlocked.Increment(ref _runId);
        _process = null;
        _cancellation = null;
        _readerTask = null;
        _sessionName = null;

        try { cancellation?.Cancel(); } catch { }

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            AppLog.Info($"Stopping PresentMon session {sessionName}");
            StopTraceSession(sessionName);
        }

        try
        {
            if (process is { HasExited: false })
            {
                if (!process.WaitForExit(1000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
        }

        WaitForReader(readerTask);
        process?.Dispose();
        cancellation?.Dispose();
        _statistics.Clear();
    }

    public void Dispose() => Stop();

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken, long runId)
    {
        Dictionary<string, int>? columns = null;
        Task<string>? errorTask = null;
        try
        {
            errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var fields = ParseCsvLine(line);
                if (columns is null)
                {
                    columns = fields
                        .Select((name, index) => (name, index))
                        .ToDictionary(x => x.name.Trim(), x => x.index, StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                if (!TryGetDouble(fields, columns, "MsBetweenPresents", out var frameTime) &&
                    !TryGetDouble(fields, columns, "MsBetweenDisplayChange", out frameTime))
                {
                    continue;
                }

                if (IsCurrentRun(runId))
                {
                    var capturedAt = DateTimeOffset.Now;
                    _statistics.Add(capturedAt, frameTime);
                    SetLastFrameAt(capturedAt);
                    SetStatus(null);
                }
            }

            var error = await errorTask;
            if (IsCurrentRun(runId) && !cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(error))
            {
                AppLog.Error("PresentMon stderr", error.Trim());
                SetStatus(NormalizeError(error));
            }

            if (IsCurrentRun(runId) && !cancellationToken.IsCancellationRequested)
            {
                await process.WaitForExitAsync(cancellationToken);
                IsRunning = false;
                if (string.IsNullOrWhiteSpace(GetStatus()))
                {
                    SetStatus($"PresentMon stopped (exit code {process.ExitCode})");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentRun(runId))
            {
                IsRunning = false;
                AppLog.Error("PresentMon reader failed", ex);
                SetStatus($"PresentMon reader failed: {ex.Message}");
            }
        }
        finally
        {
            if (errorTask is not null)
            {
                try { await errorTask; } catch { }
            }
        }
    }

    private static bool TryGetDouble(
        IReadOnlyList<string> fields,
        IReadOnlyDictionary<string, int> columns,
        string name,
        out double value)
    {
        value = 0;
        return columns.TryGetValue(name, out var index) &&
               index < fields.Count &&
               double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        result.Add(current.ToString());
        return result;
    }

    private static string NormalizeError(string error)
    {
        if (error.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("administrative privileges", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
        {
            return HasRealtimeTracePermission()
                ? "Windows denied ETW tracing while elevated. Check the 'Profile system performance' user right."
                : "Administrator permission required. Open Settings and select Restart as administrator.";
        }

        if (error.Contains("failed to start trace session", StringComparison.OrdinalIgnoreCase))
        {
            var traceError = error
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.Contains(
                    "failed to start trace session",
                    StringComparison.OrdinalIgnoreCase));
            return $"{traceError ?? "PresentMon could not start the FPS trace."}\n" +
                   "Close other PresentMon/FrameView sessions and try again.";
        }

        return error
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            ?? error.Trim().Split('\n')[0].Trim();
    }

    private void SetStatus(string? status)
    {
        lock (_sync) _status = status;
    }

    private string? GetStatus()
    {
        lock (_sync) return _status;
    }

    private void SetLastFrameAt(DateTimeOffset capturedAt)
    {
        lock (_sync) _lastFrameAt = capturedAt;
    }

    private DateTimeOffset? GetLastFrameAt()
    {
        lock (_sync) return _lastFrameAt;
    }

    private static string? FindPresentMon()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PresentMon_x64.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "FrameViewSDK", "bin", "PresentMon_x64.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Intel", "PresentMon", "PresentMon.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    internal static bool HasRealtimeTracePermission()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return true;

            // BUILTIN\Performance Log Users. Membership becomes active after
            // the user signs out and back in.
            var performanceLogUsers = new SecurityIdentifier("S-1-5-32-559");
            return principal.IsInRole(performanceLogUsers);
        }
        catch
        {
            return false;
        }
    }

    private static string GetCurrentUserSessionKey()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return (identity.User?.Value ?? Environment.UserName)
                .Replace('-', '_')
                .Replace('\\', '_');
        }
        catch
        {
            return Environment.UserName.Replace('\\', '_');
        }
    }

    private static bool TryEnableSystemProfilePrivilege()
    {
        const uint tokenAdjustPrivileges = 0x0020;
        const uint tokenQuery = 0x0008;
        const uint privilegeEnabled = 0x00000002;
        const int errorNotAllAssigned = 1300;

        if (!OpenProcessToken(
                GetCurrentProcess(),
                tokenAdjustPrivileges | tokenQuery,
                out var tokenHandle))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeSystemProfilePrivilege", out var privilegeLuid))
            {
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = privilegeLuid,
                    Attributes = privilegeEnabled
                }
            };

            if (!AdjustTokenPrivileges(
                    tokenHandle,
                    false,
                    ref privileges,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                return false;
            }

            return Marshal.GetLastWin32Error() != errorNotAllAssigned;
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static void CleanupLegacyLycheeTraceSessions()
    {
        try
        {
            var output = RunBoundedProcess(new ProcessStartInfo
            {
                FileName = "logman.exe",
                Arguments = "query -ets",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }, TimeSpan.FromSeconds(3));
            if (output is null) return;

            var stopped = 0;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("Lychee_", StringComparison.OrdinalIgnoreCase)) continue;

                var sessionName = trimmed
                    .Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sessionName))
                {
                    StopTraceSession(sessionName);
                    stopped++;
                }
            }

            if (stopped > 0) AppLog.Info($"Cleaned up {stopped} legacy Lychee trace session(s)");
        }
        catch (Exception ex)
        {
            AppLog.Error("Legacy trace cleanup failed", ex);
            // If enumeration is unavailable, PresentMon will still report the
            // exact StartTrace error and the existing-session flag can recover
            // the current deterministic session name.
        }
    }

    private static void StopTraceSession(string sessionName)
    {
        try
        {
            _ = RunBoundedProcess(new ProcessStartInfo
            {
                FileName = "logman.exe",
                ArgumentList = { "stop", sessionName, "-ets" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to stop trace session '{sessionName}'", ex);
        }
    }

    private static string? RunBoundedProcess(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo);
        if (process is null) return null;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var allTask = Task.WhenAll(stdoutTask, stderrTask, exitTask);

        if (!allTask.Wait(timeout))
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try { allTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        }

        return stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : null;
    }

    private static void CleanupFailedStart(Process process, CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
            cancellation.Dispose();
        }
    }

    private void WaitForReader(Task? readerTask)
    {
        if (readerTask is null || Task.CurrentId == readerTask.Id) return;
        try { readerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private bool IsCurrentRun(long runId) => Volatile.Read(ref _runId) == runId;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

