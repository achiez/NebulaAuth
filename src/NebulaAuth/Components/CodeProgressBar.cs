using System;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;

namespace NebulaAuth;

/// <summary>
///     Progress bar of the Steam Guard code lifetime: when <see cref="TimeRemaining" /> changes, animates smoothly from
///     the elapsed part to 100% in <see cref="TimeRemaining" /> seconds. Uses the regular ProgressBar theme.
/// </summary>
public class CodeProgressBar : ProgressBar
{
    public static readonly StyledProperty<double> TimeRemainingProperty =
        AvaloniaProperty.Register<CodeProgressBar, double>(nameof(TimeRemaining), -1.0);

    public static readonly StyledProperty<double> MaxTimeProperty =
        AvaloniaProperty.Register<CodeProgressBar, double>(nameof(MaxTime), 30.0);

    private CancellationTokenSource? _animationCts;

    public double TimeRemaining
    {
        get => GetValue(TimeRemainingProperty);
        set => SetValue(TimeRemainingProperty, value);
    }

    public double MaxTime
    {
        get => GetValue(MaxTimeProperty);
        set => SetValue(MaxTimeProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ProgressBar);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TimeRemainingProperty)
        {
            StartProgressAnimation(change.GetNewValue<double>());
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _animationCts?.Cancel();
    }

    private void StartProgressAnimation(double timeRemaining)
    {
        if (timeRemaining <= 0 || MaxTime <= 0) return;

        _animationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _animationCts = cts;

        var progress = Math.Clamp((1 - timeRemaining / MaxTime) * 100, 0, 100);
        Minimum = 0;
        Maximum = 100;
        Value = 100;
        var animation = new Animation
        {
            Duration = TimeSpan.FromSeconds(timeRemaining),
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame {Cue = new Cue(0), Setters = {new Setter(ValueProperty, progress)}},
                new KeyFrame {Cue = new Cue(1), Setters = {new Setter(ValueProperty, 100d)}}
            }
        };
        _ = animation.RunAsync(this, cts.Token);
    }
}
