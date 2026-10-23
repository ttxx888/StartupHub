using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace StartupHub.Core;

public sealed class LoginExecution(DataStore store, TaskEngine engine)
{
    private static readonly string session = ComputeSession();
    public static string Session => session;
    private static string ComputeSession()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (!GetTokenInformation(identity.Token, 10, buffer, 64, out _)) throw new System.ComponentModel.Win32Exception();
            return identity.User!.Value + ":" + Marshal.ReadInt64(buffer, 8).ToString("X");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Log(string source, string result, string[]? args = null)
    {
        lock (store.SyncRoot) File.AppendAllText(Path.Combine(store.Logs, "startup.jsonl"), JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Session, Source = source, Arguments = args ?? [], Result = result }) + Environment.NewLine);
    }
    public async Task<bool> ReceiveAsync(string source, string[] args)
    {
        lock (store.SyncRoot)
        {
            if (store.State.LastAutoStartSession == Session) { Log(source, "同一次 Windows 登录已接收，忽略重复通知", args); return false; }
            store.State.LastAutoStartSession = Session; store.SaveState();
        }
        Log(source, "接收登录通知，执行自启清单", args);
        await engine.StartBatchAsync();
        Log(source, "登录清单执行结束：" + string.Join("；", store.Config.Entries.Where(x => x.AutoStart).Select(x => x.Name + "=" + engine.Status(x.Id).Text + " " + engine.Status(x.Id).Detail)), args);
        return true;
    }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr info, int length, out int returned);
}
