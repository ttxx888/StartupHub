using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace StartupHub.App;

public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private static bool CurrentUser(string value)
    {
        if (value == Sid) return true;
        try { return ((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value == Sid; }
        catch (IdentityNotMappedException) { return false; }
    }
    private static dynamic Folder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect(); return service.GetFolder(@"\");
    }
    public static bool Enabled
    {
        get
        {
            try
            {
                dynamic task = Folder().GetTask("StartupHub"); dynamic d = task.Definition;
                return task.Enabled && d.Actions.Count == 1 && d.Actions[1].Path.Equals(App.Executable, StringComparison.OrdinalIgnoreCase)
                    && d.Actions[1].Arguments == "--autostart --tray" && d.Actions[1].WorkingDirectory.Equals(Path.GetDirectoryName(App.Executable), StringComparison.OrdinalIgnoreCase)
                    && d.Triggers.Count == 1 && d.Triggers[1].Type == 9 && d.Triggers[1].Enabled && CurrentUser((string)d.Triggers[1].UserId) && CurrentUser((string)d.Principal.UserId) && d.Principal.LogonType == 3;
            }
            catch (COMException) { return false; }
        }
    }
    public static object Status()
    {
        try
        {
            dynamic task = Folder().GetTask("StartupHub"); dynamic d = task.Definition;
            return new { Enabled, Name = "StartupHub", User = (string)d.Principal.UserId, TriggerUser = (string)d.Triggers[1].UserId, TriggerType = (int)d.Triggers[1].Type,
                Executable = (string)d.Actions[1].Path, Arguments = (string)d.Actions[1].Arguments, WorkingDirectory = (string)d.Actions[1].WorkingDirectory,
                LastRun = (DateTime)task.LastRunTime, LastResult = (int)task.LastTaskResult };
        }
        catch (COMException) { return new { Enabled = false, Name = "StartupHub", Executable = App.Executable }; }
    }
    public static void Set(bool enabled)
    {
        dynamic folder = Folder();
        if (enabled)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; service.Connect();
            dynamic d = service.NewTask(0);
            d.RegistrationInfo.Description = "启动台：当前用户登录后执行自启清单";
            d.Principal.UserId = Sid; d.Principal.LogonType = 3; d.Principal.RunLevel = 0;
            d.Settings.Enabled = true; d.Settings.StartWhenAvailable = true;
            d.Settings.DisallowStartIfOnBatteries = false; d.Settings.StopIfGoingOnBatteries = false;
            d.Settings.ExecutionTimeLimit = "PT0S"; d.Settings.MultipleInstances = 2;
            dynamic trigger = d.Triggers.Create(9); trigger.UserId = Sid; trigger.Enabled = true;
            dynamic action = d.Actions.Create(0); action.Path = App.Executable; action.Arguments = "--autostart --tray"; action.WorkingDirectory = Path.GetDirectoryName(App.Executable);
            folder.RegisterTaskDefinition("StartupHub", d, 6, Sid, null, 3);
        }
        else
        {
            try { folder.GetTask("StartupHub"); }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { RemoveRun(); return; }
            folder.DeleteTask("StartupHub", 0);
        }
        RemoveRun();
    }
    private static void RemoveRun() { using var key = Registry.CurrentUser.OpenSubKey(Key, true); key?.DeleteValue("StartupHub", false); }
}
