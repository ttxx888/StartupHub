using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StartupHub.App;

// Attach to a DataGrid, ListBox or explicit ScrollViewer. Editors keep their native scrolling.
public static class ScrollEffects
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ScrollEffects), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(ScrollState), typeof(ScrollEffects));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element || target is not (DataGrid or ListBox or ScrollViewer)) return;
        if ((bool)args.NewValue)
        {
            element.Loaded += OnLoaded; element.Unloaded += OnUnloaded;
            if (element.IsLoaded) Attach(element);
        }
        else
        {
            element.Loaded -= OnLoaded; element.Unloaded -= OnUnloaded;
            Detach(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Attach((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs args) => Detach((FrameworkElement)sender);
    private static void Attach(FrameworkElement owner)
    {
        if (owner.GetValue(StateProperty) is ScrollState) return;
        owner.ApplyTemplate();
        var viewer = owner as ScrollViewer ?? Find<ScrollViewer>(owner);
        if (viewer == null) return;
        viewer.ApplyTemplate();
        var presenter = Find<ScrollContentPresenter>(viewer);
        if (presenter == null) return;
        owner.SetValue(StateProperty, new ScrollState(viewer, presenter));
    }

    private static void Detach(FrameworkElement owner)
    {
        if (owner.GetValue(StateProperty) is ScrollState state) state.Dispose();
        owner.ClearValue(StateProperty);
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (Find<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed class ScrollState : IDisposable
    {
        private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(ScrollEffects),
            new PropertyMetadata(0d, (target, args) => ((ScrollViewer)target).ScrollToVerticalOffset((double)args.NewValue)));
        private readonly ScrollViewer viewer;
        private readonly ScrollContentPresenter presenter;
        private readonly LinearGradientBrush mask = new();
        private static readonly double[] FadePositions = { 0, .15, .30, .45, .60, .75, .87, 1 };
        private static readonly byte[] FadeAlpha = { 0, 13, 38, 77, 128, 179, 217, 255 };
        private readonly bool ownsMask;
        private AnimationClock? clock;
        private double destination;
        private bool disposed;

        public ScrollState(ScrollViewer viewer, ScrollContentPresenter presenter)
        {
            this.viewer = viewer; this.presenter = presenter;
            mask.StartPoint = new Point(.5, 0); mask.EndPoint = new Point(.5, 1);
            for (int i = 0; i < 16; i++) mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            ownsMask = presenter.OpacityMask == null;
            if (ownsMask) presenter.SetCurrentValue(UIElement.OpacityMaskProperty, mask);
            viewer.PreviewMouseWheel += OnWheel;
            viewer.ScrollChanged += OnScroll;
            viewer.PreviewMouseDown += OnMouseDown;
            viewer.PreviewKeyDown += OnKeyDown;
            viewer.PreviewDragOver += OnDragOver;
            viewer.RequestBringIntoView += OnBringIntoView;
            presenter.SizeChanged += OnSizeChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            UpdateFade();
        }

        private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            viewer.Dispatcher.BeginInvoke(() => { if (disposed) return; if (!UiMotion.Enabled) Stop(); UpdateFade(); });
        }

        private void OnScroll(object sender, ScrollChangedEventArgs args) => UpdateFade();
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateFade();
        private void OnMouseDown(object sender, MouseButtonEventArgs args) => Stop();
        private void OnKeyDown(object sender, KeyEventArgs args) => Stop();
        private void OnDragOver(object sender, DragEventArgs args) => Stop();
        private void OnBringIntoView(object sender, RequestBringIntoViewEventArgs args) => Stop();

        private void OnWheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || args.Delta == 0 || IsEditor(args.OriginalSource as DependencyObject) || (Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
            if (!UiMotion.Enabled) { Stop(); return; }
            // Logical item offsets are intentionally left to WPF; pixel virtualization is retained.
            if (viewer.CanContentScroll && Find<VirtualizingStackPanel>(presenter) is { } panel &&
                VirtualizingPanel.GetScrollUnit((DependencyObject?)ItemsControl.GetItemsOwner(panel) ?? panel) != ScrollUnit.Pixel) return;
            double current = viewer.VerticalOffset;
            if (args.Delta > 0 && current <= .1 || args.Delta < 0 && current >= viewer.ScrollableHeight - .1)
            {
                Stop();
                if (VisualTreeHelper.GetParent(viewer) is UIElement parent)
                {
                    args.Handled = true;
                    parent.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
                }
                return;
            }
            int lines = SystemParameters.WheelScrollLines;
            if (lines == 0) return;
            double step = lines < 0 ? viewer.ViewportHeight : Math.Max(16, lines * 16);
            double next = Math.Clamp((clock != null ? destination : current) - args.Delta / 120d * step, 0, viewer.ScrollableHeight);
            Stop();
            destination = next;
            viewer.SetValue(OffsetProperty, current);
            var animation = new DoubleAnimation(current, next, UiMotion.Duration("ScrollDuration"))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
            var nextClock = animation.CreateClock();
            clock = nextClock;
            nextClock.Completed += (_, _) =>
            {
                if (disposed || !ReferenceEquals(clock, nextClock)) return;
                Stop(); viewer.ScrollToVerticalOffset(next);
            };
            viewer.ApplyAnimationClock(OffsetProperty, nextClock, HandoffBehavior.SnapshotAndReplace);
            args.Handled = true;
        }

        private static bool IsEditor(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is TextBoxBase or PasswordBox) return true;
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }

        private void Stop()
        {
            if (clock == null) return;
            clock = null;
            viewer.SetValue(OffsetProperty, viewer.VerticalOffset);
            viewer.ApplyAnimationClock(OffsetProperty, null);
        }

        private void UpdateFade()
        {
            if (!ownsMask) return;
            double edge = Math.Min(.5, 20 / Math.Max(1, presenter.ActualHeight));
            bool top = UiMotion.Enabled && viewer.VerticalOffset > .5;
            bool bottom = UiMotion.Enabled && viewer.VerticalOffset < viewer.ScrollableHeight - .5;
            for (int i = 0; i < 8; i++)
            {
                mask.GradientStops[i].Offset = edge * FadePositions[i];
                mask.GradientStops[i].Color = Color.FromArgb(top ? FadeAlpha[i] : (byte)255, 0, 0, 0);
                mask.GradientStops[i + 8].Offset = 1 - edge * FadePositions[7 - i];
                mask.GradientStops[i + 8].Color = Color.FromArgb(bottom ? FadeAlpha[7 - i] : (byte)255, 0, 0, 0);
            }
        }

        public void Dispose()
        {
            disposed = true; Stop();
            viewer.PreviewMouseWheel -= OnWheel; viewer.ScrollChanged -= OnScroll;
            viewer.PreviewMouseDown -= OnMouseDown; viewer.PreviewKeyDown -= OnKeyDown;
            viewer.PreviewDragOver -= OnDragOver; viewer.RequestBringIntoView -= OnBringIntoView;
            presenter.SizeChanged -= OnSizeChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            if (ownsMask && ReferenceEquals(presenter.OpacityMask, mask)) presenter.ClearValue(UIElement.OpacityMaskProperty);
        }
    }
}
