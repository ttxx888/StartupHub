using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using StartupHub.App;
using StartupHub.Core;

internal static class ThemeChecks
{
    internal static void Run(MainWindow main, MainViewModel vm, DataStore store, TaskEngine engine, Action<bool,string> check, string root)
    {
        string configBefore = File.ReadAllText(Path.Combine(store.Root,"config.json"));
        var legacy = JsonSerializer.Deserialize<HubState>("{\"Width\":1120,\"DetailsVisible\":false}",DataStore.Json)!;
        check(legacy.ThemePreference == "System" && legacy.Width == 1120 && !legacy.DetailsVisible,"旧界面状态保留原字段并默认跟随系统");
        check(ThemeManager.Preference == "System" && ThemeManager.EffectiveTheme is "Light" or "Dark","默认主题偏好跟随系统并解析为有效主题");
        string expectedSystem = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int mode && mode == 0 ? "Dark" : "Light";
        check(ThemeManager.EffectiveTheme == expectedSystem,"跟随系统使用当前 Windows 应用主题设置");
        int changes = 0;
        EventHandler changed = (_,_) => changes++;
        ThemeManager.Changed += changed;

        var text = new TextBox { Text = "中文输入与主题", Margin = new Thickness(8) };
        var button = new Button { Content = "普通按钮" };
        var primary = new Button { Content = "启动", Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var combo = new ComboBox { ItemsSource = new[] { "常用服务", "开发网页" }, SelectedIndex = 0 };
        var statusRow = vm.Rows.First();
        statusRow.Status = new StartupHub.Core.TaskStatus(RunStatus.Running);
        var status = new TextBlock { DataContext = statusRow, Text = "运行中" };
        status.SetBinding(TextBlock.ForegroundProperty,new Binding(nameof(TaskRow.StatusBrush)));
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (var child in new UIElement[] { text,button,primary,combo,status }) panel.Children.Add(child);
        var probe = new Window { Title = "主题组件验证", Width = 440,Height = 360,Content = panel,ShowInTaskbar = false,ShowActivated = false };
        Window[] auxiliary = [new EntryEditor(store.Config,store.Config.Entries.First()),new SettingsWindow(store),new GroupsWindow(store),new DiscoverWindow([],store.Config.Groups),new ImportTasksWindow([],store.Config.Groups,store.Config.Groups.First())];
        probe.Show();
        try
        {
            ThemeManager.Apply("Light"); Flush();
            Color lightCanvas = ColorOf(probe.Background), lightSurface = ColorOf(text.Background), lightStatus = ColorOf(status.Foreground);
            combo.IsDropDownOpen = true; Flush();
            foreach (string theme in new[] { "Dark","Light" })
            {
                ThemeManager.Apply(theme); Flush();
                check(ThemeManager.EffectiveTheme == theme && ColorOf(probe.Background) == Resource("CanvasBrush"),theme + "已创建窗口背景随主题更新");
                check(ColorOf(text.Background) == Resource("SurfaceBrush") && ColorOf(text.Foreground) == Resource("TextBrush"),theme + "已创建文本框表面与文字随主题更新");
                check(ColorOf(button.Background) == Resource("SurfaceBrush") && ColorOf(button.Foreground) == Resource("TextBrush"),theme + "已创建普通按钮随主题更新");
                check(ColorOf(primary.Background) == Resource("PrimaryBrush") && ColorOf(primary.Foreground) == Resource("PrimaryTextBrush"),theme + "已创建强调按钮随主题更新");
                check(ColorOf(status.Foreground) == Resource("SuccessBrush"),theme + "已有任务状态绑定随主题更新");
                check(ColorOf(combo.Background) == Resource("SurfaceBrush") && ColorOf(combo.Foreground) == Resource("TextBrush"),theme + "已创建下拉框随主题更新");
                foreach (var window in auxiliary)
                    check(ColorOf(window.Background) == Resource("CanvasBrush") && ColorOf(window.Foreground) == Resource("TextBrush"),theme + "已创建" + window.Title + "随主题更新");
                var popup = Descendants<Popup>(combo).FirstOrDefault() ?? combo.Template.FindName("PART_Popup",combo) as Popup;
                check(popup?.IsOpen == true && popup.Child != null,theme + "下拉弹层可正常打开");
                var backgrounds = Descendants<Border>(popup!.Child!).Where(x => x.Background is SolidColorBrush).Select(x => ColorOf(x.Background)).ToArray();
                check(backgrounds.Contains(Resource("ElevatedBrush")),theme + "下拉弹层实际表面使用当前主题");
                var item = combo.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem;
                check(item != null && ColorOf(item.Foreground) == Resource("TextBrush"),theme + "下拉选项文字使用当前主题");
                CheckContrasts(check,primary,theme);
                if (theme == "Dark") check(lightCanvas != ColorOf(probe.Background) && lightSurface != ColorOf(text.Background) && lightStatus != ColorOf(status.Foreground),"深浅切换实际改变画布、控件和状态颜色");
            }
            combo.IsDropDownOpen = false;

            probe.Activate(); text.Focus(); text.Select(2,3); Flush();
            ThemeManager.Apply("Dark"); Flush();
            check(text.IsKeyboardFocused && text.Text == "中文输入与主题" && text.SelectionStart == 2 && text.SelectionLength == 3,"主题切换保留文本、键盘焦点与选区");
            var editable = new ComboBox { IsEditable = true, ItemsSource = new[] { "常用服务", "开发网页" } };
            panel.Children.Add(editable); Flush(); editable.Text = "中文分类"; Flush();
            var editableText = editable.Template.FindName("PART_EditableTextBox",editable) as TextBox;
            check(editableText?.Text == "中文分类","可编辑下拉框保留自定义中文分类输入");
            editable.SelectedIndex = 1; Flush();
            check(editable.Text == "开发网页","可编辑下拉框仍可选择既有分类");

            ThemeManager.SetPreference("Dark"); Flush();
            check(store.State.ThemePreference == "Dark" && new DataStore(store.Root).State.ThemePreference == "Dark","主题选择立即写入界面状态");
            ThemeManager.Initialize(new DataStore(store.Root)); Flush();
            check(ThemeManager.Preference == "Dark" && ThemeManager.EffectiveTheme == "Dark","重新初始化恢复已保存深色主题");
            ThemeManager.Initialize(store);
            ThemeManager.Apply("Light"); Flush();
            check(ThemeManager.Preference == "Light" && ThemeManager.EffectiveTheme == "Light" && new DataStore(store.Root).State.ThemePreference == "Dark","临时主题不改变已保存偏好");
            ThemeManager.SetPreference("System"); Flush();
            string systemTheme = ThemeManager.EffectiveTheme;
            ThemeManager.Apply(systemTheme == "Dark" ? "Light" : "Dark");
            ThemeManager.Apply("System"); Flush();
            check(ThemeManager.Preference == "System" && ThemeManager.EffectiveTheme == systemTheme && new DataStore(store.Root).State.ThemePreference == "System","切回跟随系统恢复当前系统主题并持久化");
            check(changes > 0,"主题切换发布变更通知");
            check(File.ReadAllText(Path.Combine(store.Root,"config.json")) == configBefore,"主题操作不修改任务配置");

            var api = new CommandApi(store,engine);
            var state = Await(UiCommands.ExecuteAsync(main,vm,api,["ui","state"]));
            var stateJson = JsonSerializer.SerializeToElement(state.Data,DataStore.Json);
            check(state.Ok && stateJson.GetProperty("ThemePreference").GetString() == "System" && stateJson.GetProperty("EffectiveTheme").GetString() == systemTheme,"ui state 返回主题偏好与当前有效主题");
            string output = Path.Combine(root,"theme-dark-main.png");
            var snapshot = Await(UiCommands.ExecuteAsync(main,vm,api,["ui","snapshot","--theme","dark","--output",output]));
            check(snapshot.Ok && File.Exists(output) && new FileInfo(output).Length > 0,"指定主题截图生成有效文件");
            check(ThemeManager.Preference == "System" && ThemeManager.EffectiveTheme == systemTheme && new DataStore(store.Root).State.ThemePreference == "System","截图后恢复有效主题并保留偏好");
            main.Show(); Flush();
            double windowWidth = main.Width, windowHeight = main.Height;
            var narrow = Await(UiCommands.ExecuteAsync(main,vm,api,["ui","snapshot","--theme","dark","--width","900","--height","500","--scale","1.5","--output",Path.Combine(root,"theme-narrow.png")]));
            var rendered = JsonSerializer.SerializeToElement(narrow.Data,DataStore.Json);
            check(rendered.GetProperty("RenderWidth").GetDouble() == 900 && rendered.GetProperty("RenderHeight").GetDouble() == 500,"可见大窗口按窄尺寸重新布局截图而非裁切");
            check(main.Width == windowWidth && main.Height == windowHeight && double.IsNaN(((FrameworkElement)main.Content).Width) && double.IsNaN(((FrameworkElement)main.Content).Height),"截图恢复内容尺寸并保留实际窗口大小");
            Flush();
            main.SetDetails("show");
            check(((FrameworkElement)main.FindName("RootLayout")).ActualWidth > 1120 && !main.DetailsOverlay, "窄尺寸截图后恢复实际宽窗口布局及详情断点");
            main.Hide();
            string invalidOutput = Path.Combine(root,"snapshot-output-directory"); Directory.CreateDirectory(invalidOutput);
            bool failed = false;
            try { Await(UiCommands.ExecuteAsync(main,vm,api,["ui","snapshot","--theme",systemTheme == "Dark" ? "Light" : "Dark","--output",invalidOutput])); }
            catch (UnauthorizedAccessException) { failed = true; }
            catch (IOException) { failed = true; }
            check(failed && ThemeManager.EffectiveTheme == systemTheme && ThemeManager.Preference == "System","截图输出失败仍恢复主题");
        }
        finally
        {
            combo.IsDropDownOpen = false;
            probe.Close();
            foreach (var window in auxiliary) window.Close();
            ThemeManager.Changed -= changed;
            ThemeManager.Initialize(store);
            ThemeManager.Apply("System");
        }
    }

    private static void CheckContrasts(Action<bool,string> check,Button primary,string theme)
    {
        foreach (string background in new[] { "CanvasBrush","SurfaceBrush","SidebarBrush","SelectedBrush" })
            foreach (string foreground in new[] { "TextBrush","MutedBrush" })
                check(Contrast(Brush(foreground),Brush(background)) >= 4.5,theme + " " + foreground + "/" + background + " 正文对比度达到 4.5:1");
        foreach (string foreground in new[] { "SuccessBrush","DangerBrush","WaitingBrush" })
            check(Contrast(Brush(foreground),Brush("SurfaceBrush")) >= 4.5,theme + " " + foreground + " 状态文字对比度达到 4.5:1");
        check(Contrast(primary.Foreground,primary.Background) >= 4.5,theme + " 强调按钮实资源对比度达到 4.5:1");
    }
    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static Color Resource(string key) => ColorOf(Brush(key));
    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
    internal static double Contrast(Brush foreground,Brush background)
    {
        double L(Color color) { double V(byte value) { double n = value / 255.0; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055,2.4); } return .2126 * V(color.R) + .7152 * V(color.G) + .0722 * V(color.B); }
        double a = L(ColorOf(foreground)), b = L(ColorOf(background));
        return (Math.Max(a,b) + .05) / (Math.Min(a,b) + .05);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static void Flush() => Application.Current.Dispatcher.Invoke(() => { },DispatcherPriority.ApplicationIdle);
    private static T Await<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
}
