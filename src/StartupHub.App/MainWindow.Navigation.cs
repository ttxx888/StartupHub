using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StartupHub.App;

public partial class MainWindow
{
    public static readonly DependencyProperty SidebarCollapsedProperty = DependencyProperty.Register(
        nameof(SidebarCollapsed), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool SidebarCollapsed { get => (bool)GetValue(SidebarCollapsedProperty); private set => SetValue(SidebarCollapsedProperty, value); }
    private readonly Dictionary<ItemsControl, Dictionary<object, double>> pendingMoves = new();
    private string navigationGroup = "";

    private void InitializeNavigation(bool collapsed)
    {
        SidebarCollapsed = collapsed; ApplySidebarLayout(false);
        navigationGroup = vm.SelectedGroup;
        vm.PropertyChanged += OnNavigationChanged;
        vm.Rows.CollectionChanged += OnTaskPlacementChanged;
        vm.CategoryItems.CollectionChanged += OnCategoryPlacementChanged;
        PreviewKeyDown += OnNavigationKeyDown;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= OnNavigationChanged;
            vm.Rows.CollectionChanged -= OnTaskPlacementChanged;
            vm.CategoryItems.CollectionChanged -= OnCategoryPlacementChanged;
        };
    }

    public void SetSidebar(string action)
    {
        bool collapsed = action switch { "show" => false, "hide" => true, "toggle" => !SidebarCollapsed, _ => throw new ArgumentException("侧栏命令应为 show、hide 或 toggle。") };
        if (collapsed == SidebarCollapsed) return;
        SidebarCollapsed = collapsed;
        ApplySidebarLayout(IsLoaded && !snapshotLayout);
        if (!snapshotLayout) { App.Store.State.SidebarCollapsed = collapsed; App.Store.SaveState(); }
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) => SetSidebar("toggle");
    private void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) { SetSidebar("toggle"); e.Handled = true; }
    }

    private void ApplySidebarLayout(bool animate)
    {
        double width = SidebarCollapsed ? 72 : 180;
        if (animate) UiMotion.AnimateColumn(SidebarColumn, SidebarColumn.ActualWidth, width);
        else { SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null); SidebarColumn.Width = new GridLength(width); }
        SidebarToggle.Content = SidebarCollapsed ? "\uE76C" : "\uE76B";
        Grid.SetColumn(SidebarToggle, SidebarCollapsed ? 0 : 1);
        SidebarToggle.HorizontalAlignment = HorizontalAlignment.Center;
        SidebarToggle.ToolTip = SidebarCollapsed ? "展开侧栏（Ctrl+B）" : "收起侧栏（Ctrl+B）";
        AutomationProperties.SetName(SidebarToggle, SidebarCollapsed ? "展开侧栏" : "收起侧栏");
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedGroup) || navigationGroup == vm.SelectedGroup) return;
        navigationGroup = vm.SelectedGroup;
        if (IsLoaded && !snapshotLayout) UiMotion.Reveal(ListPanel);
    }

    private void OnTaskPlacementChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move) QueuePlacementMotion(TaskList);
    }
    private void OnCategoryPlacementChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move) QueuePlacementMotion(CategoryList);
    }

    private void QueuePlacementMotion(ItemsControl control)
    {
        if (!IsLoaded || snapshotLayout || !UiMotion.Enabled || pendingMoves.ContainsKey(control)) return;
        var positions = RealizedContainers(control).Where(x => ContainerKey(x) != null).ToDictionary(x => ContainerKey(x)!, x => x.TranslatePoint(new Point(), control).Y);
        pendingMoves[control] = positions;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            pendingMoves.Remove(control);
            if (!IsLoaded || snapshotLayout || !UiMotion.Enabled) return;
            control.UpdateLayout();
            foreach (var item in RealizedContainers(control))
            {
                object? key = ContainerKey(item);
                double y = item.TranslatePoint(new Point(), control).Y;
                if (key != null && y < control.ActualHeight && y + item.ActualHeight > 0 && positions.TryGetValue(key, out double before))
                    UiMotion.MoveToRest(item, before - y);
            }
        }), DispatcherPriority.Loaded);
    }

    private static object? ContainerKey(FrameworkElement element) => element.DataContext switch { TaskRow task => task.Entry.Id, CategoryItem category => category.Name, _ => null };
    private static IEnumerable<FrameworkElement> RealizedContainers(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DataGridRow or ListBoxItem) yield return (FrameworkElement)child;
            else foreach (var item in RealizedContainers(child)) yield return item;
        }
    }

    private void FinishNavigationMotion()
    {
        ApplySidebarLayout(false);
        UiMotion.Finish(SidebarPanel); UiMotion.Finish(ListPanel); UiMotion.Finish(DetailPanel);
        foreach (var item in RealizedContainers(TaskList).Concat(RealizedContainers(CategoryList))) UiMotion.Finish(item);
    }
}
