using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Hypergryph.Shared;

/// <summary>Plugin-owned implementations of the published settings and logger ABI.</summary>
public static unsafe partial class PluginApiBoundary
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PageMemory { public nint Handle; public int Length; public int IsDisposable; }

    public static bool TryResolve(char* name, void** result, bool endfield)
    {
        if (name == null || result == null) return false;
        ReadOnlySpan<char> export = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        nint pointer;
        if (export.Equals("SetLoggerCallback", StringComparison.OrdinalIgnoreCase)) pointer = PluginDiagnostics.Setter;
        else if (export.Equals("GetGameSettingsPage", StringComparison.OrdinalIgnoreCase)) pointer = endfield
            ? (nint)(delegate* unmanaged[Cdecl]<nint, PageMemory*, int>)&GetEndfieldPage : (nint)(delegate* unmanaged[Cdecl]<nint, PageMemory*, int>)&GetArknightsPage;
        else if (export.Equals("SetGameSettingValue", StringComparison.OrdinalIgnoreCase)) pointer = endfield
            ? (nint)(delegate* unmanaged[Cdecl]<nint, char*, int, char*, int, int>)&SetEndfield : (nint)(delegate* unmanaged[Cdecl]<nint, char*, int, char*, int, int>)&SetArknights;
        else if (export.Equals("ApplyGameSettings", StringComparison.OrdinalIgnoreCase)) pointer = endfield
            ? (nint)(delegate* unmanaged[Cdecl]<nint, int>)&ApplyEndfield : (nint)(delegate* unmanaged[Cdecl]<nint, int>)&ApplyArknights;
        else return false;
        *result = (void*)pointer;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetEndfieldPage(nint preset, PageMemory* result) => GetPage(preset, result, true);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetArknightsPage(nint preset, PageMemory* result) => GetPage(preset, result, false);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetEndfield(nint preset, char* key, int keyLength, char* value, int valueLength) => Set(preset, key, keyLength, value, valueLength, true);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetArknights(nint preset, char* key, int keyLength, char* value, int valueLength) => Set(preset, key, keyLength, value, valueLength, false);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ApplyEndfield(nint preset) => Apply(preset, true);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ApplyArknights(nint preset) => Apply(preset, false);

    private static IPluginPresetConfig Preset(nint pointer) => pointer == 0 ? throw new ArgumentNullException(nameof(pointer)) :
        ComInterfaceMarshaller<IPluginPresetConfig>.ConvertToManaged((void*)pointer) ?? throw new InvalidCastException("Invalid preset interface.");

    private static int GetPage(nint preset, PageMemory* result, bool endfield)
    {
        if (result == null) return Error(new ArgumentNullException(nameof(result)));
        *result = default;
        try
        {
            PluginDisposableMemoryMarshal memory = GameSettingsPageSerializer.Serialize(PluginSettings.GetPage(Preset(preset), endfield));
            *result = new() { Handle = memory.Handle, Length = memory.Length, IsDisposable = memory.IsDisposable ? 1 : 0 };
            return 0;
        }
        catch (Exception ex) { return Error(ex); }
    }

    private static int Set(nint preset, char* key, int keyLength, char* value, int valueLength, bool endfield)
    {
        try
        {
            PluginSettings.SetValue(Preset(preset), new ReadOnlySpan<char>(key, keyLength).ToString(), new ReadOnlySpan<char>(value, valueLength).ToString(), endfield);
            return 0;
        }
        catch (Exception ex) { return Error(ex); }
    }

    private static int Apply(nint preset, bool endfield)
    {
        try { PluginSettings.Apply(Preset(preset), endfield); return 0; }
        catch (Exception ex) { return Error(ex); }
    }

    private static int Error(Exception exception)
    {
        SharedStatic.InstanceLogger.LogError(exception, "[Settings] Request failed");
        int result = Marshal.GetHRForException(exception);
        if (!OperatingSystem.IsWindows() || CreateErrorInfo(out nint creator) != 0 || creator == 0) return result;
        nint error = 0;
        try
        {
            var table = *(nint**)creator;
            fixed (char* description = exception.Message)
                if (((delegate* unmanaged[Stdcall]<nint, char*, int>)table[5])(creator, description) != 0) return result;
            Guid errorInfoId = new("1CF2B120-547D-101B-8E65-08002B2BD119");
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)table[0])(creator, &errorInfoId, &error) == 0) SetErrorInfo(0, error);
        }
        finally
        {
            if (error != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)error)[2])(error);
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)creator)[2])(creator);
        }
        return result;
    }

    [LibraryImport("oleaut32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateErrorInfo(out nint errorInfo);
    [LibraryImport("oleaut32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetErrorInfo(uint reserved, nint errorInfo);
}
