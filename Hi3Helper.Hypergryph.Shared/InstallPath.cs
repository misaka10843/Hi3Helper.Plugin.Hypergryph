using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Hi3Helper.Hypergryph.Shared;

internal static partial class InstallPath
{
    internal static string Resolve(string gameRoot, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string[] segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." || segment != segment.TrimEnd(' ', '.') ||
                segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                (OperatingSystem.IsWindows() && IsDeviceName(segment))) ||
            segments[0].Equals("_Hi3_Hg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid installation path: {relativePath}");

        string path = Path.GetFullPath(gameRoot);
        RejectLink(path);
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
            RejectLink(path);
        }
        return path;
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "AUX" or "PRN" or "NUL" or "CONIN$" or "CONOUT$" ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }

    internal static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Installation path contains a link: {path}");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static void ReplaceVerifiedFile(string temporary, string target, bool sameVolumeOnly = false)
    {
        if (!File.Exists(target))
        {
            MoveVerifiedFile(temporary, target, overwrite: false, sameVolumeOnly);
            return;
        }

        FileAttributes attributes = File.GetAttributes(target);
        bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (readOnly) File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        try { MoveVerifiedFile(temporary, target, overwrite: true, sameVolumeOnly); }
        catch (Exception replacementError)
        {
            try { if (readOnly) File.SetAttributes(target, attributes); }
            catch (Exception restoreError)
            {
                throw new AggregateException(replacementError.Message, replacementError, restoreError);
            }
            throw;
        }
    }

    private static void MoveVerifiedFile(string source, string target, bool overwrite, bool sameVolumeOnly)
    {
        if (!sameVolumeOnly)
        {
            File.Move(source, target, overwrite);
            return;
        }

        const uint replaceExisting = 1;
        if (MoveFileEx(ExtendedWindowsPath(source), ExtendedWindowsPath(target), overwrite ? replaceExisting : 0) == 0)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException($"Could not move staged file to {target}: {error.Message}", error);
        }
    }

    private static string ExtendedWindowsPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MoveFileEx(string source, string target, uint flags);
}
