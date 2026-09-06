using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AKTelaCapture;

internal static class ProcessTreeHelper
{
    public static int? FindDiscordRootProcessId()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };
        var ids = Process.GetProcesses().Where(p => { try { return names.Contains(p.ProcessName); } catch { return false; } }).Select(p => p.Id).ToHashSet();
        if (ids.Count == 0) return null;
        var parents = Parents();
        var audioProcesses = FindActiveAudioProcesses(ids);
        return SelectRootProcessId(ids, parents, audioProcesses);
    }

    // O Discord usa vários processos Electron e, em algumas atualizações/reinícios,
    // pode haver mais de uma árvore ao mesmo tempo. Priorizar o PID que possui uma
    // sessão de áudio ativa evita excluir uma árvore antiga enquanto a chamada real
    // continua entrando na captura do sistema.
    private static HashSet<int> FindActiveAudioProcesses(HashSet<int> discordIds)
    {
        var active = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    if (sessions is null) continue;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        var pid = (int)session.GetProcessID;
                        if (discordIds.Contains(pid) && session.State == AudioSessionState.AudioSessionStateActive)
                            active.Add(pid);
                    }
                }
            }
        }
        catch
        {
            // A enumeração de sessões é apenas uma preferência. Se um driver não a
            // oferecer, a seleção estrutural abaixo ainda encontra a árvore principal.
        }
        return active;
    }

    internal static int? SelectRootProcessId(
        IReadOnlyCollection<int> processIds,
        IReadOnlyDictionary<int, int> parents,
        IReadOnlyCollection<int>? preferredProcessIds = null)
    {
        if (processIds.Count == 0) return null;
        var ids = processIds.ToHashSet();

        int RootOf(int pid)
        {
            var current = pid;
            var seen = new HashSet<int>();
            while (seen.Add(current) && parents.TryGetValue(current, out var parent) && ids.Contains(parent))
                current = parent;
            return current;
        }

        var roots = ids.GroupBy(RootOf).ToDictionary(group => group.Key, group => group.Count());
        var preferredRoots = (preferredProcessIds ?? Array.Empty<int>())
            .Where(ids.Contains)
            .Select(RootOf)
            .ToHashSet();
        IEnumerable<int> candidates = preferredRoots.Count > 0 ? preferredRoots : roots.Keys;

        return candidates
            .OrderByDescending(root => roots.GetValueOrDefault(root))
            .ThenBy(ProcessStartTime)
            .First();
    }

    private static DateTime ProcessStartTime(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime; }
        catch { return DateTime.MaxValue; }
    }
    public static int FindApplicationRootProcessId(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid); var name = p.ProcessName; var parents = Parents(); var current = pid; var seen = new HashSet<int>();
            while (seen.Add(current) && parents.TryGetValue(current, out var parent) && parent > 0)
            {
                try { using var pp = Process.GetProcessById(parent); if (!pp.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) break; current = parent; }
                catch { break; }
            }
            return current;
        }
        catch { return pid; }
    }
    private static Dictionary<int, int> Parents()
    {
        var map = new Dictionary<int, int>(); var snap = CreateToolhelp32Snapshot(2, 0); if (snap == new IntPtr(-1)) return map;
        try { var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(), szExeFile = string.Empty }; if (!Process32First(snap, ref e)) return map; do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(); } while (Process32Next(snap, ref e)); }
        finally { CloseHandle(snap); }
        return map;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct PROCESSENTRY32 { public uint dwSize,cntUsage,th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID,cntThreads,th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string szExeFile; }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
