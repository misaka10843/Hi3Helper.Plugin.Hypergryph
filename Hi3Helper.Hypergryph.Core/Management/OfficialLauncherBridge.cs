using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

#pragma warning disable CA1416
#pragma warning disable SYSLIB0057

namespace Hi3Helper.Hypergryph.Core.Management;

public sealed partial class OfficialLauncherBridge : IDisposable
{
    private const string LauncherExecutableName = "Games.exe";

    private const uint CreateSuspended              = 0x00000004;
    private const uint CreateNoWindow               = 0x08000000;
    private const uint ExtendedStartupInfoPresent   = 0x00080000;
    private const nint ProcThreadAttributeParentProcess = 0x00020000;

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private static readonly string[] TrustedPublishers =
        ["Hypergryph", "GRYPHLINE", "GRYPH FRONTIER"];

    private static readonly string[] ProtocolPrefixes =
        ["hypergryphlauncher", "gryphlinelauncher"];

    private nint _suspendedProcess;
    private nint _suspendedThread;
    private bool _disposed;

    private OfficialLauncherBridge(nint process, nint thread)
    {
        _suspendedProcess = process;
        _suspendedThread  = thread;
    }

    public static bool TryCreate(string? hintDirectory, string? hintExecutable,
        out OfficialLauncherBridge? bridge, out string? launcherExecutablePath)
    {
        bridge                 = null;
        launcherExecutablePath = null;

        foreach (string candidate in EnumerateCandidates(hintDirectory, hintExecutable))
        {
            if (!IsTrustedPublisher(candidate))
            {
                continue;
            }

            if (!TryCreateSuspended(candidate, out nint process, out nint thread))
            {
                continue;
            }

            bridge                 = new OfficialLauncherBridge(process, thread);
            launcherExecutablePath = candidate;

            SharedStatic.InstanceLogger.LogInformation($"[HgBridge] Official launcher resolved: {candidate}");
            return true;
        }

        SharedStatic.InstanceLogger.LogWarning("[HgBridge] No trusted official launcher executable found.");
        return false;
    }

    public Process StartGame(string targetExecutablePath, string? arguments, string? workingDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nint attributeListSize = 0;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attributeListSize);

