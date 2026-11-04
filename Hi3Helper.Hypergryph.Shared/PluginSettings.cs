using System.Runtime.CompilerServices;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;

namespace Hi3Helper.Hypergryph.Shared;

public static class PluginSettings
{
    private sealed record Draft(IGameManager Manager, PluginConfiguration Store, PluginOptions Options, string Snapshot, string GamePath, EndfieldDisplaySettings? Display);
    private static readonly ConditionalWeakTable<IPluginPresetConfig, Draft> Drafts = new();

    public static GameSettingsPage GetPage(IPluginPresetConfig preset, bool endfield)
    {
        preset.comGet_GameManager(out var manager);
        if (manager == null) throw new InvalidOperationException("游戏管理器不可用。");
        var store = PluginConfiguration.For(manager);
        var options = store.Read();
        manager.GetGamePath(out string? path);
        Drafts.Remove(preset);
        var display = endfield && OperatingSystem.IsWindows() ? EndfieldDisplaySettings.Read(preset) : null;
        Drafts.Add(preset, new(manager, store, options, store.Snapshot, path!, display));
        var entries = new List<GameSettingEntry>
        {
            GameSettingEntry.Toggle("manualVerificationRequested", "请求一次完整文件验证", false,
                "应用后请求立即复位。返回首页刷新状态，通过更新入口执行验证；关闭启动器会丢弃尚未执行的请求。")
        };
        if (endfield) entries.Add(GameSettingEntry.Text("parentExecutablePath", "父进程来源 EXE", options.ParentExecutablePath,
            "留空使用游戏根目录的 PlatformProcess.exe。支持相对路径或绝对路径，签名者必须与游戏一致；下次启动生效。"));
        var sections = new List<GameSettingsSection>();
        if (display != null && OperatingSystem.IsWindows()) sections.Add(display.Section);
        sections.Add(new GameSettingsSection("插件设置", entries));
        return new GameSettingsPage(sections) { Title = "游戏与插件设置" };
    }

    public static void SetValue(IPluginPresetConfig preset, string key, string value, bool endfield)
    {
        if (!Drafts.TryGetValue(preset, out var draft)) throw new InvalidOperationException("请重新打开设置页面。");
        if (key.StartsWith("display.", StringComparison.Ordinal) && draft.Display != null && OperatingSystem.IsWindows())
        {
            draft.Display.Set(key, value);
            return;
        }
        switch (key)
        {
            case "manualVerificationRequested":
                draft.Options.ManualVerificationRequested = bool.Parse(value);
                break;
            case "parentExecutablePath" when endfield:
                draft.Options.ParentExecutablePath = value.Trim();
                break;
            default: throw new ArgumentException("未知设置项：" + key);
        }
    }

    public static void Apply(IPluginPresetConfig preset, bool endfield)
    {
        if (!Drafts.TryGetValue(preset, out var draft)) throw new InvalidOperationException("请重新打开设置页面。");
        draft.Manager.GetGamePath(out string? path);
        if (!string.Equals(path, draft.GamePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("游戏目录已更改，请重新打开设置。");
        if (endfield && !string.IsNullOrWhiteSpace(draft.Options.ParentExecutablePath))
        {
            string source = Path.GetFullPath(draft.Options.ParentExecutablePath, draft.GamePath);
            if (!string.Equals(Path.GetExtension(source), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                throw new FileNotFoundException("父进程来源必须是存在的 EXE 文件。", source);
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
