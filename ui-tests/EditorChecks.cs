using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class EditorChecks
{
    private static T Field<T>(EntryEditor editor,string name) => (T)typeof(EntryEditor).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(editor)!;
    private static void Save(EntryEditor editor) => typeof(EntryEditor).GetMethod("Save",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,null);
    private static void Interact(EntryEditor editor,Action<EntryEditor> action)
    {
        Exception? error=null;
        editor.ShowActivated=false; editor.ShowInTaskbar=false;
        editor.Dispatcher.BeginInvoke(() => { try { action(editor); } catch(Exception ex) { error=ex; editor.Close(); } },DispatcherPriority.ApplicationIdle);
        editor.ShowDialog(); if(error!=null) throw error;
    }
    internal static void Run(MainWindow window,MainViewModel vm,DataStore store,Action<bool,string> check,string root)
    {
        var config=new HubConfig();
        var editor=new EntryEditor(config,null,"开发网页");
        check(Field<ComboBox>(editor,"group").Text=="开发网页" && Field<CheckBox>(editor,"auto").IsChecked==false && !Field<Expander>(editor,"advanced").IsExpanded, "普通添加默认当前分类、关闭自启并折叠更多设置");
        Field<TextBox>(editor,"path").Text="https://example.com/tool";
        check(Field<TextBox>(editor,"name").Text=="example.com" && Field<StackPanel>(editor,"servicePanel").Visibility==Visibility.Collapsed && Field<StackPanel>(editor,"launchPanel").Visibility==Visibility.Collapsed, "粘贴网址自动填写名称并隐藏程序和运行检查字段");
        Field<TextBox>(editor,"port").Text="not-a-number"; Field<TextBox>(editor,"codes").Text="invalid";
        Interact(editor,e => { Snapshot(e,root,"smart-add-url.png"); Save(e); });
        check(editor.DialogResult==true && editor.Result.Kind==TaskKind.Url && editor.Result.Url=="https://example.com/tool" && editor.Result.Port==0, "网址最少填写即可保存，不读取无关数值字段");

        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="C:\\工具\\程序.exe";
        check(Field<TextBox>(editor,"name").Text=="程序" && Field<StackPanel>(editor,"servicePanel").Visibility==Visibility.Collapsed, "普通程序自动识别且默认不展示就绪检查");
        Field<TextBox>(editor,"name").Text="自定义名称"; Field<TextBox>(editor,"path").Text="C:\\工具\\另一个程序.exe";
        check(Field<TextBox>(editor,"name").Text=="自定义名称", "修改入口不覆盖用户自定义名称");
        Interact(editor,Save);
        check(editor.Result.Kind==TaskKind.Desktop && editor.Result.Name=="自定义名称" && editor.Result.Arguments.Count==0, "普通程序以简化表单保存");

        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="C:\\run.py"; Interact(editor,Save);
        check(editor.Result.Kind==TaskKind.Script && editor.Result.Path=="C:\\run.py", "脚本入口自动识别并保存");
        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="C:\\小游戏\\index.html";
        Interact(editor,e => { e.Width=520; e.Height=500; e.Dispatcher.Invoke(() => {},DispatcherPriority.ContextIdle); Snapshot(e,root,"smart-add-narrow.png"); Save(e); });
        check(editor.Result.Kind==TaskKind.File && editor.Result.Name=="index", "本地 HTML 按文件保存且窄窗口操作可见");

        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="C:\\程序.exe";
        Field<ComboBox>(editor,"kind").SelectedValue=TaskKind.Service;
        check(Field<StackPanel>(editor,"servicePanel").Visibility==Visibility.Visible, "手动选择服务展示运行检查");
        Field<TextBox>(editor,"port").Text="invalid"; Field<ComboBox>(editor,"kind").SelectedValue=TaskKind.Url; Field<TextBox>(editor,"path").Text="https://example.org/";
        Interact(editor,Save);
        check(editor.DialogResult==true && editor.Result.Kind==TaskKind.Url && editor.Result.Url=="https://example.org/", "切换任务类型后忽略无关字段输入");

        var parent=new TaskEntry { Name="前置服务",Path=Environment.ProcessPath! }; config.Entries.Add(parent);
        var existing=new TaskEntry { Name="原有服务",Kind=TaskKind.Service,Path=Environment.ProcessPath!,Script="C:\\main.py",WorkingDir="C:\\工作目录",Arguments=["--name","中文 参数",""],Environment=new() { ["CASE"]="value" },Url="http://127.0.0.1:54444/",Port=54444,CheckUrl="http://127.0.0.1:54444/health",HealthCodes=[200,403],WaitSeconds=42,DependsOn=parent.Id,StartDelaySeconds=12,ManagedPidFile="C:\\run.pid",ProcessMatch="fixture",ExternalLogs=["C:\\sample.log"],VisibleConsole=true,SuppressOutput=true,OpenPageOnAutoStart=true,Info="原始说明",AutoStart=true };
        config.Entries.Add(existing); string before=JsonSerializer.Serialize(existing,DataStore.Json);
        editor=new EntryEditor(config,existing); Interact(editor,Save);
        check(JsonSerializer.Serialize(editor.Result,DataStore.Json)==before, "编辑服务完整保留参数、环境变量、PID、日志、就绪、依赖和自启配置");
        var desktop=existing.Copy(); desktop.Id=Guid.NewGuid(); desktop.Name="桌面程序"; desktop.Kind=TaskKind.Desktop; desktop.OpensOwnBrowser=true; config.Entries.Add(desktop);
        editor=new EntryEditor(config,desktop); Interact(editor,Save);
        check(JsonSerializer.Serialize(editor.Result,DataStore.Json)==JsonSerializer.Serialize(desktop,DataStore.Json), "编辑桌面程序保留自行打开浏览器及未启用的运行检查配置");
        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="http://127.0.0.1/login"; Field<ComboBox>(editor,"kind").SelectedValue=TaskKind.Network;
        Interact(editor,Save);
        check(editor.Result.Kind==TaskKind.Network && editor.Result.Url=="http://127.0.0.1/login" && editor.Result.ProxyUrl.Length>0, "新建网络检查保留认证入口与默认检查设置");
        var file=existing.Copy(); file.Id=Guid.NewGuid(); file.Name="旧文件"; file.Kind=TaskKind.File; config.Entries.Add(file);
        editor=new EntryEditor(config,file); Field<TextBox>(editor,"port").Text="invalid"; Field<TextBox>(editor,"proxy").Text="invalid";
        Interact(editor,Save);
        check(JsonSerializer.Serialize(editor.Result,DataStore.Json)==JsonSerializer.Serialize(file,DataStore.Json), "编辑文件保留原有隐藏配置且不读取无关输入");

        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="https://"; Field<TextBox>(editor,"name").Text="无效网址"; Save(editor);
        check(Field<TextBlock>(editor,"validation").Visibility==Visibility.Visible && Field<TextBox>(editor,"path").Text=="https://", "错误以内联文字展示并保留输入"); editor.Close();
        editor=new EntryEditor(config,null); Field<TextBox>(editor,"path").Text="C:\\run.ps1"; string configBefore=JsonSerializer.Serialize(config,DataStore.Json);
        Interact(editor,e => e.Close());
        check(editor.DialogResult!=true && JsonSerializer.Serialize(config,DataStore.Json)==configBefore, "取消添加不修改配置");

        editor=new EntryEditor(config,null); var data=new DataObject(DataFormats.FileDrop,new[] {"C:\\拖入文件.html"});
        var drop=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Copy,Field<TextBox>(editor,"path"),new Point(1,1)],null)!;
        drop.RoutedEvent=DragDrop.PreviewDropEvent; Field<TextBox>(editor,"path").RaiseEvent(drop);
        check(Field<TextBox>(editor,"path").Text=="C:\\拖入文件.html" && Field<TextBox>(editor,"name").Text=="拖入文件", "入口接受单个文件拖入并自动识别名称"); editor.Close();

        window.ShowActivated=false; window.ShowInTaskbar=false; window.Show(); window.UpdateLayout();
        vm.SelectedGroup="开发网页";
        Exception? addError=null;
        window.Dispatcher.BeginInvoke(() =>
        {
            var add=Application.Current.Windows.OfType<EntryEditor>().Single();
            try { Field<TextBox>(add,"path").Text="https://example.net/"; Save(add); } catch(Exception ex) { addError=ex; add.Close(); }
        },DispatcherPriority.ApplicationIdle);
        vm.AddCommand.Execute(null); if(addError!=null) throw addError;
        check(store.Config.Entries.Any(x => x.Name=="example.net" && x.Group=="开发网页" && !x.AutoStart), "主界面添加任务继承当前分类并保持默认不自启");
    }
    private static void Snapshot(Window window,string root,string name)
    {
        window.UpdateLayout(); var content=(FrameworkElement)window.Content;
        int width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        var visual=new DrawingVisual(); using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.FindResource("CanvasBrush"),null,new Rect(0,0,width,height));
            drawing.DrawRectangle(new VisualBrush(content) {ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,width,height),Stretch=Stretch.Fill},null,new Rect(0,0,width,height));
        }
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream=File.Create(Path.Combine(root,name)); encoder.Save(stream);
    }
}
