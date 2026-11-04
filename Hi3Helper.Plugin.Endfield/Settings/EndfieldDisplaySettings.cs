using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using Microsoft.Win32;
using Hi3Helper.Hypergryph.Shared;
using static Hi3Helper.Hypergryph.Shared.SettingsLocalization;

namespace Hi3Helper.Plugin.Endfield.Settings;

/// <summary>Existing display and graphics preferences in the mainland Endfield client.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class EndfieldDisplaySettings : IGamePreferences
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
    private readonly Dictionary<string, long> _original;
    private readonly Dictionary<string, long> _values;
    private readonly string _registryPath;
    private bool Dirty => _values.Any(pair => pair.Value != _original[pair.Key]);

    private EndfieldDisplaySettings(Dictionary<string, long> values, string registryPath)
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
        var values = new Dictionary<string, long>();
        foreach (var (name, registryName) in AllNames())
        {
            object? raw = key.GetValue(registryName);
            if (raw is not int && raw is not long) continue;
            long value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            if (key.GetValueKind(registryName) == RegistryValueKind.DWord && IsValid(name, value) &&
                (Preferences.Any(p => p.Name == name && p.Volume) ? raw is long : raw is int))
                values.Add(name, value);
        }
        return values.Count == 0 ? null : new(values, registryPath);
    }

    public IEnumerable<GameSettingsSection> Sections
    {
        get
        {
            var display = new List<GameSettingEntry>();
            if (_values.TryGetValue("display.width", out long width)) display.Add(GameSettingEntry.Number("display.width", Get(SettingsText.Width), width, 1, int.MaxValue));
            if (_values.TryGetValue("display.height", out long height)) display.Add(GameSettingEntry.Number("display.height", Get(SettingsText.Height), height, 1, int.MaxValue));
            if (_values.TryGetValue("display.mode", out long mode)) display.Add(GameSettingEntry.Choice("display.mode", Get(SettingsText.WindowMode), mode.ToString(CultureInfo.InvariantCulture), [
                new("0", Get(SettingsText.ExclusiveFullscreen)), new("1", Get(SettingsText.Borderless)), new("3", Get(SettingsText.Windowed))]));
            if (_values.TryGetValue("display.native", out long native)) display.Add(GameSettingEntry.Toggle("display.native", Get(SettingsText.NativeResolution), native == 1));
            if (display.Count != 0) yield return new(Get(SettingsText.DisplaySection), display) { Description = Get(SettingsText.DisplayHelp) };

            var graphics = new List<GameSettingEntry>();
            // Stored enum values are version-specific. Only existing, recognized DWORD values are editable.
            if (_values.TryGetValue("graphics.quality", out long quality)) graphics.Add(GameSettingEntry.Choice("graphics.quality", Get(SettingsText.QualityPreset), quality.ToString(CultureInfo.InvariantCulture), [
                new("1", Get(SettingsText.Ultra)), new("2", Get(SettingsText.High)), new("3", Get(SettingsText.Medium)), new("4", Get(SettingsText.Low)), new("5", Get(SettingsText.VeryLow))]));
            if (_values.TryGetValue("graphics.fps", out long fps)) graphics.Add(GameSettingEntry.Choice("graphics.fps", Get(SettingsText.FrameRate), fps.ToString(CultureInfo.InvariantCulture), [
                new("1000", "120 FPS"), new("2000", "60 FPS"), new("3000", "30 FPS")]));
            if (graphics.Count != 0) yield return new(Get(SettingsText.GraphicsSection), graphics) { Description = Get(SettingsText.GraphicsHelp) };
            foreach (var section in AdditionalSections()) yield return section;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> AllNames() => Names.Concat(Preferences.Select(p => new KeyValuePair<string,string>(p.Name, p.RegistryName)));
    private static string RegistryName(string name) => Names.TryGetValue(name, out string? registryName) ? registryName : Preferences.Single(p => p.Name == name).RegistryName;

    private static bool IsValid(string name, long value) => name switch
    {
        "display.width" or "display.height" => value > 0,
        "display.native" => value is 0 or 1,
        "display.mode" => value is 0 or 1 or 3,
        "graphics.quality" => value is >= 1 and <= 5,
        "graphics.fps" => value is 1000 or 2000 or 3000,
        _ => IsAdditionalValid(name, value)
    };

    private static bool IsAdditionalValid(string name, long value)
    {
        var spec = Preferences.FirstOrDefault(p => p.Name == name);
        if (spec == null) return false;
        if (spec.Volume) { double volume = BitConverter.Int64BitsToDouble(value); return double.IsFinite(volume) && volume is >= 0 and <= 1; }
        if (spec.Toggle) return value is 0 or 1;
        return spec.Maximum > 0 ? value >= 0 && value <= spec.Maximum : spec.Options.Any(o => o.Value == value);
    }

    public void Set(string name, string value)
    {
        if (!_values.ContainsKey(name)) throw new ArgumentException(Format(SettingsText.UnknownSetting, name));
        long number;
        var spec = Preferences.FirstOrDefault(p => p.Name == name);
        bool toggle = name == "display.native" || spec?.Toggle == true;
        if (toggle && bool.TryParse(value, out bool native)) number = native ? 1 : 0;
        else if (spec?.Maximum > 0 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double normalized) && double.IsFinite(normalized) && normalized is >= 0 and <= 1)
            number = (long)Math.Round(normalized * 1000);
        else if (!toggle && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
        {
            if (spec?.Volume == true && integer is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(value), Format(SettingsText.InvalidValue, name));
            number = spec?.Volume == true ? BitConverter.DoubleToInt64Bits((double)(float)(integer / 10.0)) : integer;
        }
        else throw new ArgumentException(Format(SettingsText.InvalidValue, name));
        if (!IsValid(name, number)) throw new ArgumentOutOfRangeException(nameof(value), Format(SettingsText.InvalidValue, name));
        if (_values[name] == number) return;
        _values[name] = number;
        if (_values.ContainsKey("graphics.custom"))
        {
            if (name == "graphics.quality") _values["graphics.custom"] = 0;
            else if (spec?.Group == "graphics" && name != "graphics.custom") _values["graphics.custom"] = 1;
        }
    }

    public void Validate()
    {
        if (!Dirty) return;
        var processes = Process.GetProcessesByName("Endfield");
        try { if (processes.Length != 0) throw new InvalidOperationException(Get(SettingsText.GameRunning)); }
        finally { foreach (var process in processes) process.Dispose(); }
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath) ?? throw new IOException(Get(SettingsText.GameConfigMissing));
        foreach (var (name, original) in _original)
            if (key.GetValue(RegistryName(name)) is not object current || key.GetValueKind(RegistryName(name)) != RegistryValueKind.DWord || !Equals(current, RawValue(name, original)))
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
                if (value != _original[name]) WriteValue(key, name, value);
            key.Flush();
        }
        catch { Restore(); throw; }
    }

    public void Restore()
    {
        if (!Dirty) return;
        using var key = Registry.CurrentUser.OpenSubKey(_registryPath, true) ?? throw new IOException(Get(SettingsText.GameConfigRestore));
        foreach (var (name, value) in _values)
            if (value != _original[name] && Equals(key.GetValue(RegistryName(name)), RawValue(name, value)))
                WriteValue(key, name, _original[name]);
    }

    private static object RawValue(string name, long value) => Preferences.Any(p => p.Name == name && p.Volume) ? (object)value : (int)value;

    private static void WriteValue(RegistryKey key, string name, long value)
    {
        if (!Preferences.Any(p => p.Name == name && p.Volume)) { key.SetValue(RegistryName(name), (int)value, RegistryValueKind.DWord); return; }
        byte[] bytes = BitConverter.GetBytes(value);
        int error = RegSetValueEx(key.Handle, RegistryName(name), 0, 4, bytes, bytes.Length);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int RegSetValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);
}
