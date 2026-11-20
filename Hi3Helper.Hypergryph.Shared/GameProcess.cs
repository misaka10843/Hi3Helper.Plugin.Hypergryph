using System.ComponentModel;
using System.Diagnostics;

namespace Hi3Helper.Hypergryph.Shared;

internal static class GameProcess
{
    internal static Process? FindExecutable(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)) return null;
        string expected = Path.GetFullPath(executablePath);
        Process? selected = null;
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            try
            {
                if (selected is null && process.MainModule?.FileName is string actual &&
                    string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                    selected = process;
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            finally
            {
                if (!ReferenceEquals(selected, process)) process.Dispose();
            }
        }
        return selected;
    }
}
