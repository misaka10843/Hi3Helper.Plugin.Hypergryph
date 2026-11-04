using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core;

namespace Hi3Helper.Hypergryph.Shared;

/// <summary>A plugin-owned adapter for the standard Core logger callback.</summary>
public static unsafe class PluginDiagnostics
{
    private static readonly object Gate = new();
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
            if (!string.Equals(_gamePath, path, StringComparison.OrdinalIgnoreCase)) _reportedFailure = false;
            _gamePath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
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
                        string directory = PluginPaths.In(_gamePath, "logs");
                        Directory.CreateDirectory(directory);
                        string file = Path.Combine(directory, "plugin.log");
                        if (File.Exists(file) && new FileInfo(file).Length >= 5 * 1024 * 1024) File.Move(file, file + ".1", true);
                        using var writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));
                        writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{(Microsoft.Extensions.Logging.LogLevel)(*level)}] {new string(message, 0, length)}");
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

    private static void Forward(int* level, void* eventId, char* text, int length)
    {
        if (_host != 0) ((delegate* unmanaged[Cdecl]<int*, void*, char*, int, void>)_host)(level, eventId, text, length);
    }
}
