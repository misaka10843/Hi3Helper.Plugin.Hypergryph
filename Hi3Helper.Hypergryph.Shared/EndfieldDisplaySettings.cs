using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using Microsoft.Win32;
using static Hi3Helper.Hypergryph.Shared.SettingsLocalization;

namespace Hi3Helper.Hypergryph.Shared;

/// <summary>Existing display and graphics preferences in the mainland Endfield client.</summary>
[SupportedOSPlatform("windows")]
public sealed class EndfieldDisplaySettings
{
    private const string RegistryPath = @"Software\Hypergryph\Endfield";
    private static readonly Dictionary<string, string> Names = new()
    {
        ["display.width"] = "Screenmanager Resolution Width_h182942802",
        ["display.height"] = "Screenmanager Resolution Height_h2627697771",
        ["display.native"] = "Screenmanager Resolution Use Native_h1405027254",
        ["display.mode"] = "Screenmanager Fullscreen mode_h3630240806",
        ["graphics.quality"] = "video_quality_main_h2648490162",
        ["graphics.fps"] = "video_frame_rate_8_h697513772"
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
            if (key.GetValue(registryName) is int value && key.GetValueKind(registryName) == RegistryValueKind.DWord && IsValid(name, value))
                values.Add(name, value);
        }
        return values.Count == 0 ? null : new(values, registryPath);
    }

    public IEnumerable<GameSettingsSection> Sections
    {
        get
        {
            var display = new List<GameSettingEntry>();
            if (_values.TryGetValue("display.width", out int width)) display.Add(GameSettingEntry.Number("display.width", Get(SettingsText.Width), width, 1, int.MaxValue));
            if (_values.TryGetValue("display.height", out int height)) display.Add(GameSettingEntry.Number("display.height", Get(SettingsText.Height), height, 1, int.MaxValue));
            if (_values.TryGetValue("display.mode", out int mode)) display.Add(GameSettingEntry.Choice("display.mode", Get(SettingsText.WindowMode), mode.ToString(CultureInfo.InvariantCulture), [
                new("0", Get(SettingsText.ExclusiveFullscreen)), new("1", Get(SettingsText.Borderless)), new("3", Get(SettingsText.Windowed))]));
            if (_values.TryGetValue("display.native", out int native)) display.Add(GameSettingEntry.Toggle("display.native", Get(SettingsText.NativeResolution), native == 1));
            if (display.Count != 0) yield return new(Get(SettingsText.DisplaySection), display) { Description = Get(SettingsText.DisplayHelp) };

            var graphics = new List<GameSettingEntry>();
            // Stored enum values are version-specific. Only existing, recognized DWORD values are editable.
            if (_values.TryGetValue("graphics.quality", out int quality)) graphics.Add(GameSettingEntry.Choice("graphics.quality", Get(SettingsText.QualityPreset), quality.ToString(CultureInfo.InvariantCulture), [
                new("1", Get(SettingsText.Ultra)), new("2", Get(SettingsText.High)), new("3", Get(SettingsText.Medium)), new("4", Get(SettingsText.Low)), new("5", Get(SettingsText.VeryLow))]));
            if (_values.TryGetValue("graphics.fps", out int fps)) graphics.Add(GameSettingEntry.Choice("graphics.fps", Get(SettingsText.FrameRate), fps.ToString(CultureInfo.InvariantCulture), [
                new("1000", "120 FPS"), new("2000", "60 FPS"), new("3000", "30 FPS")]));
            if (graphics.Count != 0) yield return new(Get(SettingsText.GraphicsSection), graphics) { Description = Get(SettingsText.GraphicsHelp) };
        }
    }

    private static bool IsValid(string name, int value) => name switch
    {
        "display.width" or "display.height" => value > 0,
        "display.native" => value is 0 or 1,
        "display.mode" => value is 0 or 1 or 3,
        "graphics.quality" => value is >= 1 and <= 5,
        "graphics.fps" => value is 1000 or 2000 or 3000,
        _ => false
    };

    public void Set(string name, string value)
    {
        if (!_values.ContainsKey(name)) throw new ArgumentException(Format(SettingsText.UnknownSetting, name));
        int number;
        if (name == "display.native" && bool.TryParse(value, out bool native)) number = native ? 1 : 0;
        else if (name != "display.native" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer)) number = integer;
        else throw new ArgumentException(Format(SettingsText.InvalidValue, name));
        if (!IsValid(name, number)) throw new ArgumentOutOfRangeException(nameof(value), Format(SettingsText.InvalidValue, name));
        _values[name] = number;
    }

    public void Validate()
    {
        if (!Dirty) return;
        var processes = Process.GetProcessesByName("Endfield");
        try { if (processes.Length != 0) throw new InvalidOperationException(Get(SettingsText.GameRunning)); }
        finally { foreach (var process in processes) process.Dispose(); }
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath) ?? throw new IOException(Get(SettingsText.GameConfigMissing));
        foreach (var (name, original) in _original)
            if (key.GetValue(Names[name]) is not int current || key.GetValueKind(Names[name]) != RegistryValueKind.DWord || current != original)
                throw new IOException(Get(SettingsText.GameConfigChanged));
    }

    public void Apply()
    {
        Validate();
        if (!Dirty) return;
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true) ?? throw new IOException(Get(SettingsText.GameConfigReadOnly));
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
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true) ?? throw new IOException(Get(SettingsText.GameConfigRestore));
        foreach (var (name, value) in _values)
            if (value != _original[name] && key.GetValue(Names[name]) is int current && current == value)
                key.SetValue(Names[name], _original[name], RegistryValueKind.DWord);
    }
}
