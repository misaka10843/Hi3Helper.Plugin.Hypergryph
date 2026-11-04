using System;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Arknights.Management.PresetConfig;
using Hi3Helper.Plugin.Arknights.Utils;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.Arknights;

[GeneratedComClass]
public partial class ArknightsPlugin : PluginBase, IPlugin
{
    public new void SetPluginLocaleId(string? localeId)
    {
        if (Hi3Helper.Hypergryph.Shared.PluginCulture.TrySet(localeId, out string coreLocale)) base.SetPluginLocaleId(coreLocale);
    }

    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new ArknightsCnPresetConfig(),
        new ArknightsBiliPresetConfig(),
        new ArknightsGlobalPresetConfig(),
        new ArknightsJpPresetConfig(),
        new ArknightsKrPresetConfig()
    ];

    private static DateTime _pluginCreationDate = new(2026, 02, 13, 00, 00, 0, DateTimeKind.Utc);

    public override void GetPluginName(out string result)
    {
        result = "Arknights Plugin";
    }

    public override void GetPluginDescription(out string result)
    {
        result = "A plugin for Arknights in Collapse Launcher";
    }

    public override void GetPluginAuthor(out string result)
    {
        result = "misaka10843";
    }

    public override unsafe void GetPluginCreationDate(out DateTime* result)
    {
        result = _pluginCreationDate.AsPointer();
    }

    public override void GetPresetConfigCount(out int count)
    {
        count = PresetConfigInstances.Length;
    }

    public override void GetPresetConfig(int index, out IPluginPresetConfig presetConfig)
    {
        SharedStatic.InstanceLogger.LogInformation("[Arknights] Starting execution...");
        if (index < 0 || index >= PresetConfigInstances.Length)
        {
            presetConfig = null!;
            return;
        }

        presetConfig = PresetConfigInstances[index];
    }

    public override void GetPluginSelfUpdater(out IPluginSelfUpdate selfUpdate)
    {
        selfUpdate = new SelfUpdate();
    }

    public override void GetPluginAppIconUrl(out string result)
    {
        result = Convert.ToBase64String(ArknightsImageData.ArknightsAppIconData);
    }

    public override void GetNotificationPosterUrl(out string result)
    {
        result = Convert.ToBase64String(ArknightsImageData.ArknightsPosterData);
    }
}
