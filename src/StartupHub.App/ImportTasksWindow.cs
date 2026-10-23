using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using StartupHub.Core;

namespace StartupHub.App;

public sealed class ImportTaskRow(ImportCandidate candidate) : Observable
{
    public TaskEntry? Entry { get; } = candidate.Entry;
    public string Source { get; } = candidate.Source;
    public string Issue { get; } = candidate.Issue;
    public bool CanSelect => Entry != null && Issue.Length == 0;
    public bool NameReadOnly => !CanSelect;
    private bool selected = candidate.Entry != null && candidate.Issue.Length == 0;
    public bool Selected { get => selected; set { selected = value && CanSelect; Changed(); } }
    public string Name { get => Entry?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Source); set { if (Entry != null) Entry.Name = value; Changed(); } }
    public string Kind => Entry?.KindText ?? "无效入口";
}

public sealed class ImportTasksWindow : Window
{
    private readonly ImportTaskRow[] rows;
    private readonly ComboBox group;
    private readonly DataGrid list;
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap };
    public IReadOnlyList<TaskEntry> Imported { get; private set; } = [];
    public ImportTasksWindow(IReadOnlyList<ImportCandidate> candidates,IReadOnlyList<string> groups,string initialGroup)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "批量添加任务"; Width = 900; MinWidth = 650; Height = Math.Min(580,SystemParameters.WorkArea.Height-40); MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        rows = candidates.Select(x => new ImportTaskRow(x)).ToArray();
        var dock = new DockPanel { Margin = new Thickness(24) }; Content = dock;
        var top = new StackPanel(); EntryEditor.Heading(top,"批量添加任务","核对名称、取消不需要的项目，再统一选择分类。",true);
        var options = new WrapPanel { Margin = new Thickness(0,8,0,12) };
        var selectAll = new Button { Content = "全选可添加项" }; selectAll.Click += (_,_) => { foreach(var row in rows) row.Selected = row.CanSelect; };
        var clear = new Button { Content = "清空选择" }; clear.Click += (_,_) => { foreach(var row in rows) row.Selected = false; };
        options.Children.Add(selectAll); options.Children.Add(clear);
        group = new ComboBox { ItemsSource = groups.Count > 0 ? groups : new[] {"未分类"}, SelectedItem = initialGroup, Width = 180, Margin = new Thickness(0,0,0,8) };
        AutomationProperties.SetName(group,"导入分类");
        if (group.SelectedIndex < 0) group.SelectedIndex = 0;
        options.Children.Add(group); top.Children.Add(options); DockPanel.SetDock(top,Dock.Top); dock.Children.Add(top);
        var footer = new DockPanel { Margin = new Thickness(0,16,0,0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var add = new Button { Content = "添加所选", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        buttons.Children.Add(cancel); buttons.Children.Add(add); DockPanel.SetDock(buttons,Dock.Right); footer.Children.Add(buttons);
        var summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }; footer.Children.Add(summary);
        DockPanel.SetDock(footer,Dock.Bottom); dock.Children.Add(footer);
        validation.SetResourceReference(TextBlock.ForegroundProperty,"DangerBrush"); DockPanel.SetDock(validation,Dock.Bottom); dock.Children.Add(validation);
        list = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, MinRowHeight = 46, SelectionMode = DataGridSelectionMode.Single };
        var selectTemplate = new DataTemplate(); var checkbox = new FrameworkElementFactory(typeof(CheckBox));
        checkbox.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(ImportTaskRow.Selected)) {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
        checkbox.SetBinding(CheckBox.IsEnabledProperty,new Binding(nameof(ImportTaskRow.CanSelect))); checkbox.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center); selectTemplate.VisualTree = checkbox;
        checkbox.SetBinding(AutomationProperties.NameProperty,new Binding(nameof(ImportTaskRow.Name)));
        list.Columns.Add(new DataGridTemplateColumn {Header="选择",CellTemplate=selectTemplate,Width=48});
        var nameTemplate = new DataTemplate(); var name = new FrameworkElementFactory(typeof(TextBox));
        name.SetValue(FrameworkElement.HeightProperty,36d); name.SetValue(Control.MinHeightProperty,32d); name.SetValue(Control.PaddingProperty,new Thickness(8,4,8,4)); name.SetValue(FrameworkElement.MarginProperty,new Thickness(0,4,0,4)); name.SetValue(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center);
        name.SetBinding(TextBox.TextProperty,new Binding(nameof(ImportTaskRow.Name)) {Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged}); name.SetBinding(TextBox.IsReadOnlyProperty,new Binding(nameof(ImportTaskRow.NameReadOnly))); nameTemplate.VisualTree = name;
        list.Columns.Add(new DataGridTemplateColumn {Header="名称",CellTemplate=nameTemplate,Width=150});
        var typeStyle=new Style(typeof(TextBlock)); typeStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(6,0,6,0)));
        list.Columns.Add(new DataGridTextColumn {Header="类型",Binding=new Binding(nameof(ImportTaskRow.Kind)),ElementStyle=typeStyle,IsReadOnly=true,Width=85});
        var sourceStyle=new Style(typeof(TextBlock)); sourceStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(6,0,6,0))); sourceStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis)); sourceStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(nameof(ImportTaskRow.Source))));
        var issueStyle=new Style(typeof(TextBlock)); issueStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(8,4,6,4))); issueStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap)); issueStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding(nameof(ImportTaskRow.Issue))));
        list.Columns.Add(new DataGridTextColumn {Header="入口",Binding=new Binding(nameof(ImportTaskRow.Source)),ElementStyle=sourceStyle,IsReadOnly=true,Width=new DataGridLength(1,DataGridLengthUnitType.Star),MinWidth=100});
        list.Columns.Add(new DataGridTextColumn {Header="说明",Binding=new Binding(nameof(ImportTaskRow.Issue)),ElementStyle=issueStyle,IsReadOnly=true,Width=new DataGridLength(.8,DataGridLengthUnitType.Star),MinWidth=100}); dock.Children.Add(list);
        AutomationProperties.SetName(list, "待导入任务");
        void UpdateSummary()
        {
            int count=rows.Count(x => x.Selected && x.CanSelect);
            summary.Text=$"已选 {count} 项 · 跳过 {rows.Count(x => !x.CanSelect)} 项"; add.IsEnabled=count>0;
        }
        foreach(var row in rows) row.PropertyChanged+=(_,_)=>UpdateSummary(); UpdateSummary();
        add.Click+=(_,_)=>
        {
            if (InputComposition.IsActive(this)) return;
            list.CommitEdit(DataGridEditingUnit.Cell,true); list.CommitEdit(DataGridEditingUnit.Row,true);
            var selected=rows.Where(x => x.Selected && x.CanSelect).ToArray();
            if(selected.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Name)) is { } invalid) {validation.Text="请为所选任务填写名称。"; list.SelectedItem=invalid; list.ScrollIntoView(invalid); list.Focus(); return;}
            Imported=selected.Select(x=> {var entry=x.Entry!.Copy();entry.Name=x.Name.Trim();entry.Group=(string)group.SelectedItem;return entry;}).ToArray();
            DialogResult=true;
        };
        DialogExperience.Attach(this, () => list);
    }
}
