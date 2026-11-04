using Hi3Helper.Hypergryph.Shared;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;

namespace Hi3Helper.Plugin.Arknights;

public partial class Exports
{
    protected override GameSettingsPage? GetGameSettingsPageCore(IPluginPresetConfig presetConfig) => PluginSettings.GetPage(presetConfig, false);
    protected override void SetGameSettingValueCore(IPluginPresetConfig presetConfig, string key, string value) => PluginSettings.SetValue(presetConfig, key, value, false);
    protected override void ApplyGameSettingsCore(IPluginPresetConfig presetConfig) => PluginSettings.Apply(presetConfig, false);
}
