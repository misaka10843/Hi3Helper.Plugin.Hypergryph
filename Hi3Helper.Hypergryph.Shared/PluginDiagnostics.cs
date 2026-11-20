using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Hi3Helper.Plugin.Core;

namespace Hi3Helper.Hypergryph.Shared;

/// <summary>A plugin-owned adapter for the standard Core logger callback.</summary>
public static unsafe class PluginDiagnostics
{
    private static readonly object Gate = new();
    private const string LogTimestampFormat = "yyyyMMdd'T'HHmmss.fffffff'Z'";
    private const int RetainedSessions = 3;
    private static readonly HashSet<string> SessionFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Encoding LogEncoding = new UTF8Encoding(false);
    private static readonly string SessionFileName = GetSessionFileName();
    private static bool _initialized;
    private static string? _gamePath;
    private static bool _reportedFailure;
    private static nint _host;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            void* original = null;
            fixed (char* name = "SetLoggerCallback")
            {
                if (SharedStatic.TryGetApiExportPointer(name, &original) != 0 || original == null)
                    throw new InvalidOperationException("Core does not expose its standard logger callback.");
            }
            ((delegate* unmanaged[Cdecl]<nint, void>)original)((nint)(delegate* unmanaged[Cdecl]<int*, void*, char*, int, void>)&Capture);
            _initialized = true;
        }
    }

    public static void SetGamePath(string? path)
    {
        Initialize();
        lock (Gate)
        {
            string? fullPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            if (!string.Equals(_gamePath, fullPath, StringComparison.OrdinalIgnoreCase)) _reportedFailure = false;
            _gamePath = fullPath;
        }
    }

    internal static nint Setter => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&SetHost;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetHost(nint callback)
    {
        lock (Gate) _host = callback;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Capture(int* level, void* eventId, char* message, int length)
    {
        // Do not let managed exceptions cross this native callback boundary.
        try
        {
            lock (Gate)
            {
                if (level == null || length < 0 || message == null && length != 0) return;
                if (*level is >= 2 and <= 5 && _gamePath != null && Directory.Exists(_gamePath))
                {
                    try
                    {
                        WriteSessionLog(_gamePath, *level, new ReadOnlySpan<char>(message, length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        if (!_reportedFailure)
                        {
                            _reportedFailure = true;
                            string warning = $"[PluginLog] Cannot write log under {_gamePath}: {ex.Message}";
                            int warningLevel = 3;
                            fixed (char* text = warning) Forward(&warningLevel, eventId, text, warning.Length);
                        }
                    }
                }
                Forward(level, eventId, message, length);
            }
        }
        catch (Exception) { /* A logger callback must never terminate the native caller. */ }
    }

    private static string GetSessionFileName()
    {
        using var process = Process.GetCurrentProcess();
        string stamp = process.StartTime.ToUniversalTime().ToString(LogTimestampFormat, CultureInfo.InvariantCulture);
        return $"plugin-{stamp}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.log";
    }

    private static void WriteSessionLog(string gamePath, int level, ReadOnlySpan<char> message)
    {
        string directory = PluginPaths.In(gamePath, "logs");
        string file = InstallPath.Resolve(directory, SessionFileName);
        bool firstWrite = !SessionFiles.Contains(file);
        if (firstWrite) Directory.CreateDirectory(directory);

        try
        {
            using var stream = new FileStream(file, firstWrite ? FileMode.OpenOrCreate : FileMode.Open,
                FileAccess.Write, FileShare.Read | FileShare.Delete);
            stream.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(stream, LogEncoding);
            writer.Write($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{(Microsoft.Extensions.Logging.LogLevel)level}] ");
            writer.WriteLine(message);
        }
        catch (FileNotFoundException) when (!firstWrite) { return; }

        if (firstWrite)
        {
            PruneSessionLogs(directory);
            SessionFiles.Add(file);
        }
    }

    internal static void PruneSessionLogs(string directory)
    {
        var sessions = Directory.EnumerateFiles(directory, "plugin-*.log", new EnumerationOptions
            { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Where(path => IsSessionLog(Path.GetFileName(path)))
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(RetainedSessions)
            .ToArray();

        foreach (string file in sessions) File.Delete(file);
    }

    private static bool IsSessionLog(string name)
    {
        const string prefix = "plugin-";
        const string suffix = ".log";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> value = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        int separator = value.LastIndexOf('-');
        return separator > 0 &&
               DateTime.TryParseExact(value[..separator], LogTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
               int.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0;
    }

    private static void Forward(int* level, void* eventId, char* text, int length)
    {
        if (_host != 0) ((delegate* unmanaged[Cdecl]<int*, void*, char*, int, void>)_host)(level, eventId, text, length);
    }
}
