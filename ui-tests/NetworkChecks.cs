using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class NetworkChecks
{
    public static void Run(DataStore store, Action<bool, string> check)
    {
        var clash = new TaskEntry { Name = "代理", Kind = TaskKind.Desktop, Path = Environment.ProcessPath!, WaitForReady = true, Port = 7897 };
        var network = new TaskEntry { Name = "网络", Kind = TaskKind.Network, Url = "http://127.0.0.1/", WaitSeconds = 300, DependsOn = clash.Id };
        var chat = new TaskEntry { Name = "目标", Kind = TaskKind.Desktop, AppUserModelId = "Example!App", DependsOn = network.Id, StartDelaySeconds = 10 };
        store.Config.Entries.AddRange([clash, network, chat]); store.SaveConfig();
        T Field<T>(EntryEditor editor, string name) => (T)typeof(EntryEditor).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        TaskEntry Save(TaskEntry task, Action<EntryEditor>? change = null)
        {
            var editor = new EntryEditor(store.Config, task); change?.Invoke(editor);
            Exception? error = null;
            editor.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    typeof(EntryEditor).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);
                    if (editor.DialogResult != true) throw new Exception(Field<TextBlock>(editor, "validation").Text);
                }
                catch (Exception ex) { error = ex; editor.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            bool? saved = editor.ShowDialog(); if (error != null) throw error;
            check(saved == true && task.Id == editor.Result.Id, "编辑保存并保留 ID：" + task.Name);
            return editor.Result;
        }
        var n = Save(network, e => Field<TextBox>(e, "proxy").Text = "http://127.0.0.1:17897");
        check(n.Kind == TaskKind.Network && n.WaitSeconds == 300 && n.DependsOn == clash.Id && n.ProxyUrl.EndsWith(":17897") && n.AutoLogin, "网络字段、自动登录和桌面依赖保存");
        var c = Save(chat); check(c.Path.Length == 0 && c.AppUserModelId == chat.AppUserModelId && c.StartDelaySeconds == 10 && c.DependsOn == network.Id, "包应用无路径、网络依赖和延迟保存");
        var d = Save(clash); check(d.WaitForReady && d.Port == 7897, "桌面就绪配置保存");
        var invalid = new EntryEditor(store.Config, network); Field<TextBox>(invalid, "wait").Text = "301";
        typeof(EntryEditor).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(invalid, null);
        check(Field<TextBlock>(invalid, "validation").Text.Contains("300"), "超过五分钟以内联提示拒绝"); invalid.Close();
    }
}
