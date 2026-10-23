using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Windows.Threading;
using StartupHub.Core;

namespace StartupHub.App;

public static class UiCommands
{
    public static async Task<CommandResponse> ExecuteAsync(MainWindow window, MainViewModel vm, CommandApi api, string[] args)
    {
        string verb = args.ElementAtOrDefault(1) ?? "state";
        string Required(int i) => args.ElementAtOrDefault(i) ?? throw new ArgumentException("缺少界面命令参数。");
        switch (verb)
        {
            case "show": window.ShowFromTray(); break;
            case "hide": window.Hide(); break;
            case "exit": return new(true, "管理器已退出，服务继续运行。");
            case "select":
                var e = api.FindEntry(Required(2)); var row = vm.Rows.First(x => x.Entry.Id == e.Id);
                if (!vm.TasksView.Contains(row)) { vm.Search = ""; vm.StatusFilter = "全部状态"; vm.SelectedGroup = "全部任务"; }
                vm.Selected = row; break;
            case "search": vm.Search = Required(2); break;
            case "details": window.SetDetails(Required(2)); break;
            case "sidebar": window.SetSidebar(Required(2)); break;
            case "theme": ThemeManager.SetPreference(Required(2)); break;
            case "snapshot": return await SnapshotAsync(window, vm, api, args);
            case "state": break;
            default: throw new ArgumentException("未知界面命令。");
        }
        return new(true, new { Pid = Environment.ProcessId, Visible = window.IsVisible, window.Width, window.Height, window.DetailsVisible, window.DetailsOverlay, window.SidebarCollapsed, ThemePreference = ThemeManager.Preference, ThemeManager.EffectiveTheme, vm.Search, vm.SelectedGroup, Selected = vm.Selected?.Entry.Id, vm.Summary, vm.Message, VisibleTasks = vm.TasksView.Cast<TaskRow>().Select(x => new { x.Entry.Id, x.Entry.Name, x.Status.Text }).ToArray() });
    }
    private static async Task<CommandResponse> SnapshotAsync(MainWindow window, MainViewModel vm, CommandApi api, string[] args)
    {
        string output = Path.GetFullPath(CommandApi.Option(args, "--output") ?? throw new ArgumentException("需要 --output 路径。"));
        double width = double.Parse(CommandApi.Option(args, "--width") ?? "1240", System.Globalization.CultureInfo.InvariantCulture);
        double height = double.Parse(CommandApi.Option(args, "--height") ?? "780", System.Globalization.CultureInfo.InvariantCulture);
        double scale = double.Parse(CommandApi.Option(args, "--scale") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        if (width < 320 || height < 400 || width > 3840 || height > 2160 || scale < 0.5 || scale > 3) throw new ArgumentException("预览尺寸或缩放超出范围。");
        string view = CommandApi.Option(args, "--view") ?? "main";
        string previousTheme = ThemeManager.Preference;
        string? theme = CommandApi.Option(args, "--theme");
        Window? target = null;
        FrameworkElement? root = null;
        Size previous = default;
        double previousWidth = double.NaN, previousHeight = double.NaN;
        bool snapshotLayout = false;
        try
        {
            if (theme != null) ThemeManager.Apply(theme);
            target = view switch
            {
                "main" => window,
                "editor" => new EntryEditor(App.Store.Config, CommandApi.Option(args, "--task") is { } id ? api.FindEntry(id) : vm.Selected?.Entry),
                "settings" => new SettingsWindow(App.Store),
                "groups" => new GroupsWindow(App.Store),
                "discover" => new DiscoverWindow([], App.Store.Config.Groups),
                _ => throw new ArgumentException("预览视图应为 main、editor、settings、groups 或 discover。")
            };
            root = (FrameworkElement)target.Content;
            previous = root.RenderSize;
            previousWidth = root.Width; previousHeight = root.Height;
            root.Width = width - root.Margin.Left - root.Margin.Right;
            root.Height = height - root.Margin.Top - root.Margin.Bottom;
            if (previous.Width <= 0 || previous.Height <= 0) previous = new Size(target.Width, target.Height);
            if (target == window)
            {
                window.BeginSnapshot(width); snapshotLayout = true;
                if (CommandApi.Option(args, "--details") is { } details) window.SetDetails(details);
                if (CommandApi.Option(args, "--sidebar") is { } sidebar) window.SetSidebar(sidebar);
            }
            target.ApplyTemplate();
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            root.Arrange(new Rect(0, 0, width, height));
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle((Brush)Application.Current.FindResource("CanvasBrush"), null, new Rect(0, 0, width, height));
                var brush = new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill };
                drawing.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var file = File.Create(output)) encoder.Save(file);
            return new(true, new { Output = output, View = view, Theme = ThemeManager.EffectiveTheme, window.SidebarCollapsed, Width = width, Height = height, RenderWidth = root.ActualWidth + root.Margin.Left + root.Margin.Right, RenderHeight = root.ActualHeight + root.Margin.Top + root.Margin.Bottom, Scale = scale, PixelWidth = bitmap.PixelWidth, PixelHeight = bitmap.PixelHeight });
        }
        finally
        {
            if (root != null) { root.Width = previousWidth; root.Height = previousHeight; }
            if (snapshotLayout)
            {
                if (root != null)
                {
                    if (window.IsLoaded)
                    { root.InvalidateMeasure(); root.InvalidateArrange(); window.InvalidateMeasure(); window.UpdateLayout(); }
                    else if (previous.Width > 0 && previous.Height > 0)
                    { root.Measure(previous); root.Arrange(new Rect(previous)); root.UpdateLayout(); }
                }
                window.EndSnapshot();
            }
            if (target != null && target != window) target.Close();
            if (theme != null) ThemeManager.Apply(previousTheme);
        }
    }
}
