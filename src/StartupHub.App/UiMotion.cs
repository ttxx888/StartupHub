using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StartupHub.App;

public static class UiMotion
{
    internal static TimeSpan Duration(string key) => ((Duration)Application.Current.FindResource(key)).TimeSpan;
    public static bool Enabled => SystemParameters.ClientAreaAnimation && SystemParameters.UIEffects;

    public static readonly DependencyProperty FeedbackProperty = DependencyProperty.RegisterAttached(
        "Feedback", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnFeedbackChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(UiMotion));

    public static bool GetFeedback(DependencyObject target) => (bool)target.GetValue(FeedbackProperty);
    public static void SetFeedback(DependencyObject target, bool value) => target.SetValue(FeedbackProperty, value);

    public static void Finish(FrameworkElement target)
    {
        if (target.GetValue(StateProperty) is not MotionState state) return;
        state.OpacityAnimation = null;
        state.MoveAnimation = null;
        state.ConcealCompleted = null;
        state.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        target.BeginAnimation(UIElement.OpacityProperty, null);
    }

    public static void Reveal(FrameworkElement target)
    {
        var state = State(target);
        state.OpacityAnimation = null;
        state.ConcealCompleted = null;
        if (!Enabled) { state.Translation.BeginAnimation(TranslateTransform.YProperty, null); target.BeginAnimation(UIElement.OpacityProperty, null); return; }
        Move(target, state.Translation.Y == 0 ? 6 : state.Translation.Y, Duration("RevealDuration"), false);
        var opacity = new DoubleAnimation(target.HasAnimatedProperties ? target.Opacity : 0, target.GetAnimationBaseValue(UIElement.OpacityProperty) is double value ? value : 1,
            Duration("RevealDuration")) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        opacity.Completed += (_, _) => { if (ReferenceEquals(state.OpacityAnimation, opacity)) { target.BeginAnimation(UIElement.OpacityProperty, null); state.OpacityAnimation = null; } };
        state.OpacityAnimation = opacity;
        target.BeginAnimation(UIElement.OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Conceal(FrameworkElement target, Action completed)
    {
        var state = State(target);
        state.OpacityAnimation = null;
        state.ConcealCompleted = null;
        if (!Enabled) { target.BeginAnimation(UIElement.OpacityProperty, null); completed(); return; }
        var animation = new DoubleAnimation(target.Opacity, 0, Duration("FeedbackDuration"))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        state.OpacityAnimation = animation;
        state.ConcealCompleted = completed;
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(state.OpacityAnimation, animation)) return;
            state.OpacityAnimation = null;
            state.ConcealCompleted = null;
            target.BeginAnimation(UIElement.OpacityProperty, null);
            completed();
        };
        target.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void MoveToRest(FrameworkElement target, double fromY) => Move(target, Math.Clamp(fromY + State(target).Translation.Y, -220, 220), Duration("ReorderDuration"), true);

    private static void Move(FrameworkElement target, double fromY, TimeSpan duration, bool spring)
    {
        var state = State(target);
        state.Translation.BeginAnimation(TranslateTransform.YProperty, null);
        state.Translation.Y = 0;
        if (!Enabled || Math.Abs(fromY) < .1) return;
        var animation = new DoubleAnimation(fromY, 0, duration)
        {
            EasingFunction = spring ? new BackEase { Amplitude = .12, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        state.MoveAnimation = animation;
        animation.Completed += (_, _) => { if (ReferenceEquals(state.MoveAnimation, animation)) { state.Translation.BeginAnimation(TranslateTransform.YProperty, null); state.MoveAnimation = null; } };
        state.Translation.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public static void AnimateColumn(ColumnDefinition column, double from, double to)
    {
        var current = column.HasAnimatedProperties ? column.Width.Value : from;
        column.BeginAnimation(ColumnDefinition.WidthProperty, null);
        column.Width = new GridLength(Math.Max(0, to));
        if (!Enabled || Math.Abs(current - to) < .1) return;
        var animation = new ColumnAnimation { From = Math.Max(0, current), To = Math.Max(0, to), Duration = Duration("PanelDuration"), FillBehavior = FillBehavior.Stop };
        column.BeginAnimation(ColumnDefinition.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static MotionState State(FrameworkElement target)
    {
        if (target.GetValue(StateProperty) is MotionState state) return state;
        state = new MotionState(target);
        target.SetValue(StateProperty, state);
        return state;
    }

    private static void OnFeedbackChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ButtonBase button) return;
        if ((bool)args.NewValue)
        {
            button.Loaded += OnButtonLoaded;
            button.Unloaded += OnButtonUnloaded;
            if (button.IsLoaded) State(button).Listen(button);
        }
        else
        {
            button.Loaded -= OnButtonLoaded;
            button.Unloaded -= OnButtonUnloaded;
            if (button.GetValue(StateProperty) is MotionState state) state.StopListening(button);
        }
    }

    private static void OnButtonLoaded(object sender, RoutedEventArgs args) => State((ButtonBase)sender).Listen((ButtonBase)sender);
    private static void OnButtonUnloaded(object sender, RoutedEventArgs args)
    {
        var button = (ButtonBase)sender;
        if (button.GetValue(StateProperty) is MotionState state) state.StopListening(button);
    }

    private sealed class MotionState
    {
        private static readonly DependencyPropertyDescriptor Pressed = DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));
        private readonly FrameworkElement target;
        private bool listening;
        private bool watchingSystem;
        public TranslateTransform Translation { get; } = new();
        private ScaleTransform Scale { get; } = new(1, 1);
        public DoubleAnimation? MoveAnimation;
        public DoubleAnimation? OpacityAnimation;
        public Action? ConcealCompleted;

        public MotionState(FrameworkElement target)
        {
            this.target = target;
            var transforms = new TransformGroup();
            if (target.RenderTransform is { } original) transforms.Children.Add(original);
            transforms.Children.Add(Scale);
            transforms.Children.Add(Translation);
            target.SetCurrentValue(UIElement.RenderTransformProperty, transforms);
            target.Loaded += OnLoaded;
            target.Unloaded += OnUnloaded;
            if (target.IsLoaded) WatchSystem();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => WatchSystem();
        private void WatchSystem()
        {
            if (watchingSystem) return;
            watchingSystem = true;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        }
        private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (Enabled) return;
            target.Dispatcher.BeginInvoke(() =>
            {
                if (Enabled) return;
                var completed = ConcealCompleted;
                ConcealCompleted = null; OpacityAnimation = null; MoveAnimation = null;
                Translation.BeginAnimation(TranslateTransform.YProperty, null);
                target.BeginAnimation(UIElement.OpacityProperty, null);
                ResetScale();
                completed?.Invoke();
            });
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (watchingSystem) SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            watchingSystem = false;
            Translation.BeginAnimation(TranslateTransform.YProperty, null);
            target.BeginAnimation(UIElement.OpacityProperty, null);
            ResetScale();
            MoveAnimation = null; OpacityAnimation = null; ConcealCompleted = null;
        }

        public void Listen(ButtonBase button)
        {
            if (listening) return;
            Pressed.AddValueChanged(button, OnPressed);
            listening = true;
        }

        public void StopListening(ButtonBase button)
        {
            if (listening) Pressed.RemoveValueChanged(button, OnPressed);
            listening = false;
            ResetScale();
        }

        private void ResetScale()
        {
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        private void OnPressed(object? sender, EventArgs args)
        {
            if (sender is not ButtonBase button || !Enabled) { ResetScale(); return; }
            Scale.CenterX = button.ActualWidth / 2; Scale.CenterY = button.ActualHeight / 2;
            bool pressed = button.IsPressed;
            var animation = new DoubleAnimation(pressed ? .98 : 1, Duration(pressed ? "PressDuration" : "RevealDuration"))
            {
                EasingFunction = pressed ? new CubicEase { EasingMode = EasingMode.EaseOut } : new BackEase { Amplitude = .2, EasingMode = EasingMode.EaseOut },
                FillBehavior = pressed ? FillBehavior.HoldEnd : FillBehavior.Stop
            };
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private sealed class ColumnAnimation : AnimationTimeline
    {
        public double From { get; init; }
        public double To { get; init; }
        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new ColumnAnimation { From = From, To = To };
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            double progress = animationClock.CurrentProgress ?? 1;
            progress = 1 - Math.Pow(1 - progress, 3);
            return new GridLength(From + (To - From) * progress);
        }
    }
}
