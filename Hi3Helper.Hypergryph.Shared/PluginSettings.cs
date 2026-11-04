using System.Runtime.CompilerServices;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using static Hi3Helper.Hypergryph.Shared.SettingsLocalization;

namespace Hi3Helper.Hypergryph.Shared;

public static class PluginSettings
{
    private sealed record Draft(IGameManager Manager, PluginConfiguration Store, PluginOptions Options, string Snapshot, string GamePath, EndfieldDisplaySettings? Display);
    private static readonly ConditionalWeakTable<IPluginPresetConfig, Draft> Drafts = new();

    public static GameSettingsPage GetPage(IPluginPresetConfig preset, bool endfield)
    {
        preset.comGet_GameManager(out var manager);
        if (manager == null) throw new InvalidOperationException(Get(SettingsText.ManagerUnavailable));
        var store = PluginConfiguration.For(manager);
        var options = store.Read();
        manager.GetGamePath(out string? path);
        Drafts.Remove(preset);
        var display = endfield && OperatingSystem.IsWindows() ? EndfieldDisplaySettings.Read(preset) : null;
        Drafts.Add(preset, new(manager, store, options, store.Snapshot, path!, display));
        var entries = new List<GameSettingEntry>
        {
            GameSettingEntry.Toggle("manualVerificationRequested", Get(SettingsText.Verification), false)
        };
        if (endfield) entries.Add(GameSettingEntry.Text("parentExecutablePath", Get(SettingsText.ParentExecutable), options.ParentExecutablePath,
            placeholder: "PlatformProcess.exe"));
        var sections = new List<GameSettingsSection>();
        if (display != null && OperatingSystem.IsWindows()) sections.AddRange(display.Sections);
        sections.Add(new GameSettingsSection(Get(SettingsText.PluginSection), entries)
        {
            Description = Get(endfield ? SettingsText.EndfieldPluginHelp : SettingsText.PluginHelp)
        });
        return new GameSettingsPage(sections) { Title = Get(SettingsText.PageTitle) };
    }

    public static void SetValue(IPluginPresetConfig preset, string key, string value, bool endfield)
    {
        if (!Drafts.TryGetValue(preset, out var draft)) throw new InvalidOperationException(Get(SettingsText.ReopenSettings));
        if ((key.StartsWith("display.", StringComparison.Ordinal) || key.StartsWith("graphics.", StringComparison.Ordinal)) && draft.Display != null && OperatingSystem.IsWindows())
        {
            draft.Display.Set(key, value);
            return;
        }
        switch (key)
        {
            case "manualVerificationRequested":
                if (!bool.TryParse(value, out bool request)) throw new ArgumentException(Format(SettingsText.InvalidValue, key));
                draft.Options.ManualVerificationRequested = request;
                break;
            case "parentExecutablePath" when endfield:
                draft.Options.ParentExecutablePath = value.Trim();
                break;
            default: throw new ArgumentException(Format(SettingsText.UnknownSetting, key));
        }
    }

    public static void Apply(IPluginPresetConfig preset, bool endfield)
    {
        if (!Drafts.TryGetValue(preset, out var draft)) throw new InvalidOperationException(Get(SettingsText.ReopenSettings));
        draft.Manager.GetGamePath(out string? path);
        if (!string.Equals(path, draft.GamePath, StringComparison.OrdinalIgnoreCase)) throw new IOException(Get(SettingsText.GamePathChanged));
        if (endfield && !string.IsNullOrWhiteSpace(draft.Options.ParentExecutablePath))
        {
            string source = Path.GetFullPath(draft.Options.ParentExecutablePath, draft.GamePath);
            if (!string.Equals(Path.GetExtension(source), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                throw new FileNotFoundException(Get(SettingsText.ParentFileMissing), source);
        }
        if (OperatingSystem.IsWindows()) draft.Display?.Apply();
        try { draft.Store.Apply(draft.Options, draft.Snapshot); }
        catch { if (OperatingSystem.IsWindows()) draft.Display?.Restore(); throw; }
        // The host keeps its existing controls after Apply; clear the one-shot value in the draft too.
        Drafts.Remove(preset);
        Drafts.Add(preset, draft with
        {
            Snapshot = draft.Store.Snapshot,
            Display = endfield && OperatingSystem.IsWindows() ? EndfieldDisplaySettings.Read(preset) : null
        });
    }
}
