using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AKTelaCapture;

internal static class ProcessTreeHelper
{
    public static int? FindDiscordRootProcessId()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment"
        };

        var candidates = Process.GetProcesses()
            .Where(p =>
            {
                try { return names.Contains(p.ProcessName); }
                catch { return false; }
            })
            .Select(p => p.Id)
            .ToHashSet();

        if (candidates.Count == 0) return null;
        var parents = SnapshotParents();
        var roots = candidates.Where(pid => !parents.TryGetValue(pid, out var parent) || !candidates.Contains(parent)).ToList();
        if (roots.Count == 0) roots = candidates.ToList();

        return roots
            .OrderByDescending(pid => CountDescendants(pid, parents))
            .FirstOrDefault();
    }

    public static int FindApplicationRootProcessId(int processId)
    {
        try
        {
            using var start = Process.GetProcessById(processId);
            var targetName = start.ProcessName;
            var parents = SnapshotParents();
            var current = processId;
            var seen = new HashSet<int> { current };
            while (parents.TryGetValue(current, out var parent) && parent > 0 && seen.Add(parent))
            {
                try
                {
                    using var pp = Process.GetProcessById(parent);
                    if (!pp.ProcessName.Equals(targetName, StringComparison.OrdinalIgnoreCase)) break;
                    current = parent;
                }
                catch { break; }
            }
            return current;
        }
        catch
        {
            return processId;
        }
    }

    private static int CountDescendants(int root, Dictionary<int, int> parents)
    {
        var count = 0;
        foreach (var pid in parents.Keys)
        {
            var current = pid;
            var guard = 0;
            while (parents.TryGetValue(current, out var parent) && parent > 0 && guard++ < 64)
            {
                if (parent == root) { count++; break; }
                current = parent;
            }
        }
        return count;
    }

    private static Dictionary<int, int> SnapshotParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == INVALID_HANDLE_VALUE) return result;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(), szExeFile = string.Empty };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
