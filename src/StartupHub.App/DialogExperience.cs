using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace StartupHub.App;

public static class InputComposition
{
    private static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(InputComposition), new PropertyMetadata(false));

    public static bool IsActive(Window window, KeyEventArgs? args = null) =>
        (bool)window.GetValue(ActiveProperty) || args?.Key == Key.ImeProcessed;

    public static void Attach(Window window)
    {
        window.AddHandler(TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, _) => window.SetValue(ActiveProperty, true)), true);
        window.AddHandler(TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, _) => window.SetValue(ActiveProperty, false)), true);
        window.Deactivated += (_, _) => window.SetValue(ActiveProperty, false);
        window.PreviewKeyDown += (_, e) =>
        {
            if (IsActive(window) && e.Key is Key.Enter or Key.Escape) e.Handled = true;
        };
    }
}

public static class DialogExperience
{
    public static void Attach(Window window, Func<IInputElement?> initialFocus)
    {
        InputComposition.Attach(window);
        KeyboardNavigation.SetTabNavigation(window, KeyboardNavigationMode.Cycle);
        IInputElement? previousFocus = null;
        window.Loaded += (_, _) =>
        {
            previousFocus = window.Owner != null ? FocusManager.GetFocusedElement(window.Owner) : null;
            window.Dispatcher.BeginInvoke(() =>
            {
                if (!window.IsVisible) return;
                var target = initialFocus(); Keyboard.Focus(target);
                if (target is FrameworkElement element) element.BringIntoView();
            }, DispatcherPriority.Input);
        };
        window.ContentRendered += (_, _) => { if (window.Content is FrameworkElement content) UiMotion.Reveal(content); };
        window.Closed += (_, _) =>
        {
            if (window.Content is FrameworkElement content) UiMotion.Finish(content);
            if (window.Owner is not { IsVisible: true } owner || previousFocus == null) return;
            owner.Dispatcher.BeginInvoke(() =>
            {
                if (owner.IsActive && previousFocus is UIElement { IsVisible: true, IsEnabled: true }) Keyboard.Focus(previousFocus);
            }, DispatcherPriority.Input);
        };
    }

    public static void Status(TextBlock target, string text, bool error = false)
    {
        target.Text = text;
        target.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "SuccessBrush");
        target.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(target, AutomationLiveSetting.Polite);
    }

    public static bool Confirm(Window owner, string title, string description, string acceptLabel)
    {
        var window = new Window
        {
            Owner = owner, Title = title, Width = 460, SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 40),
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)Application.Current.FindResource(typeof(Window))
        };
        var dock = new DockPanel { Margin = new Thickness(24) }; window.Content = dock;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, IsDefault = true };
        var accept = new Button { Content = acceptLabel, Style = (Style)window.FindResource("PrimaryButton") };
        accept.Click += (_, _) => window.DialogResult = true;
        footer.Children.Add(cancel); footer.Children.Add(accept);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var body = new StackPanel(); EntryEditor.Heading(body, title, description, true);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Attach(window, () => cancel);
        return window.ShowDialog() == true;
    }
}
