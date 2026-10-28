using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NebulaAuth.Core.Browser;

/// <summary>
///     WebView2 engine processes (<c>msedgewebview2.exe</c>) started by this app: descendants of the current process.
///     Other apps' WebView2 processes are never included.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsProcesses
{
    private const string WEBVIEW2_PROCESS = "msedgewebview2";
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MAX_DEPTH = 6;

    public static List<int> OwnWebViewProcesses()
    {
        var root = Environment.ProcessId;
        var procs = Process.GetProcessesByName(WEBVIEW2_PROCESS);
        var parent = new Dictionary<int, int>();
        foreach (var p in procs)
        {
            parent[p.Id] = ParentOf(p.Id);
            p.Dispose();
        }

        var result = new List<int>();
        foreach (var pid in parent.Keys)
        {
            var cur = pid;
            // The chain from an engine process up to this process consists of engine processes only
            for (var depth = 0; depth < MAX_DEPTH && parent.TryGetValue(cur, out var pp); depth++)
            {
                if (pp == root)
                {
                    result.Add(pid);
                    break;
                }

                cur = pp;
            }
        }

        return result;
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Terminates <paramref name="pid" /> only if it is still one of this app's WebView2 processes.</summary>
    public static bool KillOwn(int pid)
    {
        if (!OwnWebViewProcesses().Contains(pid)) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            p.WaitForExit(3000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IEnumerable<int> Alive(IEnumerable<int> pids)
    {
        return pids.Where(IsAlive);
    }

    private static int ParentOf(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(h, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int) info.InheritedFromUniqueProcessId
                : -1;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref ProcessBasicInformation info,
        int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
