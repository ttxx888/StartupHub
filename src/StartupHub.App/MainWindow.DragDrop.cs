using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace StartupHub.App;

public partial class MainWindow
{
    internal const string TaskDragFormat = "StartupHub.TaskId";
    internal const string TasksDragFormat = "StartupHub.TaskIds";
    internal const string CategoryDragFormat = "StartupHub.Category";
    private Point dragOrigin, lastDragPoint;
    private TaskRow? pendingDrag;
    private TaskRow? deferredSelection;
    private DataGridCell? deferredCell;
    private Guid? activeDrag;
    private string? pendingCategory, activeCategory;
    private FrameworkElement? dragScrollHost;
    private Point categoryDragOrigin;
    private long lastDragFinished;
    private DropMarker? marker;
    private AdornerLayer? markerLayer;
    private DispatcherTimer? dragScrollTimer;
    private int scrollDirection;

    private static T? Ancestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T result) return result;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
    private void OnTaskMouseDown(object sender, MouseButtonEventArgs e)
    {
        pendingDrag = null; deferredSelection = null; deferredCell = null;
        if (e.ClickCount != 1 || !vm.CanArrange) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var source = e.OriginalSource as DependencyObject;
        if (Ancestor<ButtonBase>(source) != null || Ancestor<ScrollBar>(source) != null) return;
        if (Ancestor<DataGridRow>(source)?.Item is not TaskRow row) return;
        pendingDrag = row; dragOrigin = e.GetPosition(TaskList);
        if (vm.HasMultipleSelection && TaskList.SelectedItems.Contains(row))
        {
            deferredSelection = row; deferredCell = Ancestor<DataGridCell>(source); e.Handled = true;
        }
    }
    private void OnTaskMouseUp(object sender, MouseButtonEventArgs e)
    {
        var row = deferredSelection;
        var cell = deferredCell;
        pendingDrag = null; deferredSelection = null; deferredCell = null;
        if (row == null) return;
        if (cell == null) { vm.Selected = row; return; }
        // Complete a deferred click through WPF so the next Shift selection uses this row as its anchor.
        cell.Focus();
        cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, e.Timestamp, MouseButton.Left)
        { RoutedEvent = MouseLeftButtonDownEvent });
    }
    private void OnTaskMouseMove(object sender, MouseEventArgs e)
    {
        if (pendingDrag == null || e.LeftButton != MouseButtonState.Pressed || !vm.CanArrange) return;
        var point = e.GetPosition(TaskList);
        if (Math.Abs(point.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = pendingDrag; pendingDrag = null;
        deferredSelection = null; deferredCell = null;
        if (!TaskList.SelectedItems.Contains(row)) vm.Selected = row;
        var ids = vm.SelectedRows.Select(x => x.Entry.Id).ToArray();
        activeDrag = ids.Length == 1 ? ids[0] : null;
        var data = ids.Length == 1 ? new DataObject(TaskDragFormat, ids[0].ToString()) : new DataObject(TasksDragFormat, ids.Select(x => x.ToString()).ToArray());
        Mouse.Capture(null);
        try { DragDrop.DoDragDrop(TaskList, data, DragDropEffects.Move); }
        finally { activeDrag = null; lastDragFinished = Environment.TickCount64; ClearDragFeedback(); }
        e.Handled = true;
    }
    private bool ReadDraggedTask(DragEventArgs e, out Guid id)
    {
        id = Guid.Empty;
        if (!ReadDraggedTasks(e, out var ids) || ids.Length != 1) return false;
        id = ids[0]; return true;
    }
    private bool ReadDraggedTasks(DragEventArgs e, out Guid[] ids)
    {
        ids = [];
        if (!vm.CanArrange) return false;
        var values = e.Data.GetDataPresent(TasksDragFormat, false) ? e.Data.GetData(TasksDragFormat, false) as string[] :
            e.Data.GetDataPresent(TaskDragFormat, false) ? new[] { e.Data.GetData(TaskDragFormat, false) as string ?? "" } : [];
        if (values == null || values.Length == 0) return false;
        var parsed = new System.Collections.Generic.List<Guid>();
        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var id) || !vm.Rows.Any(x => x.Entry.Id == id)) return false;
            parsed.Add(id);
        }
        ids = parsed.Distinct().ToArray(); return true;
    }
    private void OnCategoryMouseDown(object sender, MouseButtonEventArgs e)
    {
        pendingCategory = null;
        if (e.ClickCount != 1 || !vm.CanArrange || Ancestor<ScrollBar>(e.OriginalSource as DependencyObject) != null) return;
        if (Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not CategoryItem category || category.Name == "全部任务") return;
        pendingCategory = category.Name; categoryDragOrigin = e.GetPosition(CategoryList);
    }
    private void OnCategoryMouseUp(object sender, MouseButtonEventArgs e) => pendingCategory = null;
    private void OnCategoryMouseMove(object sender, MouseEventArgs e)
    {
        if (pendingCategory == null || e.LeftButton != MouseButtonState.Pressed || !vm.CanArrange) return;
        var point = e.GetPosition(CategoryList);
        if (Math.Abs(point.X - categoryDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - categoryDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        string name = pendingCategory; pendingCategory = null; activeCategory = name;
        Mouse.Capture(null);
        try { DragDrop.DoDragDrop(CategoryList, new DataObject(CategoryDragFormat, name), DragDropEffects.Move); }
        finally { activeCategory = null; ClearDragFeedback(); }
        e.Handled = true;
    }
    private bool ReadDraggedCategory(DragEventArgs e, out string name)
    {
        name = e.Data.GetDataPresent(CategoryDragFormat, false) ? e.Data.GetData(CategoryDragFormat, false) as string ?? "" : "";
        return vm.CanArrange && name != "全部任务" && vm.Groups.Contains(name);
    }
    private (CategoryItem Category, ListBoxItem Container, bool After)? CategoryDropTarget(Point point, DependencyObject? source = null)
    {
        var item = Ancestor<ListBoxItem>(source ?? CategoryList.InputHitTest(point) as DependencyObject);
        if (item?.DataContext is CategoryItem category)
        {
            if (category.Name == "全部任务") return null;
            return (category, item, CategoryList.TranslatePoint(point, item).Y >= item.ActualHeight / 2);
        }
        if (vm.CategoryItems.LastOrDefault() is not { } last || last.Name == "全部任务" || CategoryList.ItemContainerGenerator.ContainerFromItem(last) is not ListBoxItem container) return null;
        return point.Y >= container.TranslatePoint(new Point(0, container.ActualHeight), CategoryList).Y ? (last, container, true) : null;
    }
    private (TaskRow Row, DataGridRow Container, bool After)? ListDropTarget(Point point)
    {
        var hit = TaskList.InputHitTest(point) as DependencyObject;
        var row = Ancestor<DataGridRow>(hit);
        if (row?.Item is TaskRow task)
        {
            var local = TaskList.TranslatePoint(point, row);
            return (task, row, local.Y >= row.ActualHeight / 2);
        }
        if (Ancestor<ScrollBar>(hit) != null) return null;
        var last = vm.TasksView.Cast<TaskRow>().LastOrDefault();
        if (last == null || TaskList.ItemContainerGenerator.ContainerFromItem(last) is not DataGridRow container) return null;
        if (point.Y >= container.TranslatePoint(new Point(0, container.ActualHeight), TaskList).Y) return (last, container, true);
        return null;
    }
    private void OnTaskDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (ReadFiles(e,out _))
        {
            ClearDragFeedback(); e.Effects=DragDropEffects.Copy;
            ShowMarker(ListPanel,new Rect(3,3,Math.Max(0,ListPanel.ActualWidth-6),Math.Max(0,ListPanel.ActualHeight-6)),true); return;
        }
        if (!ReadDraggedTask(e, out var id)) { ClearDragFeedback(); return; }
        lastDragPoint = e.GetPosition(TaskList);
        dragScrollHost = TaskList;
        UpdateListFeedback(id, lastDragPoint);
        scrollDirection = lastDragPoint.Y < 28 ? -1 : lastDragPoint.Y > TaskList.ActualHeight - 28 ? 1 : 0;
        if (scrollDirection != 0)
        {
            dragScrollTimer ??= NewScrollTimer(); dragScrollTimer.Start();
        }
        else dragScrollTimer?.Stop();
        if (ListDropTarget(lastDragPoint) is { } target && target.Row.Entry.Id != id) e.Effects = DragDropEffects.Move;
    }
    private DispatcherTimer NewScrollTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            var host = dragScrollHost ?? TaskList;
            var scroll = Descendant<ScrollViewer>(host);
            if (scrollDirection < 0) scroll?.LineUp(); else if (scrollDirection > 0) scroll?.LineDown();
            host.UpdateLayout();
            if (host == TaskList && activeDrag is { } id) UpdateListFeedback(id, lastDragPoint);
            if (host == CategoryList && activeCategory != null) UpdateCategoryFeedback(activeCategory, lastDragPoint);
        };
        return timer;
    }
    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void UpdateListFeedback(Guid id, Point point)
    {
        if (ListDropTarget(point) is not { } target || target.Row.Entry.Id == id) { ClearMarker(); return; }
        var start = target.Container.TranslatePoint(new Point(0, target.After ? target.Container.ActualHeight : 0), TaskList);
        ShowMarker(TaskList, new Rect(6, Math.Clamp(start.Y, 2, Math.Max(2, TaskList.ActualHeight - 2)), Math.Max(0, TaskList.ActualWidth - 18), 0), false);
    }
    private void OnTaskDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        try
        {
            if (ReadFiles(e,out var paths))
            {
                e.Effects=DragDropEffects.Copy;
                Dispatcher.BeginInvoke(()=>vm.ImportFiles(paths)); return;
            }
            if (!ReadDraggedTask(e, out var id) || ListDropTarget(e.GetPosition(TaskList)) is not { } target) return;
            var scroll = Descendant<ScrollViewer>(TaskList); double offset = scroll?.VerticalOffset ?? 0;
            if (vm.ReorderTask(id, target.Row.Entry.Id, target.After)) e.Effects = DragDropEffects.Move;
            Dispatcher.BeginInvoke(() => scroll?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        }
        catch (Exception ex) { vm.Message = "移动失败：" + ex.Message; }
        finally { ClearDragFeedback(); }
    }
    private bool ReadFiles(DragEventArgs e,out string[] paths)
    {
        paths=[];
        if(!vm.CanArrange || !e.Data.GetDataPresent(DataFormats.FileDrop,false)) return false;
        paths=e.Data.GetData(DataFormats.FileDrop,false) as string[] ?? [];
        return paths.Length>0 && (e.AllowedEffects & DragDropEffects.Copy)!=0;
    }
    private void OnCategoryDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        dragScrollTimer?.Stop();
        if (ReadDraggedCategory(e, out var name))
        {
            lastDragPoint = e.GetPosition(CategoryList); dragScrollHost = CategoryList;
            if (UpdateCategoryFeedback(name, lastDragPoint, e.OriginalSource as DependencyObject)) e.Effects = DragDropEffects.Move;
            scrollDirection = lastDragPoint.Y < 24 ? -1 : lastDragPoint.Y > CategoryList.ActualHeight - 24 ? 1 : 0;
            if (scrollDirection != 0) { dragScrollTimer ??= NewScrollTimer(); dragScrollTimer.Start(); }
            return;
        }
        var item = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (!ReadDraggedTasks(e, out var ids) || item?.DataContext is not CategoryItem category || category.Name == "全部任务" || vm.Rows.Where(x => ids.Contains(x.Entry.Id)).All(x => x.Entry.Group == category.Name)) { ClearMarker(); return; }
        e.Effects = DragDropEffects.Move;
        var point = item.TranslatePoint(new Point(), CategoryList);
        ShowMarker(CategoryList, new Rect(point, new Size(item.ActualWidth, item.ActualHeight)), true);
    }
    private bool UpdateCategoryFeedback(string source, Point point, DependencyObject? hit = null)
    {
        if (CategoryDropTarget(point, hit) is not { } target || target.Category.Name == source) { ClearMarker(); return false; }
        var start = target.Container.TranslatePoint(new Point(0, target.After ? target.Container.ActualHeight : 0), CategoryList);
        ShowMarker(CategoryList, new Rect(4, Math.Clamp(start.Y, 2, Math.Max(2, CategoryList.ActualHeight - 2)), Math.Max(0, CategoryList.ActualWidth - 8), 0), false);
        return true;
    }
    private void OnCategoryDrop(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        try
        {
            if (ReadDraggedCategory(e, out var name))
            {
                if (CategoryDropTarget(e.GetPosition(CategoryList), e.OriginalSource as DependencyObject) is { } target && vm.ReorderCategory(name, target.Category.Name, target.After)) e.Effects = DragDropEffects.Move;
                return;
            }
            var item = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (!ReadDraggedTasks(e, out var ids) || item?.DataContext is not CategoryItem category || category.Name == "全部任务") return;
            bool moved = ids.Length == 1 ? vm.MoveTaskToGroup(ids[0], category.Name) : vm.MoveTasksToGroup(ids, category.Name);
            if (moved) e.Effects = DragDropEffects.Move;
        }
        catch (Exception ex) { vm.Message = "移动分类失败：" + ex.Message; }
        finally { ClearDragFeedback(); }
    }
    private void OnTargetDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement target && !new Rect(0, 0, target.ActualWidth, target.ActualHeight).Contains(e.GetPosition(target))) ClearDragFeedback();
    }
    private void ShowMarker(UIElement target, Rect bounds, bool category)
    {
        ClearMarker(); markerLayer = AdornerLayer.GetAdornerLayer(target);
        if (markerLayer == null) return;
        marker = new DropMarker(target, bounds, category, (Brush)FindResource("AccentBrush")); markerLayer.Add(marker);
    }
    private void ClearMarker() { if (marker != null) markerLayer?.Remove(marker); marker = null; markerLayer = null; }
    private void ClearDragFeedback() { ClearMarker(); dragScrollTimer?.Stop(); scrollDirection = 0; dragScrollHost = null; }
}

internal sealed class DropMarker : Adorner
{
    private readonly Rect bounds;
    private readonly bool category;
    private readonly Brush brush;
    public DropMarker(UIElement target, Rect bounds, bool category, Brush brush) : base(target)
    {
        this.bounds = bounds; this.category = category; this.brush = brush;
        IsHitTestVisible = false;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        var pen = new Pen(brush, 2);
        if (category) drawing.DrawRoundedRectangle(null, pen, bounds, 8, 8);
        else
        {
            drawing.DrawLine(pen, bounds.TopLeft, bounds.TopRight);
            drawing.DrawEllipse(brush, null, bounds.TopLeft, 3, 3);
        }
    }
}
