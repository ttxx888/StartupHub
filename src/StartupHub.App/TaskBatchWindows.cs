using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StartupHub.Core;

namespace StartupHub.App;

public sealed class MoveTasksWindow : Window
{
    private readonly ComboBox group;
    public string Destination => (string)group.SelectedItem;
    public MoveTasksWindow(IReadOnlyList<string> groups, int count)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "移动到分类"; Width = 420; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        EntryEditor.Heading(panel, "移动到分类", $"将选中的 {count} 项移入目标分类末尾，保持相对顺序。", true);
        group = new ComboBox { ItemsSource = groups, SelectedIndex = 0 };
        AutomationProperties.SetName(group, "目标分类");
        EntryEditor.Add(panel, "目标分类", group);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var move = new Button { Content = "移动", IsDefault = true, Style = (Style)FindResource("PrimaryButton"), IsEnabled = groups.Count > 0 };
        move.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(move); panel.Children.Add(buttons);
        DialogExperience.Attach(this, () => group);
    }
}

public sealed class DeleteTasksWindow : Window
{
    public DeleteTasksWindow(IReadOnlyList<TaskEntry> entries, IReadOnlyList<TaskEntry> dependents)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "删除任务"; Width = 520; Height = Math.Min(560, SystemParameters.WorkArea.Height - 40); MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(24) }; Content = dock;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true };
        var delete = new Button { Content = $"删除 {entries.Count} 项", Style = (Style)FindResource("PrimaryButton") };
        delete.Click += (_, _) => DialogResult = true;
        footer.Children.Add(cancel); footer.Children.Add(delete);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var panel = new StackPanel();
        EntryEditor.Heading(panel, $"删除选中的 {entries.Count} 项？", "仅从启动台清单移除，项目文件和已运行的程序会保留。", true);
        foreach (var entry in entries) panel.Children.Add(new TextBlock { Text = "• " + entry.Name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
        if (dependents.Count > 0)
        {
            EntryEditor.Heading(panel, "同时解除以下前置依赖", "这些项目将保留，之后启动时不再等待被删除的前置任务。");
            foreach (var entry in dependents)
            {
                var parent = entries.First(e => e.Id == entry.DependsOn);
                panel.Children.Add(new TextBlock { Text = entry.Name + " → " + parent.Name, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            }
        }
        dock.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        DialogExperience.Attach(this, () => cancel);
    }
}
