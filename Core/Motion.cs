using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Shft.Core;

/// <summary>A critically damped-ish spring: overshoots a touch, settles fast.</summary>
public sealed class SpringEase : IEasingFunction
{
    public double Damping { get; init; } = 7.5;
    public double Frequency { get; init; } = 11;

    public double Ease(double t) => t >= 1 ? 1 : 1 - Math.Exp(-Damping * t) * Math.Cos(Frequency * t);
}

/// <summary>The one place that decides how things move, so bar, panels and Dock feel alike.</summary>
public static class Motion
{
    static readonly IEasingFunction Spring = new SpringEase();
    static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>True when movement should drop to plain fades.</summary>
    public static bool Reduced => SettingsStore.Current.Motion switch
    {
        MotionMode.Full => false,
        MotionMode.Reduced => true,
        _ => !SystemParameters.ClientAreaAnimation,
    };

    public static Duration Time(double ms) =>
        new(TimeSpan.FromMilliseconds(Reduced ? Math.Min(ms, 90) : ms / Math.Clamp(SettingsStore.Current.MotionSpeed, 0.25, 3)));

    static (ScaleTransform Scale, TranslateTransform Move) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] }) return (s, t);
        var scale = new ScaleTransform();
        var move = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { scale, move } };
        return (scale, move);
    }

    /// <summary>Panel opening: fade plus a small spring in scale and position.</summary>
    public static void Open(FrameworkElement el, Point origin, double fromY = -8, double fromScale = 0.95)
    {
        el.RenderTransformOrigin = origin;
        var (scale, move) = Transforms(el);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Time(130)) { EasingFunction = Out });
        if (Reduced) return;
        var spring = Time(300);
        var grow = new DoubleAnimation(fromScale, 1, spring) { EasingFunction = Spring };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, spring) { EasingFunction = Spring });
    }

    public static void Close(FrameworkElement el, Action done)
    {
        var (scale, _) = Transforms(el);
        var fade = new DoubleAnimation(0, Time(100)) { EasingFunction = Out };
        fade.Completed += (_, _) => done();
        if (!Reduced)
        {
            var shrink = new DoubleAnimation(0.97, Time(100)) { EasingFunction = Out };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Scale feedback while pressed, for anything clickable.</summary>
    public static void Press(FrameworkElement el, double to = 0.95)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var (scale, _) = Transforms(el);
        void To(double v, IEasingFunction ease, double ms)
        {
            if (Reduced) return;
            var a = new DoubleAnimation(v, Time(ms)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        el.PreviewMouseLeftButtonDown += (_, _) => To(to, Out, 80);
        el.PreviewMouseLeftButtonUp += (_, _) => To(1, Spring, 260);
        el.MouseLeave += (_, _) => To(1, Spring, 260);
    }

    /// <summary>Smooth colour change on a brush that is not frozen.</summary>
    public static void Tint(SolidColorBrush brush, Color to, double ms = 220) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, Time(ms)));

    public static void Fade(UIElement el, double to, double ms = 180) =>
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, Time(ms)) { EasingFunction = Out });
}
