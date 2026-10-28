using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class DialogChecks
{
    internal static void Run(MainWindow main, MainViewModel vm, DataStore store, Action<bool, string> check, string root)
    {
        string before = JsonSerializer.Serialize(store.Config, DataStore.Json);
        main.Show(); main.Activate(); Flush();
        var search = (TextBox)main.FindName("SearchBox");
        var entry = store.Config.Entries.First();
        Window[] dialogs = [
            new EntryEditor(store.Config, entry),
            new MoveTasksWindow(store.Config.Groups, 1),
            new DeleteTasksWindow([entry], []),
            new ImportTasksWindow([new ImportCandidate("https://example.org/", entry.Copy(), "")], store.Config.Groups, store.Config.Groups.First()),
            new GroupsWindow(store),
            new SettingsWindow(store),
            new DiscoverWindow([], store.Config.Groups)
        ];
        string[] focusNames = ["任务入口", "目标分类", "取消", "待导入任务", "分类名称", "外观主题", "查找网页文件…"];
        try
        {
            for (int i = 0; i < dialogs.Length; i++)
            {
                var dialog = dialogs[i]; int index = i;
                search.Focus(); Flush();
                dialog.Owner = main;
                if (dialog.MinWidth > 0) dialog.Width = dialog.MinWidth;
                if (dialog.MinHeight > 0) dialog.Height = dialog.MinHeight;
                Interact(dialog, () =>
                {
                    Flush();
                    string focused = Keyboard.FocusedElement is DependencyObject element ? AutomationProperties.GetName(element) : "";
                    if (Keyboard.FocusedElement is Button button) focused = button.Content as string ?? focused;
                    if (index == 3 && Keyboard.FocusedElement is DataGrid) focused = "待导入任务";
                    check(focused == focusNames[index], dialog.Title + "初始键盘焦点明确");
                    check(KeyboardNavigation.GetTabNavigation(dialog) == KeyboardNavigationMode.Cycle, dialog.Title + "Tab 导航保持在弹窗内");
                    dialog.UpdateLayout();
                    if (dialog is ImportTasksWindow import)
                    {
                        var grid = Descendants<DataGrid>(dialog).Single();
                        StartComposition(grid);
                        Descendants<Button>(dialog).Single(b => Equals(b.Content, "添加所选")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(import.IsVisible && import.Imported.Count == 0, "批量导入在输入合成期间不提交未完成的名称");
                        CompleteComposition(grid);
                    }
                    foreach (string theme in new[] { "Light", "Dark" })
                    {
                        ThemeManager.Apply(theme); Flush();
                        check(ColorOf(dialog.Background) == ColorOf((Brush)Application.Current.FindResource("CanvasBrush")) &&
                              ColorOf(dialog.Foreground) == ColorOf((Brush)Application.Current.FindResource("TextBrush")), dialog.Title + theme + "主题表面与文字同步");
                        var cancel = Descendants<Button>(dialog).Single(b => b.IsCancel);
                        check(((Panel)cancel.Parent).Children.OfType<Button>().All(b => InViewport(b, dialog)), dialog.Title + theme + "最小窗口底部操作完整可见");
                        foreach(double scale in new[]{1.0,1.5,2.0}) Snapshot(dialog, root, $"dialog-{index}-{theme.ToLowerInvariant()}-{scale*100:0}.png",scale);
                    }
                    if (dialog is DeleteTasksWindow)
                    {
                        var cancel = Descendants<Button>(dialog).Single(b => b.IsCancel);
                        check(cancel.IsDefault && !Descendants<Button>(dialog).Any(b => !b.IsCancel && b.IsDefault), "删除任务默认取消且不默认执行删除");
                    }
                    CancelDialog(dialog);
                });
                Flush();
                check(search.IsKeyboardFocused, dialog.Title + "关闭后恢复主窗口原输入焦点");
            }

            var editor = new EntryEditor(store.Config, entry) { Owner = main };
            Interact(editor, () =>
            {
                var input = Field<TextBox>(editor, "path");
                StartComposition(input);
                check(InputComposition.IsActive(editor), "程序化输入合成开始后设置窗口合成状态");
                check(RaiseKey(input, Key.Return).Handled && RaiseKey(input, Key.Escape).Handled && editor.IsVisible, "程序化输入合成期间 Enter 与 Esc 不保存或关闭编辑器");
                check(!RaiseKey(input, Key.Left, bubble: false).Handled, "输入合成期间方向键继续交给输入控件");
                var imeKey = RaiseKey(input, Key.ImeProcessed, bubble: false);
                check(!imeKey.Handled && InputComposition.IsActive(editor, imeKey), "输入法处理键继续交给系统且被识别为合成输入");
                typeof(EntryEditor).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                check(editor.IsVisible && editor.DialogResult != true, "输入合成期间编辑器保存入口不提交任务");
                CompleteComposition(input);
                check(!InputComposition.IsActive(editor) && !RaiseKey(input, Key.Return).Handled, "程序化输入合成结束后释放 Enter 提交行为");
                CancelDialog(editor);
            });

            Flush();
            main.Width = 900; main.ApplyDetailsLayout(900); main.SetDetails("show"); main.UpdateLayout(); search.Focus();
            StartComposition(search);
            check(RaiseKey(search, Key.Escape).Handled && main.DetailsVisible, "主窗口输入合成期间 Esc 保留窄窗口详情面板");
            CompleteComposition(search);
            RaiseKey(search, Key.Escape);
            check(!main.DetailsVisible, "主窗口输入合成结束后 Esc 恢复关闭详情");
            main.SetDetails("show");
            var filter = Descendants<ComboBox>(main).Single(c => AutomationProperties.GetName(c) == "状态筛选");
            filter.Focus(); filter.IsDropDownOpen = true; Flush();
            check(filter.IsDropDownOpen && main.DetailsVisible, "窄窗口详情打开时状态筛选下拉可展开");
            var filterEscape = RaiseKey(filter, Key.Escape);
            check(!filter.IsDropDownOpen && main.DetailsVisible && filterEscape.Handled, "下拉框优先响应 Esc 并保留详情面板");

            CheckConfirmation(main, check);
            CheckSettings(main, store, check);
            CheckGroups(main, store, check);
            CheckDrafts(main,store,check);
            CheckStatus(check);
            check(JsonSerializer.Serialize(store.Config, DataStore.Json) == before, "弹窗取消、无效设置和输入合成检查保留完整任务配置");
        }
        finally
        {
            foreach (var dialog in dialogs) if (dialog.IsVisible) CancelDialog(dialog);
            ThemeManager.Apply("Light");
        }
    }

    private static void CancelDialog(Window window) {
        var cancel=Descendants<Button>(window).FirstOrDefault(b=>b.IsCancel);
        cancel?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.Close();
    }
    private static void CheckConfirmation(Window owner, Action<bool, string> check)
    {
        Exception? error = null;
        owner.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "测试配置替换确认");
            try
            {
                Flush();
                var cancel = Descendants<Button>(dialog).Single(b => b.IsCancel);
                check(cancel.IsDefault && cancel.IsKeyboardFocused, "业务确认弹窗默认取消并初始聚焦取消");
                check(!Descendants<Button>(dialog).Single(b => Equals(b.Content, "替换测试数据")).IsDefault, "业务确认的替换操作不响应默认 Enter");
            }
            catch (Exception ex) { error = ex; }
            finally { CancelDialog(dialog); }
        }, DispatcherPriority.ApplicationIdle);
        bool result = DialogExperience.Confirm(owner, "测试配置替换确认", "仅检查取消与焦点，不执行配置替换。", "替换测试数据");
        if (error != null) throw error;
        check(!result, "关闭业务确认弹窗返回取消");
    }

    private static void CheckSettings(Window owner, DataStore store, Action<bool, string> check)
    {
        int refreshBefore = store.Config.RefreshSeconds, waitBefore = store.Config.DefaultWaitSeconds;
        bool registrationBefore = StartupRegistration.Enabled;
        var dialog = new SettingsWindow(store) { Owner = owner };
        Interact(dialog, () =>
        {
            var refresh = DialogLayout.Descendants(dialog).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "状态刷新间隔（秒）");
            refresh.Text = "0";
            Descendants<Button>(dialog).Single(b => Equals(b.Content, "保存设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(refresh.IsKeyboardFocused && Descendants<TextBlock>(dialog).Any(t => t.IsVisible && t.Text == "状态刷新间隔必须填写正整数。"), "无效设置以内联错误显示并定位到输入字段");
            check(store.Config.RefreshSeconds == refreshBefore && store.Config.DefaultWaitSeconds == waitBefore && StartupRegistration.Enabled == registrationBefore, "无效设置保留配置与启动注册状态");
            CancelDialog(dialog);
        });
    }

    private static void CheckGroups(Window owner, DataStore store, Action<bool, string> check)
    {
        var dialog = new GroupsWindow(store) { Owner = owner };
        Interact(dialog, () =>
        {
            var input = Descendants<TextBox>(dialog).Single(t => AutomationProperties.GetName(t) == "分类名称");
            input.Text = "";
            Descendants<Button>(dialog).Single(b => Equals(b.Content, "新增")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(input.IsKeyboardFocused && Descendants<TextBlock>(dialog).Any(t => t.IsVisible && t.Text == "请输入分类名称。"), "空分类名称以内联错误显示并保持编辑焦点");
            CancelDialog(dialog);
        });
    }

    private static void CheckDrafts(Window owner,DataStore store,Action<bool,string> check)
    {
        var original=System.Text.Json.JsonSerializer.Serialize(store.Config,DataStore.Json);
        string originalTheme=ThemeManager.Preference;
        var settings=new SettingsWindow(store,_=>{},false) {Owner=owner};
        Interact(settings,()=>{
            var theme=DialogLayout.Descendants(settings).OfType<ComboBox>().Single(x=>AutomationProperties.GetName(x)=="外观主题");
            theme.SelectedIndex=2;
            check(ThemeManager.Preference=="Dark" && store.State.ThemePreference!="Dark","主题即时预览且未持久化");
            CancelDialog(settings);
        });
        check(ThemeManager.Preference==originalTheme,"取消设置恢复原主题");
        int registered=0;
        settings=new SettingsWindow(store,_=>registered++,false) {Owner=owner};
        Interact(settings,()=>{
            var refresh=DialogLayout.Descendants(settings).OfType<TextBox>().Single(x=>AutomationProperties.GetName(x)=="状态刷新间隔（秒）");refresh.Text="17";
            var login=DialogLayout.Descendants(settings).OfType<CheckBox>().Single();login.IsChecked=true;
            Descendants<Button>(settings).Single(x=>Equals(x.Content,"保存设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        check(registered==1 && new DataStore(store.Root).Config.RefreshSeconds==17,"保存设置写入真实隔离配置并调用注册替身");
        store.ReplaceConfig(System.Text.Json.JsonSerializer.Deserialize<HubConfig>(original,DataStore.Json)!);
        var groups=new GroupsWindow(store) {Owner=owner};
        Interact(groups,()=>{
            var list=Descendants<ListBox>(groups).Single(x=>AutomationProperties.GetName(x)=="已有分类");list.SelectedIndex=0;
            var input=Descendants<TextBox>(groups).Single();input.Text="重命名分类";
            check(store.Config.Groups[0]!="重命名分类","分类编辑不提前修改配置");
            Descendants<Button>(groups).Single(x=>Equals(x.Content,"保存分类")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        check(store.Config.Groups[0]=="重命名分类" && store.Config.Entries.First().Group=="重命名分类","保存分类同步更新任务归属");
        store.ReplaceConfig(System.Text.Json.JsonSerializer.Deserialize<HubConfig>(original,DataStore.Json)!);
        foreach(var choice in new[]{"继续编辑","放弃更改","保存"}) {
            var editor=new EntryEditor(store.Config,store.Config.Entries.First()) {Owner=owner};
            Interact(editor,()=>{
                DialogLayout.Descendants(editor).OfType<TextBox>().Single(x=>AutomationProperties.GetName(x)=="任务名称").Text="关闭提醒测试";
                editor.Dispatcher.BeginInvoke(()=>{
                    var prompt=Application.Current.Windows.OfType<Window>().Single(x=>x.Title=="未保存的更改");
                    Descendants<Button>(prompt).Single(x=>Equals(x.Content,choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(choice=="继续编辑") {prompt.Close();check(editor.IsVisible,"关闭提醒可继续编辑");CancelDialog(editor);}
                },DispatcherPriority.ApplicationIdle);
                editor.Close();
            });
            if(choice=="保存") check(editor.DialogResult==true && editor.Result.Name=="关闭提醒测试","关闭提醒支持保存草稿");
            if(choice=="放弃更改") check(editor.DialogResult!=true,"关闭提醒支持放弃更改");
        }
    }

    private static void CheckStatus(Action<bool, string> check)
    {
        var status = new TextBlock(); DialogExperience.Status(status, "失败", true);
        ThemeManager.Apply("Dark"); Flush();
        check(status.Visibility == Visibility.Visible && ColorOf(status.Foreground) == ColorOf((Brush)Application.Current.FindResource("DangerBrush")) && AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite, "错误反馈使用当前主题状态色及辅助技术通知");
        DialogExperience.Status(status, "完成");
        check(ColorOf(status.Foreground) == ColorOf((Brush)Application.Current.FindResource("SuccessBrush")), "成功反馈使用统一成功色");
        DialogExperience.Status(status, "");
        check(status.Visibility == Visibility.Collapsed, "空反馈不占据界面空间");
    }

    private static void Interact(Window dialog, Action action)
    {
        Exception? error = null; dialog.ShowInTaskbar = false;
        dialog.Dispatcher.BeginInvoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; CancelDialog(dialog); }
        }, DispatcherPriority.ApplicationIdle);
        dialog.ShowDialog();
        if (error != null) throw error;
    }

    private static void StartComposition(UIElement source) => Composition(source, TextCompositionManager.PreviewTextInputStartEvent);
    private static void CompleteComposition(UIElement source) => Composition(source, TextCompositionManager.PreviewTextInputEvent);
    private static void Composition(UIElement source, RoutedEvent routedEvent) => source.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, source, "中文")) { RoutedEvent = routedEvent });
    private static KeyEventArgs RaiseKey(UIElement source, System.Windows.Input.Key key, bool bubble = true)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args);
        if (bubble && !args.Handled) { args.RoutedEvent = Keyboard.KeyDownEvent; source.RaiseEvent(args); }
        return args;
    }
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
    private static bool InViewport(FrameworkElement element, Window window)
    {
        var content = (FrameworkElement)window.Content;
        var point = element.TranslatePoint(new Point(), content);
        return element.ActualWidth > 0 && point.X >= -1 && point.X + element.ActualWidth <= content.ActualWidth + 1 && point.Y >= -1 && point.Y + element.ActualHeight <= content.ActualHeight + 1;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Flush() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Snapshot(Window window, string root, string name,double scale=1)
    {
        var content = (FrameworkElement)window.Content;
        UiMotion.Finish(content); content.UpdateLayout();
        int width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right), height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width*scale), (int)Math.Ceiling(height*scale), 96*scale, 96*scale, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, name)); encoder.Save(stream);
    }
}
