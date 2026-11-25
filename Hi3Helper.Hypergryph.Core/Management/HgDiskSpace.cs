using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Hi3Helper.Hypergryph.Core.Management;

internal static partial class HgDiskSpace
{
    internal static void EnsureAvailable(string directory, long requiredBytes)
    {
        if (requiredBytes > 0 && TryGetAvailable(directory, out ulong available) && available < (ulong)requiredBytes)
            throw new SpaceException(
                $"Insufficient disk space in '{directory}'. Required: {requiredBytes:N0} bytes. Available: {available:N0} bytes.");
    }

    internal static bool IsDiskFull(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
        {
            if (cause.HResult is unchecked((int)0x80070027) or unchecked((int)0x80070070) ||
                cause is Win32Exception { NativeErrorCode: 39 or 112 })
                return true;
            if (cause is AggregateException aggregate && aggregate.InnerExceptions.Count > 1)
                foreach (Exception inner in aggregate.InnerExceptions)
                    if (IsDiskFull(inner)) return true;
        }
        return false;
    }

    internal static SpaceException WriteFailure(string directory, Exception cause, long? totalBytes = null)
    {
        string available = TryGetAvailable(directory, out ulong bytes) ? $"{bytes:N0} bytes" : "unknown";
        string size = totalBytes is long total ? $" Total output size: {total:N0} bytes." : "";
        return new SpaceException($"Disk space ran out while writing to '{directory}'. Available: {available}.{size}", cause);
    }

    internal sealed class SpaceException(string message, Exception? cause = null) : IOException(message, cause);

    private static bool TryGetAvailable(string directory, out ulong available)
    {
        available = 0;
        return OperatingSystem.IsWindows() && !string.IsNullOrEmpty(directory) &&
               GetDiskFreeSpaceEx(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                   out available, out _, out _) != 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
