using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;

namespace StartupHub.Core;

public record ProcessInfo(int Pid, int ParentPid, string Path, string CommandLine, string Name = "");
public record ListenerEndpoint(string Address, int Port, int Pid);
public sealed record MachineSnapshot(Dictionary<int, ProcessInfo> Processes, Dictionary<int, List<int>> Listeners, IReadOnlyList<ListenerEndpoint>? Endpoints = null);

public static class ProcessInspector
{
    private const int SnapshotCacheMs = 700;
    private static MachineSnapshot? snapshotCache;
    private static long snapshotCacheAt;
    private static readonly object snapshotGate = new();
    public static void Invalidate() { lock (snapshotGate) snapshotCache = null; }
    public static MachineSnapshot Snapshot()
    {
        lock (snapshotGate)
        {
            if (snapshotCache != null && Environment.TickCount64 - snapshotCacheAt < SnapshotCacheMs) return snapshotCache;
            var snapshot = TakeSnapshot();
            snapshotCache = snapshot; snapshotCacheAt = Environment.TickCount64;
            return snapshot;
        }
    }
    private static MachineSnapshot TakeSnapshot()
    {
        var processes = new Dictionary<int, ProcessInfo>();
        using var search = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine, Name FROM Win32_Process");
        using var results = search.Get();
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                int pid = Convert.ToInt32(item["ProcessId"]);
                processes[pid] = new(pid, Convert.ToInt32(item["ParentProcessId"]), item["ExecutablePath"] as string ?? "", item["CommandLine"] as string ?? "", item["Name"] as string ?? "");
            }
        }
        var endpoints = ListenerEndpoints();
        return new(processes, GroupListeners(endpoints), endpoints);
    }
    public static Dictionary<int, List<int>> Listeners()
        => GroupListeners(ListenerEndpoints());
    private static Dictionary<int,List<int>> GroupListeners(IEnumerable<ListenerEndpoint> endpoints)
        => endpoints.GroupBy(x=>x.Port).ToDictionary(x=>x.Key,x=>x.Select(e=>e.Pid).Distinct().ToList());
    public static List<ListenerEndpoint> ListenerEndpoints()
    {
        var result = new List<ListenerEndpoint>();
        foreach (var family in new[] { 2, 23 })
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (size <= 0) continue;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) continue;
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * (family == 2 ? 24 : 56));
                    int portOffset = family == 2 ? 8 : 20;
                    int port = Marshal.ReadByte(row, portOffset) * 256 + Marshal.ReadByte(row, portOffset + 1);
                    int pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    var addressBytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row,family==2?4:0),addressBytes,0,addressBytes.Length);
                    var address = family == 2 ? new IPAddress(addressBytes) : new IPAddress(addressBytes,(uint)Marshal.ReadInt32(row,16));
                    result.Add(new(address.ToString(),port,pid));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }
    public static bool Alive(int pid, long ticks)
    {
        if (pid <= 0 || ticks == 0) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch { return false; }
    }
    public static long StartTicks(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; } catch { return 0; }
    }
    public static bool Belongs(TaskEntry e, int pid, ProcessRecord? record, MachineSnapshot snapshot)
    {
        if (!snapshot.Processes.TryGetValue(pid, out var process)) return false;
        if (record != null)
        {
            if (pid == record.Pid && Alive(pid, record.StartTicks)) return true;
            if (pid == record.ServicePid && Alive(pid, record.ServiceStartTicks)) return true;
            var current = pid;
            var visited = new HashSet<int>();
            while (visited.Add(current) && snapshot.Processes.TryGetValue(current, out var p))
            {
                if (p.ParentPid == record.Pid && Alive(record.Pid, record.StartTicks)) return true;
                current = p.ParentPid;
            }
        }
        if (e.ManagedPidFile.Length > 0 && e.ProcessMatch.Length > 0)
        {
            try { if (int.TryParse(ReadPidFile(e.ManagedPidFile).Trim(), out var stored) && stored == pid && process.CommandLine.Contains(e.ProcessMatch, StringComparison.OrdinalIgnoreCase)) return true; } catch (IOException) { }
        }
        if (e.Script.Length > 0) return SamePath(process.Path, e.Path) && ContainsPath(process.CommandLine, e.Script) && ArgumentsMatch(process.CommandLine, e.Arguments);
        var ext = System.IO.Path.GetExtension(e.Path).ToLowerInvariant();
        if (ext is ".ps1" or ".cmd" or ".bat" or ".py" or ".js" or ".mjs") return ContainsPath(process.CommandLine, e.Path) && ArgumentsMatch(process.CommandLine, e.Arguments);
        return e.Path.Length > 0 && SamePath(process.Path, e.Path) && ArgumentsMatch(process.CommandLine, e.Arguments);
    }
    private static (string Path, long Stamp, string Content)? pidFileCache;
    private static string ReadPidFile(string path)
    {
        try
        {
            long stamp = File.GetLastWriteTimeUtc(path).Ticks;
            if (pidFileCache is { } cached && cached.Path == path && cached.Stamp == stamp) return cached.Content;
            string content = File.ReadAllText(path);
            pidFileCache = (path, stamp, content);
            return content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
    private static bool ArgumentsMatch(string command, List<string> expected)
    {
        if (expected.Count == 0) return true;
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) return false;
        try
        {
            int matched = 0;
            for (int i = 1; i < count && matched < expected.Count; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) == expected[matched]) matched++;
            return matched == expected.Count;
        }
        finally { LocalFree(pointer); }
    }
    private static bool SamePath(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool ContainsPath(string text, string path) => path.Length > 0 && text.Replace('/', '\\').Contains(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
