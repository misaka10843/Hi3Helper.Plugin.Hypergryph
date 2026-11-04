using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.Arknights;

public partial class Exports : SharedStaticV1Ext<Exports>
{
    static Exports()
    {
        Load<ArknightsPlugin>(!RuntimeFeature.IsDynamicCodeCompiled ? new GameVersion(1, 0, 0, 0) : default);
        Hi3Helper.Hypergryph.Shared.PluginDiagnostics.Initialize();
    }

    [UnmanagedCallersOnly(EntryPoint = "TryGetApiExport", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int TryGetApiExport(char* exportName, void** delegateP)
    {
        if (Hi3Helper.Hypergryph.Shared.PluginApiBoundary.TryResolve(exportName, delegateP, false)) return 0;
        return TryGetApiExportPointer(exportName, delegateP);
    }
}
