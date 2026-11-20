using System;
using System.IO;
using System.Linq;

namespace Hi3Helper.Hypergryph.Shared;

internal static class InstallPath
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

    internal static void ReplaceVerifiedFile(string temporary, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(temporary, target);
            return;
        }

        FileAttributes attributes = File.GetAttributes(target);
        bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        if (readOnly) File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        try { File.Move(temporary, target, true); }
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
}
