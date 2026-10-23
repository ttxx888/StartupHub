using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StartupHub.Core;

public static class PackagedApp
{
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ActivationManager { }
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint pid);
        int ActivateForFile(IntPtr appId, IntPtr items, IntPtr verb, out uint pid);
        int ActivateForProtocol(IntPtr appId, IntPtr items, out uint pid);
    }
    public static Process Activate(string appId)
    {
        var manager = (IActivationManager)new ActivationManager();
        try { Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, null, 0, out uint pid)); return Process.GetProcessById((int)pid); }
        finally { Marshal.ReleaseComObject(manager); }
    }
    public static bool Matches(int pid, string appId)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            uint length = 256; var name = new StringBuilder((int)length);
            return GetApplicationUserModelId(process.Handle, ref length, name) == 0 && name.ToString().Equals(appId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder name);
}
