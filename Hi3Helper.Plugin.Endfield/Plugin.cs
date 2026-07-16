using System;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Endfield.Management.PresetConfig;
using Hi3Helper.Plugin.Endfield.Utils;
using Hi3Helper.Hypergryph.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.Endfield;

[GeneratedComClass]
public partial class EndfieldPlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances =
    [
        new EndfieldCnPresetConfig(),
        new EndfieldGlobalPresetConfig(),
        new EndfieldBiliPresetConfig(),
        new EndfieldPlayPresetConfig()
    ];

    private static DateTime _pluginCreationDate = new(2026, 01, 27, 00, 00, 0, DateTimeKind.Utc);

    public override void GetPluginName(out string result)
    {
        result = "Arknights: Endfield Plugin";
    }

    public override void GetPluginDescription(out string result)
    {
        result = "A plugin for Arknights: Endfield in Collapse Launcher";
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
        SharedStatic.InstanceLogger.LogInformation("[Endfield] Starting execution...");
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
        result = Convert.ToBase64String(EndfieldImageData.EndfieldAppIconData);
    }

    public override void GetNotificationPosterUrl(out string result)
    {
        result = Convert.ToBase64String(EndfieldImageData.EndfieldPosterData);
    }
}