using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StartupHub.Core;

public static class NativeProcess
{
    private static readonly object LaunchGate = new();
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var pointer = CommandLineToArgvW("shortcut.exe " + arguments, out int count);
        if (pointer == IntPtr.Zero) throw new Win32Exception();
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    public static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static (string Exe, string CommandLine) Command(TaskEntry entry)
    {
        var path = entry.Path;
        var args = new List<string>();
        if (entry.Script.Length > 0) args.Add(entry.Script);
        args.AddRange(entry.Arguments);
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".ps1":
                args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path, .. entry.Arguments];
                path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                break;
            case ".cmd": case ".bat":
                var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "C:\\Windows\\System32\\cmd.exe";
                var scriptCommand = "\"" + path + "\"" + (entry.Arguments.Count > 0 ? " " + string.Join(" ", entry.Arguments.Select(CmdArgument)) : "");
                return (cmd, Quote(cmd) + " /d /s /v:off /c \"" + scriptCommand + "\"");
            case ".py":
                args = [path, .. entry.Arguments]; path = FindOnPath("python.exe"); break;
            case ".js": case ".mjs": case ".cjs":
                args = [path, .. entry.Arguments]; path = FindOnPath("node.exe"); break;
        }
        return (path, Quote(path) + (args.Count == 0 ? "" : " " + string.Join(" ", args.Select(Quote))));
    }
    private static string CmdArgument(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string FindOnPath(string file) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(x => System.IO.Path.Combine(x.Trim('"'), file)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException($"找不到 {file}，请在程序路径中指定解释器，并将脚本填入脚本路径。");
    public static Process Launch(TaskEntry entry, string stdout, string stderr)
    {
        lock (LaunchGate)
        {
            var (exe, command) = Command(entry);
            var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables()) environment[(string)pair.Key] = (string?)pair.Value ?? "";
            environment["PYTHONUTF8"] = "1"; environment["PYTHONIOENCODING"] = "utf-8";
            environment["NO_COLOR"] = "1"; environment["FORCE_COLOR"] = "0";
            foreach (var pair in entry.Environment) environment[pair.Key] = pair.Value;
            var env = Marshal.StringToHGlobalUni(string.Join('\0', environment.Select(x => x.Key + "=" + x.Value)) + "\0\0");
            var sx = new StartupInfoEx { Info = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() } };
            ref var si = ref sx.Info;
            IntPtr handles = IntPtr.Zero;
            FileStream? output = null, error = null;
            IntPtr input = IntPtr.Zero;
            try
            {
                if (!entry.VisibleConsole)
                {
                    if (!entry.SuppressOutput) { Rotate(stdout); Rotate(stderr); }
                    output = new FileStream(entry.SuppressOutput ? "NUL" : stdout, entry.SuppressOutput ? FileMode.Open : FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    error = new FileStream(entry.SuppressOutput ? "NUL" : stderr, entry.SuppressOutput ? FileMode.Open : FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    SetHandleInformation(output.SafeFileHandle.DangerousGetHandle(), 1, 1);
                    SetHandleInformation(error.SafeFileHandle.DangerousGetHandle(), 1, 1);
                    input = CreateFile("NUL", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (input == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    SetHandleInformation(input, 1, 1);
                    si.dwFlags = 0x100;
                    si.hStdInput = input; si.hStdOutput = output.SafeFileHandle.DangerousGetHandle(); si.hStdError = error.SafeFileHandle.DangerousGetHandle();
                    nuint size = 0;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    sx.Attributes = Marshal.AllocHGlobal((nint)size);
                    if (!InitializeProcThreadAttributeList(sx.Attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                    Marshal.Copy(new[] { input, si.hStdOutput, si.hStdError }, 0, handles, 3);
                    if (!UpdateProcThreadAttribute(sx.Attributes, 0, (nuint)0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var cwd = string.IsNullOrWhiteSpace(entry.WorkingDir) ? System.IO.Path.GetDirectoryName(entry.Path) : entry.WorkingDir;
                if (!CreateProcess(exe, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, !entry.VisibleConsole,
                    0x400u | 0x4u | 0x80000u | (entry.VisibleConsole ? 0x10u : 0x08000000u), env, cwd, ref sx, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                var process = Process.GetProcessById(pi.ProcessId);
                _ = process.Handle;
                _ = process.StartTime;
                ResumeThread(pi.Thread);
                ProcessInspector.Invalidate();
                return process;
                }
                finally { CloseHandle(pi.Process); CloseHandle(pi.Thread); }
            }
            finally
            {
                output?.Dispose(); error?.Dispose();
                if (sx.Attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(sx.Attributes); Marshal.FreeHGlobal(sx.Attributes); }
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (input != IntPtr.Zero && input != new IntPtr(-1)) CloseHandle(input);
                Marshal.FreeHGlobal(env);
            }
        }
    }
    internal static void Rotate(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, path + ".previous", true);
    }
    public static Process? Open(string path, IEnumerable<string>? args = null, string? cwd = null)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(cwd)) info.WorkingDirectory = cwd;
        if (args != null) foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
}
