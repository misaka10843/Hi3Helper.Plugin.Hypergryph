using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

#pragma warning disable CA1416

namespace Hi3Helper.Hypergryph.Core.Management;

/// <summary>
///     Builds the parent process out of an installed official launcher.
///     <para>
///         Not used by the launch path right now: the game ships files that already satisfy the
///         parent check, so <see cref="GameDecoyParentBridge" /> is cheaper and needs no download.
///         This stays here for the case where the client tightens the check to something only the
///         real launcher satisfies.
///     </para>
/// </summary>
public sealed class OfficialLauncherBridge : ParentProcessBridge
{
    private static readonly string[] ProtocolPrefixes =
        ["hypergryphlauncher", "gryphlinelauncher"];

    private OfficialLauncherBridge(nint process, nint thread) : base(process, thread)
    {
    }

    public static bool TryCreate(string? hintDirectory, string? hintExecutable,
        out OfficialLauncherBridge? bridge, out string? launcherExecutablePath)
    {
        bridge                 = null;
        launcherExecutablePath = null;

        foreach (string candidate in EnumerateCandidates(hintDirectory, hintExecutable))
        {
            if (!IsTrustedFile(candidate))
            {
                continue;
            }

            if (!TrySuspend(candidate, out nint process, out nint thread))
            {
                continue;
            }

            bridge                 = new OfficialLauncherBridge(process, thread);
            launcherExecutablePath = candidate;

            SharedStatic.InstanceLogger.LogInformation($"[HgBridge] Official launcher resolved: {candidate}");
            return true;
        }

        SharedStatic.InstanceLogger.LogWarning("[HgBridge] No trusted official launcher executable found.");
        return false;
    }

    private static IEnumerable<string> EnumerateCandidates(string? hintDirectory, string? hintExecutable)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(hintExecutable) && seen.Add(hintExecutable))
        {
            yield return hintExecutable;
        }

        foreach (string directory in EnumerateLauncherDirectories(hintDirectory))
        {
            foreach (string executable in EnumerateLauncherExecutables(directory))
            {
                if (seen.Add(executable))
                {
                    yield return executable;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLauncherDirectories(string? hintDirectory)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(hintDirectory) && Directory.Exists(hintDirectory))
        {
            if (seen.Add(hintDirectory))
            {
                yield return hintDirectory;
            }
        }

        foreach (string directory in EnumerateProtocolDirectories())
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static List<string> EnumerateProtocolDirectories()
    {
        List<string> directories = [];

        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? classes = root.OpenSubKey(@"Software\Classes");
            if (classes == null)
            {
                continue;
            }

            foreach (string subKeyName in classes.GetSubKeyNames())
            {
                if (!ProtocolPrefixes.Any(prefix =>
                        subKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                using RegistryKey? command = classes.OpenSubKey($@"{subKeyName}\shell\open\command");
                if (command?.GetValue(null) is not string commandLine)
                {
                    continue;
                }

                string? executable = ExtractExecutablePath(commandLine);
                string? directory  = executable == null ? null : Path.GetDirectoryName(executable);

                if (!string.IsNullOrEmpty(directory))
                {
                    directories.Add(directory);
                }
            }
        }

        return directories;
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        string trimmed = commandLine.Trim();

        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        int executableIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableIndex >= 0 ? trimmed[..(executableIndex + 4)] : null;
    }

    private static IEnumerable<string> EnumerateLauncherExecutables(string directory)
    {
        string direct = Path.Combine(directory, GamesExecutableName);
        if (File.Exists(direct))
        {
            yield return direct;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                $"[HgBridge] Cannot enumerate {directory}: {ex.Message}");
            yield break;
        }

        foreach (string child in children)
        {
            string nested = Path.Combine(child, GamesExecutableName);
            if (File.Exists(nested))
            {
                yield return nested;
            }
        }
    }
}
