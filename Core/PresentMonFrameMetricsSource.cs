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
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastFrameAt;

    public PresentMonFrameMetricsSource(string? executablePath = null)
    {
        _executablePath = executablePath ?? FindPresentMon();
    }

    public string SourceName => "Application presents";
    public bool IsRunning { get; private set; }
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
        _sessionName = sessionName;
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

        try
        {
            _cancellation = new CancellationTokenSource();
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!_process.Start())
            {
                SetStatus("PresentMon could not be started");
                return;
            }

            IsRunning = true;
            SetStatus("Waiting for frame presents...");
            _readerTask = ReadOutputAsync(_process, _cancellation.Token);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            SetStatus($"PresentMon failed to start: {ex.Message}");
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
        IsRunning = false;
        _cancellation?.Cancel();

        if (!string.IsNullOrWhiteSpace(_sessionName))
        {
            StopTraceSession(_sessionName);
        }

        try
        {
            if (_process is { HasExited: false })
            {
                if (!_process.WaitForExit(1000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;
        _sessionName = null;
        _cancellation?.Dispose();
        _cancellation = null;
        _readerTask = null;
        _statistics.Clear();
    }

    public void Dispose() => Stop();

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        Dictionary<string, int>? columns = null;
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
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

                _statistics.Add(DateTimeOffset.Now, frameTime);
                SetLastFrameAt(DateTimeOffset.Now);
                SetStatus(null);
            }

            var error = await errorTask;
            if (!cancellationToken.IsCancellationRequested && !string.IsNullOrWhiteSpace(error))
            {
                SetStatus(NormalizeError(error));
            }

            if (!cancellationToken.IsCancellationRequested)
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
            IsRunning = false;
            SetStatus($"PresentMon reader failed: {ex.Message}");
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
            using var query = Process.Start(new ProcessStartInfo
            {
                FileName = "logman.exe",
                Arguments = "query -ets",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (query is null) return;

            var output = query.StandardOutput.ReadToEnd();
            query.WaitForExit(3000);
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
                }
            }
        }
        catch
        {
            // If enumeration is unavailable, PresentMon will still report the
            // exact StartTrace error and the existing-session flag can recover
            // the current deterministic session name.
        }
    }

    private static void StopTraceSession(string sessionName)
    {
        try
        {
            using var stop = Process.Start(new ProcessStartInfo
            {
                FileName = "logman.exe",
                ArgumentList = { "stop", sessionName, "-ets" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (stop is null) return;

            stop.StandardOutput.ReadToEnd();
            stop.StandardError.ReadToEnd();
            stop.WaitForExit(3000);
        }
        catch
        {
        }
    }

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
