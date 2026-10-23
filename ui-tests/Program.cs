using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var reports = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); reports.Add(name); Console.WriteLine("PASS " + name); }
        try
        {
            var app = new TestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.Light.xaml") });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/StartupHub;component/Theme.xaml") });
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            var store = new DataStore(root);
            ThemeManager.Initialize(store);
            if (!args.Contains("--theme")) ThemeManager.Apply("Light");
            var entry = new TaskEntry { Name = "测试网页", Kind = TaskKind.Url, Url = "http://127.0.0.1:54321/", Group = "常用服务" };
            store.Config.Entries.Add(entry); store.SaveConfig();
            using var engine = new TaskEngine(store);
            typeof(App).GetProperty(nameof(App.Store))!.SetValue(null, store);
            typeof(App).GetProperty(nameof(App.Engine))!.SetValue(null, engine);
            var vm = new MainViewModel(store, engine); var window = new MainWindow(vm); app.MainWindow = window;
            var rootView = (FrameworkElement)window.Content;
            rootView.Measure(new Size(1240,780)); rootView.Arrange(new Rect(0,0,1240,780)); rootView.UpdateLayout();
            if (args.Contains("--dialogs"))
            {
                DialogChecks.Run(window,vm,store,Check,root);
                File.WriteAllText(Path.Combine(root,"dialog-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--main-window"))
            {
                MainWindowChecks.Run(window,vm,store,engine,Check,root);
                File.WriteAllText(Path.Combine(root,"main-window-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--theme"))
            {
                ThemeChecks.Run(window,vm,store,engine,Check,root);
                File.WriteAllText(Path.Combine(root,"theme-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (Array.IndexOf(args,"--discover-live") is var liveIndex && liveIndex >= 0)
            {
                DiscoveryChecks.RunLive(window,vm,store,Check,args[liveIndex+1]);
                File.WriteAllText(Path.Combine(root,"discovery-live-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--discover"))
            {
                DiscoveryChecks.Run(window,Check,root);
                File.WriteAllText(Path.Combine(root,"discovery-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--session"))
            {
                SessionChecks.Run(window,vm,store,engine,Check);
                File.WriteAllText(Path.Combine(root,"session-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--import"))
            {
                ImportChecks.Run(window,vm,store,Check,root);
                File.WriteAllText(Path.Combine(root,"import-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--editor"))
            {
                EditorChecks.Run(window,vm,store,Check,root);
                File.WriteAllText(Path.Combine(root,"editor-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--batch"))
            {
                BatchChecks.Run(window, vm, store, Check, root);
                File.WriteAllText(Path.Combine(root,"batch-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--network"))
            {
                NetworkChecks.Run(store, Check);
                File.WriteAllText(Path.Combine(root,"network-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            if (args.Contains("--drag"))
            {
                DragChecks.Run(window, vm, store, Check);
                File.WriteAllText(Path.Combine(root,"drag-results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
                window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
            }
            window.ApplyDetailsLayout(1240);
            Check(window.DetailsVisible && !window.DetailsOverlay, "宽窗口默认展示详情");
            window.SetDetails("hide"); Check(!window.DetailsVisible && !new DataStore(root).State.DetailsVisible, "收起详情并保存偏好");
            window.SetDetails("toggle"); Check(window.DetailsVisible, "详情开关恢复");
            window.ApplyDetailsLayout(900); Check(!window.DetailsVisible, "窄窗口默认收起详情");
            window.SetDetails("show"); Check(window.DetailsOverlay && vm.Selected?.Entry.Id == entry.Id, "窄窗口覆盖详情并保留选中任务");
            window.SetDetails("hide"); window.ApplyDetailsLayout(1240); Check(window.DetailsVisible, "返回宽窗口保留展开偏好");
            vm.Search = "无匹配"; Check(vm.IsEmpty && vm.HasTasks, "搜索无结果与空清单区分");
            vm.ClearFiltersCommand.Execute(null); Check(!vm.IsEmpty && vm.Search == "", "清除筛选恢复列表");
            Check(window.InputBindings.OfType<KeyBinding>().Any(x => x.Key == Key.N && x.Modifiers == ModifierKeys.Control) && window.InputBindings.OfType<KeyBinding>().Any(x => x.Key == Key.F5), "新增与刷新快捷键绑定保留");
            Check(window.Icon != null && window.FontFamily.Source == "Microsoft YaHei UI", "窗口图标及中文界面字体已应用");
            Console.WriteLine("Window font: " + window.FontFamily.Source);
            var editor = new EntryEditor(store.Config, entry);
            var name = (TextBox)typeof(EntryEditor).GetField("name", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
            var validation = (TextBlock)typeof(EntryEditor).GetField("validation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
            var save = typeof(EntryEditor).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
            name.Text = ""; save.Invoke(editor,null);
            Check(validation.Visibility == Visibility.Visible && validation.Text.Length > 0, "编辑器错误以内联文字显示");
            Exception? editError = null;
            editor.Dispatcher.BeginInvoke(() => { try { name.Text = "更新后的名称"; save.Invoke(editor,null); } catch(Exception ex) { editError=ex; editor.Close(); } }, DispatcherPriority.ApplicationIdle);
            bool? saved = editor.ShowDialog();
            if (editError != null) throw editError;
            Check(saved == true && editor.Result.Name == "更新后的名称" && editor.Result.Id == entry.Id && editor.Result.Url == entry.Url, "编辑器保存名称并保留 ID 与入口");
            store.ReplaceConfig(new HubConfig()); vm.Reload(); Check(vm.IsEmpty && vm.HasNoTasks, "空清单提供添加入口");
            var button = new Button { Content = "测试" }; button.Style = (Style)app.FindResource(typeof(Button)); button.ApplyTemplate();
            Check(button.FocusVisualStyle != null, "按钮提供独立键盘焦点样式");
            var primary = new Button { Style = (Style)app.FindResource("PrimaryButton") };
            Check(ThemeChecks.Contrast(primary.Foreground,primary.Background) >= 4.5 && ThemeChecks.Contrast((Brush)app.FindResource("MutedBrush"),(Brush)app.FindResource("CanvasBrush")) >= 4.5, "主按钮和次文字实资源对比度达到 4.5:1");
            File.WriteAllText(Path.Combine(root,"results.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed=reports.Count,Checks=reports },DataStore.Json));
            window.ExitApplication(); Console.WriteLine($"RESULT {reports.Count} passed: {root}"); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
internal sealed class TestApp : App
{
    protected override void OnStartup(StartupEventArgs e) { }
}
