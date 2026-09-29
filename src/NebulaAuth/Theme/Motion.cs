using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using NebulaAuth.Model;

namespace NebulaAuth.Theme;

/// <summary>
///     Short app-wide motion effects:
///     <list type="bullet">
///         <item>
///             every popup (menus, submenus, context menus, flyouts, ComboBox drop-downs) fades in with a slight
///             scale/slide when it opens (<see cref="Install" />, called once at startup);
///         </item>
///         <item>
///             <see cref="ExpandTransition" />: fade + slide of an Expander's content when it expands
///             (set on every Expander by Theme/Styles.axaml).
///         </item>
///     </list>
///     Closing is immediate: Avalonia closes popups and collapses Expander content synchronously.
/// </summary>
/// <remarks>
///     Popups are animated frame by frame, not with a wall-clock <see cref="Animation" />:
///     <list type="bullet">
///         <item>
///             the content is made transparent synchronously when <see cref="Popup.IsOpen" /> turns true, before the
///             popup window renders its first frame (Loaded comes after the first frames: they were shown at full
///             opacity, then the animation restarted from 0);
///         </item>
///         <item>
///             a new popup window stalls for ~100-150 ms before its frames reach the screen. A wall-clock animation of
///             150 ms was over during that stall and jumped from 0 to 1 in one frame (a visible "double bounce").
///             Progress is advanced per rendered frame with a capped step (the first frames stay transparent), so the
///             fade always spans several visible frames.
///         </item>
///     </list>
/// </remarks>
public static class Motion
{
    // Debug builds: NEBULA_MOTION_SLOW=<factor> slows every effect down (frame-by-frame checks)
    internal static double SlowFactor { get; } =
#if DEBUG
        double.TryParse(Environment.GetEnvironmentVariable("NEBULA_MOTION_SLOW"), out var slow) && slow > 0 ? slow : 1;
#else
        1;
#endif

    // After SlowFactor: static fields are initialized in declaration order
    private static readonly double PopupDurationMs = 140 * SlowFactor;

    // Largest progress step of one frame: at least 7 frames per fade, whatever the frame timing
    private const double MAX_STEP = 1.0 / 7;

    // The first frames of a new popup window are rendered before the window reaches the screen: keep them transparent
    private const int HIDDEN_FRAMES = 2;

    // Hover delay before a submenu opens (Avalonia default: 400 ms)
    private static readonly TimeSpan SubmenuShowDelay = TimeSpan.FromMilliseconds(120);

    private static readonly AttachedProperty<int> AnimationIdProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("MotionAnimationId", typeof(Motion));

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay = SubmenuShowDelay;
        // Registered after Popup's own handler: the popup window exists (not rendered yet) when this runs
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(OnPopupIsOpenChanged);
    }

    private static void OnPopupIsOpenChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || popup.Child is not { } content) return;
        // Tooltips have their own (Semi) look and appear after a delay; keep them static
        if (content is ToolTip) return;
        try
        {
            AnimatePopupContent(popup, content);
        }
        catch (Exception ex)
        {
            // A cosmetic effect must never break a popup
            content.ClearValue(Visual.OpacityProperty);
            Shell.Logger.Debug(ex, "Popup animation failed");
        }
    }

    private static void AnimatePopupContent(Popup popup, Control content)
    {
        var topLevel = TopLevel.GetTopLevel(content);
        if (topLevel == null) return;

        // Scale from the edge next to the placement target (drop-downs open below, or above near the window bottom)
        var below = true;
        if (popup.PlacementTarget is { } target && TopLevel.GetTopLevel(target) != null)
        {
            below = content.PointToScreen(default).Y >= target.PointToScreen(default).Y;
        }

        var id = content.GetValue(AnimationIdProperty) + 1;
        content.SetValue(AnimationIdProperty, id);
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        var originalTransform = content.RenderTransform;
        content.RenderTransformOrigin = new RelativePoint(0.5, below ? 0 : 1, RelativeUnit.Relative);
        content.RenderTransform = new TransformGroup {Children = {scale, translate}};
        var offset = below ? -4.0 : 4.0;
        var easing = new CubicEaseOut();
        var progress = 0.0;
        var frame = 0;
        TimeSpan? last = null;

        void Apply(double t)
        {
            var eased = easing.Ease(t);
            content.Opacity = eased;
            scale.ScaleX = scale.ScaleY = 0.97 + 0.03 * eased;
            translate.Y = offset * (1 - eased);
        }

        void Finish()
        {
            content.ClearValue(Visual.OpacityProperty);
            content.RenderTransform = originalTransform;
        }

        void OnFrame(TimeSpan time)
        {
            // A newer animation took over, or the popup closed (content detached from its window)
            if (content.GetValue(AnimationIdProperty) != id) return;
            if (TopLevel.GetTopLevel(content) is not { } current)
            {
                Finish();
                return;
            }

            var step = last is { } previous && frame++ >= HIDDEN_FRAMES
                ? (time - previous).TotalMilliseconds / PopupDurationMs
                : 0;
            last = time;
            progress = Math.Min(1, progress + Math.Clamp(step, 0, MAX_STEP));
            Apply(progress);
#if DEBUG
            Trace($"  frame opacity={content.Opacity:F2}");
#endif
            if (progress >= 1) Finish();
            else current.RequestAnimationFrame(OnFrame);
        }

        // Transparent before the first frame of the popup window
        Apply(0);
#if DEBUG
        Trace($"open {content.GetType().Name}");
#endif
        topLevel.RequestAnimationFrame(OnFrame);
    }

#if DEBUG
    private static readonly System.Diagnostics.Stopwatch TraceClock = System.Diagnostics.Stopwatch.StartNew();

    // NEBULA_MOTION_TRACE=1: frame log of the popup animations in ./motion-trace.log
    private static void Trace(string text)
    {
        if (Environment.GetEnvironmentVariable("NEBULA_MOTION_TRACE") != "1") return;
        System.IO.File.AppendAllText("motion-trace.log", $"{TraceClock.ElapsedMilliseconds,6} {text}{Environment.NewLine}");
    }
#endif

    /// <summary>
    ///     Opacity 0 -> 1, vertical offset <paramref name="offsetY" /> -> 0 and scale <paramref name="scale" /> -> 1.
    ///     Uses the Scale/Translate transforms of the control (created by the animation when RenderTransform is null).
    /// </summary>
    internal static Animation FadeSlideScale(TimeSpan duration, double offsetY, double scale)
    {
        return new Animation
        {
            Duration = duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, offsetY),
                        new Setter(ScaleTransform.ScaleXProperty, scale),
                        new Setter(ScaleTransform.ScaleYProperty, scale)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d),
                        new Setter(ScaleTransform.ScaleXProperty, 1d),
                        new Setter(ScaleTransform.ScaleYProperty, 1d)
                    }
                }
            }
        };
    }
}

/// <summary>
///     Expander content transition: the content fades in and slides down a few pixels when the Expander expands.
///     <c>&lt;Setter Property="ContentTransition"&gt;&lt;theme:ExpandTransition /&gt;&lt;/Setter&gt;</c>
/// </summary>
public class ExpandTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(180);

    public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        // Collapsing: Semi hides the content presenter before the transition starts, nothing left to animate
        if (to == null || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
        var animation = Motion.FadeSlideScale(Duration, -6, 1);
        return animation.RunAsync(to, cancellationToken);
    }
}
