using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Shared;

public static class PluginPaths
{
    private static readonly object MigrationGate = new();
    private static readonly HashSet<string> Migrated = new(StringComparer.OrdinalIgnoreCase);
    public static string Root(string gamePath) => Path.Combine(Path.GetFullPath(gamePath), "_Hi3_Hg");
    public static string In(string gamePath, string name)
    {
        InstallPath.RejectLink(Path.GetFullPath(gamePath));
        return InstallPath.Resolve(Root(gamePath), name);
    }

    public static void Migrate(string gamePath)
    {
        if (!Directory.Exists(gamePath)) return;
        lock (MigrationGate)
        {
            gamePath = Path.GetFullPath(gamePath);
            if (Migrated.Contains(gamePath)) return;
            if (Directory.Exists(Root(gamePath)) && (File.GetAttributes(Root(gamePath)) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(SettingsLocalization.Get(SettingsText.LinkedPluginDirectory));
            Directory.CreateDirectory(Root(gamePath));
            (string Old, string New)[] entries =
            [
                ("CollapsePluginLogs", "logs"), ("_Collapse_Logs", "logs"),
                ("Diffs", "downloads"), ("_Collapse_Diffs", "downloads"),
                ("_Hg_DeltaTemp", "delta-temp"), ("_Collapse_DeltaTemp", "delta-temp"),
                ("_Hg_ApplyStage", "apply-stage"), ("_Collapse_ApplyStage", "apply-stage"),
                ("_Hg_Tools", "tools"), ("_Collapse_Tools", "tools")
            ];
            foreach (var (oldName, newName) in entries)
            {
                string source = Path.Combine(gamePath, oldName);
                string destination = Path.Combine(Root(gamePath), newName);
                if (!Directory.Exists(source)) continue;
                if (Directory.Exists(destination) || File.Exists(destination) ||
                    (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                {
                    SharedStatic.InstanceLogger.LogWarning("[Migration] Kept legacy directory {Path}; destination exists or source is a link.", source);
                    continue;
                }
                Directory.Move(source, destination);
            }
            Migrated.Add(gamePath);
        }
    }
}
