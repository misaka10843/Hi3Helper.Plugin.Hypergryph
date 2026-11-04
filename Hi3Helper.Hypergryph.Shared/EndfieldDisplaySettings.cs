using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using Microsoft.Win32;

namespace Hi3Helper.Hypergryph.Shared;

/// <summary>Existing Unity display preferences observed in the mainland Endfield client.</summary>
[SupportedOSPlatform("windows")]
public sealed class EndfieldDisplaySettings
{
    private const string RegistryPath = @"Software\Hypergryph\Endfield";
    private static readonly Dictionary<string, string> Names = new()
    {
        ["display.width"] = "Screenmanager Resolution Width_h182942802",
        ["display.height"] = "Screenmanager Resolution Height_h2627697771",
        ["display.native"] = "Screenmanager Resolution Use Native_h1405027254",
        ["display.mode"] = "Screenmanager Fullscreen mode_h3630240806"
    };
    private readonly Dictionary<string, int> _original;
    private readonly Dictionary<string, int> _values;
    private readonly string _registryPath;
    private bool Dirty => _values.Any(pair => pair.Value != _original[pair.Key]);

    private EndfieldDisplaySettings(Dictionary<string, int> values, string registryPath)
    {
        _registryPath = registryPath;
        _original = values;
        _values = new(values);
    }

    public static EndfieldDisplaySettings? Read(IPluginPresetConfig preset)
    {
        preset.comGet_ProfileName(out string profile);
        if (profile != "EndfieldCn") return null;
        return ReadRegistry(RegistryPath);
    }

    internal static EndfieldDisplaySettings? ReadRegistry(string registryPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        if (key == null) return null;
        var values = new Dictionary<string, int>();
        foreach (var (name, registryName) in Names)
        {
            if (key.GetValue(registryName) is not int value || key.GetValueKind(registryName) != RegistryValueKind.DWord) return null;
            values.Add(name, value);
        }
        if (values["display.width"] < 1 || values["display.height"] < 1 ||
            values["display.native"] is not (0 or 1) || values["display.mode"] is not (0 or 1 or 3)) return null;
        return new(values, registryPath);
    }

    public GameSettingsSection Section => new("游戏设置 · 显示", [
        GameSettingEntry.Toggle("display.native", "使用显示器原生分辨率", _values["display.native"] == 1, "关闭游戏后应用，下次启动生效；游戏自身可能再次覆盖显示设置。"),
        GameSettingEntry.Number("display.width", "分辨率宽度", _values["display.width"], 1, int.MaxValue, 1, "使用原生分辨率时，此数值可能被游戏忽略。"),
        GameSettingEntry.Number("display.height", "分辨率高度", _values["display.height"], 1, int.MaxValue),
        GameSettingEntry.Choice("display.mode", "窗口模式", _values["display.mode"].ToString(CultureInfo.InvariantCulture), [
            new("0", "独占全屏"), new("1", "无边框全屏"), new("3", "窗口")])
    ]);

    public void Set(string name, string value)
    {
        if (!Names.ContainsKey(name)) throw new ArgumentException("未知显示设置。");
        int number = name == "display.native" ? (bool.Parse(value) ? 1 : 0) : int.Parse(value, CultureInfo.InvariantCulture);
        if (name is "display.width" or "display.height" && number < 1 || name == "display.mode" && number is not (0 or 1 or 3))
            throw new ArgumentOutOfRangeException(nameof(value));
        _values[name] = number;
    }

    public void Validate()
    {
        if (!Dirty) return;
        var processes = Process.GetProcessesByName("Endfield");
        try { if (processes.Length != 0) throw new InvalidOperationException("请关闭 Endfield 后再应用游戏显示设置。"); }
        finally { foreach (var process in processes) process.Dispose(); }
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath) ?? throw new IOException("游戏配置已消失，请重新打开设置。");
        foreach (var (name, registryName) in Names)
            if (key.GetValue(registryName) is not int current || current != _original[name])
                throw new IOException("游戏配置已被修改，请重新打开设置。");
    }

    public void Apply()
    {
        Validate();
        if (!Dirty) return;
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true) ?? throw new IOException("游戏配置不可写。");
        try
        {
            foreach (var (name, value) in _values)
                if (value != _original[name]) key.SetValue(Names[name], value, RegistryValueKind.DWord);
            key.Flush();
        }
        catch { Restore(); throw; }
    }

    public void Restore()
    {
        if (!Dirty) return;
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true) ?? throw new IOException("无法恢复游戏显示设置。");
        foreach (var (name, value) in _values)
            if (value != _original[name] && key.GetValue(Names[name]) is int current && current == value)
                key.SetValue(Names[name], _original[name], RegistryValueKind.DWord);
    }
}
