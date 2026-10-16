using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Core.Management;

/// <summary>
///     Builds the parent process out of a file that already ships with the game.
///     <para>
///         The client only looks at the name, the signature and the certificate fingerprint of its
///         direct parent, so any Hypergryph-signed executable renamed to <c>Games.exe</c> is enough.
///         Several files inside the game directory already qualify, which means the parent process
///         can be staged from the installation the player already has, without downloading or
///         installing anything.
///     </para>
/// </summary>
public sealed class GameDecoyParentBridge : ParentProcessBridge
{
    /// <summary>
    ///     Tried in order. The first one that both verifies and can be created suspended wins.
    /// </summary>
    private static readonly string[] CandidateFileNames =
        ["PlatformProcess.exe", "UnityCrashHandler64.exe", "Endfield.exe"];

    private static readonly string DecoyDirectory =
        Path.Combine(Path.GetTempPath(), "Hi3Helper", "ParentBridge");

    private static readonly string DecoyPath =
        Path.Combine(DecoyDirectory, GamesExecutableName);

    /// <summary>How many directory levels below the game directory are searched for a candidate.</summary>
    private const int SearchDepth = 3;

    private GameDecoyParentBridge(nint process, nint thread) : base(process, thread)
    {
    }

    public static GameDecoyParentBridge? TryCreate(string? gameDirectory)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            SharedStatic.InstanceLogger.LogWarning($"[HgDecoy] Unusable game directory: {gameDirectory}");
            return null;
        }

        RemoveStaleDecoy();

        foreach (string source in EnumerateSources(gameDirectory))
        {
            if (!IsTrustedFile(source))
            {
                continue;
            }

            SharedStatic.InstanceLogger.LogInformation($"[HgDecoy] Trying {source} as the parent process.");

            if (!TryStage(source))
            {
                continue;
            }

            if (!TrySuspend(DecoyPath, out nint process, out nint thread))
            {
                RemoveStaleDecoy();
                continue;
            }

            SharedStatic.InstanceLogger.LogInformation(
                $"[HgDecoy] Parent process staged from {source} into {DecoyPath}.");

            return new GameDecoyParentBridge(process, thread);
        }

        RemoveStaleDecoy();
        SharedStatic.InstanceLogger.LogWarning(
            $"[HgDecoy] No game file could stand in as the parent process in {gameDirectory}.");
        return null;
    }

    protected override void OnDispose() => RemoveStaleDecoy();

    private static IEnumerable<string> EnumerateSources(string gameDirectory)
    {
        foreach (string fileName in CandidateFileNames)
        {
            string? found = FindFile(gameDirectory, fileName);

            if (found != null)
            {
                yield return found;
            }
        }
    }

    private static string? FindFile(string root, string fileName)
    {
        string direct = Path.Combine(root, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        List<string> current = [root];

        for (int depth = 0; depth < SearchDepth && current.Count > 0; depth++)
        {
            List<string> next = [];

            foreach (string directory in current)
            {
                string[] children;

                try
                {
                    children = Directory.GetDirectories(directory);
                }
                catch (Exception ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        $"[HgDecoy] Cannot enumerate {directory}: {ex.Message}");
                    continue;
                }

                foreach (string child in children)
                {
                    string candidate = Path.Combine(child, fileName);

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    if (!IsReparsePoint(child))
                    {
                        next.Add(child);
                    }
                }
            }

            current = next;
        }

        return null;
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryStage(string source)
    {
        try
        {
            Directory.CreateDirectory(DecoyDirectory);

            File.Copy(source, DecoyPath, true);

            // File.Copy carries the read-only flag over, and that would block the
            // cleanup of the next launch.
            File.SetAttributes(DecoyPath, FileAttributes.Normal);
            return true;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning($"[HgDecoy] Cannot stage {source}: {ex.Message}");
            return false;
        }
    }

    private static void RemoveStaleDecoy()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(DecoyPath))
                {
                    return;
                }

                File.Delete(DecoyPath);
                return;
            }
            catch (Exception ex) when (attempt < 2)
            {
                // A freshly terminated image may still be releasing its file mapping.
                SharedStatic.InstanceLogger.LogWarning($"[HgDecoy] Retrying the cleanup of {DecoyPath}: {ex.Message}");
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning($"[HgDecoy] Cannot remove {DecoyPath}: {ex.Message}");
                return;
            }
        }
    }
}
