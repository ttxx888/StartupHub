using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Text.Json;

namespace StartupHub.App;

public sealed class DialogLayout : DockPanel
{
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    public TextBlock Error { get; } = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    public ListBox Navigation { get; } = new() { MinWidth = 144, Margin = new Thickness(0,0,20,0) };
    private readonly Grid pages = new();
    private readonly Dictionary<string, ScrollViewer> sections = new();
    public DialogLayout(Window window)
    {
        window.Width = 860; window.Height = 640; window.MinWidth = 640; window.MinHeight = 480;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Margin = new Thickness(24);
        var footer = new DockPanel { Margin = new Thickness(0,16,0,0) };
        SetDock(Actions, Dock.Right); footer.Children.Add(Actions);
        Error.SetResourceReference(TextBlock.ForegroundProperty,"DangerBrush"); footer.Children.Add(Error);
        SetDock(footer,Dock.Bottom); Children.Add(footer);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width=GridLength.Auto }); body.ColumnDefinitions.Add(new());
        body.Children.Add(Navigation); Grid.SetColumn(pages,1); body.Children.Add(pages); Children.Add(body);
        AutomationProperties.SetName(Navigation,"设置分组");
        Navigation.SelectionChanged += (_,_) => { foreach(var item in sections) item.Value.Visibility = Equals(Navigation.SelectedItem,item.Key) ? Visibility.Visible : Visibility.Collapsed; };
    }
    public void AddSection(string title, Panel content)
    {
        var scroll = new ScrollViewer { Content=content, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, Visibility=Visibility.Collapsed };
        ScrollEffects.SetEnabled(scroll,true); sections.Add(title,scroll); pages.Children.Add(scroll); Navigation.Items.Add(title);
        if(Navigation.SelectedIndex<0) Navigation.SelectedIndex=0;
    }
    public void ShowSection(string title) { if(Navigation.Items.Contains(title)) Navigation.SelectedItem=title; }
    public void SetAvailable(string title,bool available)
    {
        if(available && !Navigation.Items.Contains(title)) Navigation.Items.Insert(sections.Keys.TakeWhile(x=>x!=title).Count(x=>Navigation.Items.Contains(x)),title);
        if(!available && Navigation.Items.Contains(title)) { Navigation.Items.Remove(title); if(Navigation.SelectedIndex<0) Navigation.SelectedIndex=0; }
    }
    public void Reveal(FrameworkElement field)
    {
        foreach(var section in sections) if(Descendants(section.Value).Contains(field)) { ShowSection(section.Key); break; }
        UpdateLayout(); field.Focus(); field.BringIntoView();
    }
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach(var node in Descendants(child)) yield return node;
    }
    public static string FormState(Window window) => JsonSerializer.Serialize(Descendants(window).Select(x => x switch {
        TextBox t when AutomationProperties.GetName(t)!="搜索前置任务" => t.Text, ComboBox c => c.Text, ToggleButton t => t.IsChecked.ToString(),
        DataGrid g => JsonSerializer.Serialize(g.ItemsSource), _ => null }).Where(x=>x!=null));
}

public sealed class DraftClose
{
    private readonly Window window;
    private readonly Func<bool> dirty;
    private readonly Func<bool> save;
    private bool discard;
    public bool Discarded { get; private set; }
    public DraftClose(Window window, Button cancel, Func<bool> dirty, Func<bool> save)
    {
        this.window=window; this.dirty=dirty; this.save=save;
        cancel.Click += (_,_) => discard=true;
        window.PreviewKeyDown += (_,e) => {
            if(e.Key!=Key.Escape || InputComposition.IsActive(window,e)) return;
            var combo=DialogLayout.Descendants(window).OfType<ComboBox>().FirstOrDefault(c=>c.IsDropDownOpen);
            if(combo!=null) { combo.IsDropDownOpen=false; e.Handled=true; return; }
            e.Handled=true; window.Close();
        };
        window.Closing += (_,e) => { if(window.IsVisible && !discard && window.DialogResult!=true && dirty()) { e.Cancel=true; window.Dispatcher.BeginInvoke(new Action(()=> { if(Resolve()) { discard=true; if(window.IsVisible) window.Close(); } })); } };
    }
    public bool Resolve()
    {
        if(!dirty()) return true;
        var dialog=new Window { Title="未保存的更改",Owner=window,Width=480,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)Application.Current.FindResource(typeof(Window)) };
        var panel=new StackPanel { Margin=new Thickness(24) }; dialog.Content=panel;
        EntryEditor.Heading(panel,"保存更改？",null,true);
        var actions=new WrapPanel { Margin=new Thickness(0,20,0,0) }; panel.Children.Add(actions);
        int choice=0;
        var keep=new Button {Content="继续编辑",IsCancel=true,IsDefault=true};
        var drop=new Button {Content="放弃更改"}; drop.Click+=(_,_)=>{choice=1;dialog.Close();};
        var commit=new Button {Content="保存",Style=(Style)dialog.FindResource("PrimaryButton")}; commit.Click+=(_,_)=>{choice=2;dialog.Close();};
        actions.Children.Add(keep);actions.Children.Add(drop);actions.Children.Add(commit); DialogExperience.Attach(dialog,()=>keep);dialog.ShowDialog();
        Discarded=choice==1;
        return choice==1 || choice==2 && save();
    }
}