        nint attributeList = Marshal.AllocHGlobal(attributeListSize);
        nint commandLine   = nint.Zero;

        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "InitializeProcThreadAttributeList failed.");
            }

            unsafe
            {
                nint parent = _suspendedProcess;
                if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeParentProcess,
                        (nint)(&parent), (nint)sizeof(nint), nint.Zero, nint.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "UpdateProcThreadAttribute failed.");
                }
            }

            if (!string.IsNullOrEmpty(arguments))
            {
                commandLine = Marshal.StringToCoTaskMemUni($"\"{targetExecutablePath}\" {arguments}");
            }

            STARTUPINFOEX startupInfo = default;
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.lpAttributeList = attributeList;

            if (!CreateProcess(targetExecutablePath, commandLine, nint.Zero, nint.Zero, false,
                    ExtendedStartupInfoPresent, nint.Zero, workingDirectory, in startupInfo,
                    out PROCESS_INFORMATION processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed.");
            }

            CloseHandle(processInformation.hThread);
            return Process.GetProcessById(processInformation.dwProcessId);
        }
        finally
        {
            if (commandLine != nint.Zero)
            {
                Marshal.FreeCoTaskMem(commandLine);
            }

            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_suspendedProcess != nint.Zero)
        {
            TerminateProcess(_suspendedProcess, 0);
            CloseHandle(_suspendedProcess);
            _suspendedProcess = nint.Zero;
        }

        if (_suspendedThread != nint.Zero)
        {
            CloseHandle(_suspendedThread);
            _suspendedThread = nint.Zero;
        }
    }

    private static IEnumerable<string> EnumerateCandidates(string? hintDirectory, string? hintExecutable)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(hintExecutable) && seen.Add(hintExecutable))
        {
            yield return hintExecutable;
        }

        foreach (string directory in EnumerateLauncherDirectories(hintDirectory))
        {
            foreach (string executable in EnumerateLauncherExecutables(directory))
            {
                if (seen.Add(executable))
                {
                    yield return executable;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLauncherDirectories(string? hintDirectory)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(hintDirectory) && Directory.Exists(hintDirectory))
        {
            if (seen.Add(hintDirectory))
            {
                yield return hintDirectory;
            }
        }

        foreach (string directory in EnumerateProtocolDirectories())
        {
            if (seen.Add(directory))
            {
                yield return directory;
            }
        }
    }

    private static List<string> EnumerateProtocolDirectories()
    {
        List<string> directories = [];

        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? classes = root.OpenSubKey(@"Software\Classes");
            if (classes == null)
            {
                continue;
            }

            foreach (string subKeyName in classes.GetSubKeyNames())
            {
                if (!ProtocolPrefixes.Any(prefix =>
                        subKeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                using RegistryKey? command = classes.OpenSubKey($@"{subKeyName}\shell\open\command");
                if (command?.GetValue(null) is not string commandLine)
                {
                    continue;
                }

                string? executable = ExtractExecutablePath(commandLine);
                string? directory  = executable == null ? null : Path.GetDirectoryName(executable);

                if (!string.IsNullOrEmpty(directory))
                {
                    directories.Add(directory);
                }
            }
        }

        return directories;
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        string trimmed = commandLine.Trim();

        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        int executableIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableIndex >= 0 ? trimmed[..(executableIndex + 4)] : null;
    }

    private static IEnumerable<string> EnumerateLauncherExecutables(string directory)
    {
        string direct = Path.Combine(directory, LauncherExecutableName);
        if (File.Exists(direct))
        {
            yield return direct;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                $"[HgBridge] Cannot enumerate {directory}: {ex.Message}");
            yield break;
        }

        foreach (string child in children)
        {
            string nested = Path.Combine(child, LauncherExecutableName);
            if (File.Exists(nested))
            {
                yield return nested;
            }
        }
    }

    private static bool IsTrustedPublisher(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return false;
        }

        uint result = WinVerifyTrustFile(executablePath);
        if (result != 0)
        {
            SharedStatic.InstanceLogger.LogWarning(
                $"[HgBridge] Signature verification failed (0x{result:x8}): {executablePath}");
            return false;
        }

        try
        {
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(executablePath);
            string subject = certificate.Subject;

            if (TrustedPublishers.Any(publisher =>
                    subject.Contains(publisher, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            SharedStatic.InstanceLogger.LogWarning($"[HgBridge] Unexpected signer: {subject}");
            return false;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning($"[HgBridge] Cannot read signer certificate: {ex.Message}");
            return false;
        }
    }

    private static bool TryCreateSuspended(string executablePath, out nint process, out nint thread)
    {
        process = nint.Zero;
        thread  = nint.Zero;

        STARTUPINFOEX startupInfo = default;
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        if (!CreateProcess(executablePath, nint.Zero, nint.Zero, nint.Zero, false,
                CreateSuspended | CreateNoWindow, nint.Zero, Path.GetDirectoryName(executablePath),
                in startupInfo, out PROCESS_INFORMATION processInformation))
        {
            int error = Marshal.GetLastWin32Error();
            SharedStatic.InstanceLogger.LogWarning(
                $"[HgBridge] Cannot create suspended launcher process: {new Win32Exception(error).Message}");
            return false;
        }

        process = processInformation.hProcess;
        thread  = processInformation.hThread;
        return true;
    }

    private static uint WinVerifyTrustFile(string filePath)
    {
        nint nativeFilePath = Marshal.StringToCoTaskMemUni(filePath);
        nint nativeFileInfo = nint.Zero;
        nint nativeTrustData = nint.Zero;

        try
        {
            WINTRUST_FILE_INFO fileInfo = default;
            fileInfo.cbStruct       = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>();
            fileInfo.pcwszFilePath  = nativeFilePath;

            nativeFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, nativeFileInfo, false);

            WINTRUST_DATA trustData = default;
            trustData.cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>();
            trustData.dwUIChoice          = 2;
            trustData.fdwRevocationChecks = 0;
            trustData.dwUnionChoice       = 1;
            trustData.pFile               = nativeFileInfo;
            trustData.dwStateAction       = 1;
            trustData.dwProvFlags         = 0x100;

            nativeTrustData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(trustData, nativeTrustData, false);

            uint result = WinVerifyTrust(nint.Zero, in WinTrustActionGenericVerifyV2, nativeTrustData);

            trustData.dwStateAction = 2;
            Marshal.StructureToPtr(trustData, nativeTrustData, false);
            WinVerifyTrust(nint.Zero, in WinTrustActionGenericVerifyV2, nativeTrustData);

            return result;
        }
        finally
        {
            if (nativeTrustData != nint.Zero)
            {
                Marshal.FreeHGlobal(nativeTrustData);
            }

            if (nativeFileInfo != nint.Zero)
            {
                Marshal.FreeHGlobal(nativeFileInfo);
            }

            Marshal.FreeCoTaskMem(nativeFilePath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string? lpApplicationName, nint lpCommandLine,
        nint lpProcessAttributes, nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, nint lpEnvironment,
        string? lpCurrentDirectory, in STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount,
        int dwFlags, ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nint attribute,
        nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial uint WinVerifyTrust(nint hwnd, in Guid pgActionID, nint pWVTData);
}
