using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.Endfield;

public partial class Exports : SharedStaticV1Ext<Exports>
{
    static Exports()
    {
        Load<EndfieldPlugin>(!RuntimeFeature.IsDynamicCodeCompiled ? new GameVersion(1, 0, 0, 0) : default);
        Hi3Helper.Hypergryph.Shared.PluginDiagnostics.Initialize();
        Hi3Helper.Hypergryph.Shared.PluginSettings.RegisterPreferences("EndfieldCn", preset => System.OperatingSystem.IsWindows() ? Settings.EndfieldDisplaySettings.Read(preset) : null);
    }

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* exportName, void** delegateP)
    {
        if (Hi3Helper.Hypergryph.Shared.PluginApiBoundary.TryResolve(exportName, delegateP, true)) return 0;
        return TryGetApiExportPointer(exportName, delegateP);
    }
}
