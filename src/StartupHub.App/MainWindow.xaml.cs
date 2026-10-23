using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using StartupHub.Core;
using Forms = System.Windows.Forms;

namespace StartupHub.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    private readonly DispatcherTimer timer;
    private Forms.NotifyIcon? tray;
    private bool exiting;
    private readonly CommandApi api;
    private bool layoutReady, snapshotLayout, narrowDetails, wasNarrow;
    private double layoutWidth = 1240;
    private double savedLayoutWidth;
    private bool savedNarrowDetails, savedWideDetails, savedSidebarCollapsed, detailsShown;
    private System.Drawing.Icon? brandIcon;
    private bool synchronizingSelection;
    public bool DetailsVisible => detailsShown;
    public bool DetailsOverlay => layoutWidth < 1120 && DetailsVisible;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); vm = viewModel; DataContext = vm;
        InputComposition.Attach(this);
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && StatusFilterBox.IsDropDownOpen && !InputComposition.IsActive(this, args))
            { StatusFilterBox.IsDropDownOpen = false; args.Handled = true; }
        };
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(MainViewModel.SelectedRows)) SyncTaskSelection(); };
        SyncTaskSelection();
        layoutReady = true;
        api = new CommandApi(App.Store, App.Engine);
        api.ConfigurationChanged += () => vm.Reload();
        var state = App.Store.State;
        InitializeNavigation(state.SidebarCollapsed);
        var area = SystemParameters.WorkArea;
        Width = Math.Min(state.Width, area.Width); Height = Math.Min(state.Height, area.Height);
        MinWidth = Math.Min(900, area.Width); MinHeight = Math.Min(500, area.Height);
        Left = state.Left.HasValue ? Math.Clamp(state.Left.Value, area.Left, Math.Max(area.Left, area.Right - Width)) : area.Left + (area.Width - Width) / 2;
        Top = state.Top.HasValue ? Math.Clamp(state.Top.Value, area.Top, Math.Max(area.Top, area.Bottom - Height)) : area.Top + (area.Height - Height) / 2;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(App.Store.Config.RefreshSeconds) };
        timer.Tick += async (_, _) => { timer.Interval = TimeSpan.FromSeconds(App.Store.Config.RefreshSeconds); try { await vm.RefreshAsync(); } catch (Exception ex) { vm.Message = "刷新失败：" + ex.Message; } };
        timer.Start();
        Loaded += async (_, _) => { InitializeTray(); try { await vm.RefreshAsync(); } catch (Exception ex) { vm.Message = ex.Message; } };
        PreviewKeyDown += OnWindowShortcut;
        Closing += OnClosing;
        ApplyDetailsLayout(Width);
        KeyDown += (_, args) => { if (!InputComposition.IsActive(this, args) && args.Key == Key.Escape && DetailsOverlay) { SetDetails("hide"); args.Handled = true; } };
    }
    private void OnWindowShortcut(object sender, KeyEventArgs args)
    {
        if (InputComposition.IsActive(this, args)) return;
        if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { SearchBox.Focus(); SearchBox.SelectAll(); args.Handled = true; return; }
        ICommand? command = args.Key switch
        {
            Key.N when Keyboard.Modifiers == ModifierKeys.Control => vm.AddCommand,
            Key.E when Keyboard.Modifiers == ModifierKeys.Control => vm.EditCommand,
            Key.F5 when Keyboard.Modifiers == ModifierKeys.None => vm.RefreshCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return;
        args.Handled = true; command.Execute(null);
    }
    public void InitializeTray()
    {
        if (tray != null) return;
        try { brandIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "assets", "startuphub.ico"), 24, 24); }
        catch { brandIcon = System.Drawing.SystemIcons.Application; }
        tray = new Forms.NotifyIcon { Icon = brandIcon, Text = "启动台", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开启动台", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("启动自启项", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await App.Engine.StartBatchAsync()).Task.Unwrap());
        menu.Items.Add("退出（服务继续运行）", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }
    public void ShowFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveGeometry();
        if (!exiting) { e.Cancel = true; InitializeTray(); Hide(); }
    }
    private void SaveGeometry()
    {
        if (WindowState != WindowState.Normal) return;
        App.Store.State.Width = Width; App.Store.State.Height = Height; App.Store.State.Left = Left; App.Store.State.Top = Top;
        App.Store.SaveState();
    }
    public void ExitApplication()
    {
        exiting = true; vm.FlushPendingStateSave(); SaveGeometry(); timer.Stop(); tray?.ContextMenuStrip?.Dispose(); tray?.Dispose(); brandIcon?.Dispose(); Close(); Application.Current.Shutdown();
    }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Environment.TickCount64 - lastDragFinished < 400) return;
        var source = e.OriginalSource as DependencyObject;
        bool row = false;
        while (source != null)
        {
            if (source is CheckBox or Button) return;
            if (source is System.Windows.Controls.DataGridRow) { row = true; break; }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        if (!row) return;
        if (vm.StartCommand.CanExecute(null)) vm.StartCommand.Execute(null);
    }
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) { if (layoutReady && !snapshotLayout) ApplyDetailsLayout(e.NewSize.Width); }
    public void ApplyDetailsLayout(double width)
    {
        layoutWidth = width;
        bool narrow = width < 1120;
        if (narrow && !wasNarrow) narrowDetails = false;
        wasNarrow = narrow;
        bool show = narrow ? narrowDetails : App.Store.State.DetailsVisible;
        bool changed = detailsShown != show;
        bool animate = changed && IsLoaded && !snapshotLayout && UiMotion.Enabled;
        detailsShown = show;
        double columnWidth = !narrow && show ? 336 : 0;
        if (animate && !narrow) UiMotion.AnimateColumn(DetailColumn, DetailColumn.ActualWidth, columnWidth);
        else { DetailColumn.BeginAnimation(ColumnDefinition.WidthProperty, null); DetailColumn.Width = new GridLength(columnWidth); }
        Grid.SetColumn(DetailPanel, narrow ? 0 : 1);
        DetailPanel.IsHitTestVisible = show;
        if (show)
        {
            DetailPanel.Visibility = Visibility.Visible;
            if (animate) UiMotion.Reveal(DetailPanel); else UiMotion.Finish(DetailPanel);
        }
        else if (animate) UiMotion.Conceal(DetailPanel, () => { if (!detailsShown) DetailPanel.Visibility = Visibility.Collapsed; });
        else { UiMotion.Finish(DetailPanel); DetailPanel.Visibility = Visibility.Collapsed; }
        DetailPanel.Margin = new Thickness(0, narrow ? -150 : 0, 0, 0);
        DetailsButton.Content = show ? "收起详情" : "显示详情";
    }
    public void SetDetails(string action)
    {
        bool show = action switch { "show" => true, "hide" => false, "toggle" => !DetailsVisible, _ => throw new ArgumentException("详情命令应为 show、hide 或 toggle。") };
        if (layoutWidth < 1120) narrowDetails = show;
        else { App.Store.State.DetailsVisible = show; if (!snapshotLayout) App.Store.SaveState(); }
        ApplyDetailsLayout(layoutWidth);
    }
    public void BeginSnapshot(double width)
    {
        savedLayoutWidth = layoutWidth; savedNarrowDetails = narrowDetails; savedWideDetails = App.Store.State.DetailsVisible; savedSidebarCollapsed = SidebarCollapsed;
        snapshotLayout = true; FinishNavigationMotion(); ApplyDetailsLayout(width);
    }
    public void EndSnapshot()
    {
        App.Store.State.DetailsVisible = savedWideDetails; wasNarrow = savedLayoutWidth < 1120; narrowDetails = savedNarrowDetails;
        SidebarCollapsed = savedSidebarCollapsed; ApplySidebarLayout(false); ApplyDetailsLayout(savedLayoutWidth); snapshotLayout = false;
    }
    private void OnToggleDetails(object sender, RoutedEventArgs e) => SetDetails("toggle");
    private void OnOpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button) { menu.DataContext = vm; menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; }
    }
    private async void OnRowStart(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskRow row }) return;
        e.Handled = true;
        try { await vm.StartRowAsync(row); }
        catch (Exception ex) { vm.Message = "启动失败：" + ex.Message; }
    }
    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (vm == null || synchronizingSelection || e.Source != TaskList) return;
        vm.SetSelection(TaskList.SelectedItems.Cast<TaskRow>().ToArray());
    }
    private void SyncTaskSelection()
    {
        var rows = vm.SelectedRows.ToArray();
        if (TaskList.SelectedItems.Cast<TaskRow>().Select(x => x.Entry.Id).ToHashSet().SetEquals(rows.Select(x => x.Entry.Id))) return;
        synchronizingSelection = true;
        try
        {
            TaskList.UnselectAll();
            foreach (var row in rows) TaskList.SelectedItems.Add(row);
        }
        finally { synchronizingSelection = false; }
    }
    public async Task<StartupHub.Core.CommandResponse> ExecuteCommandAsync(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "ui") return await UiCommands.ExecuteAsync(this, vm, api, args);
            if (args.Length > 0 && args[0] == "startup")
            {
                if (args.Length < 2) throw new ArgumentException("需要 status、enable 或 disable。");
                if (args[1] == "enable") StartupRegistration.Set(true);
                else if (args[1] == "disable") StartupRegistration.Set(false);
                else if (args[1] != "status") throw new ArgumentException("未知启动入口命令。");
                return new(true, StartupRegistration.Status());
            }
            return await api.ExecuteAsync(args);
        }
        catch (Exception ex) { return new(false, null, ex.Message); }
    }
}
