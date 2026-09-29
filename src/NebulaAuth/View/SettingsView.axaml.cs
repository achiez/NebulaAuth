using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DialogHostAvalonia;
using NebulaAuth.Core;
using NebulaAuth.Model;

namespace NebulaAuth.View;

public partial class SettingsView : UserControl
{
    // While a background slider is dragged the dialog almost disappears, so the effect is visible on the window
    private const double DRAG_OPACITY = 0.05;

    private bool _isSliderDragging;
    private TopLevel? _dragTopLevel;
    private IBrush? _hostBackground;
    private IBrush _hostOverlayBackground = Brushes.Transparent;
    private IBrush _hostBorderBrush = Brushes.Transparent;
    private BoxShadows _hostBoxShadow;

    public SettingsView()
    {
        InitializeComponent();
        foreach (var slider in new[] {BlurSlider, OpacitySlider, GammaSlider, LeftOpacitySlider, RightOpacitySlider})
        {
            // Tunnel + handledEventsToo: the Slider/Thumb handle the press themselves
            slider.AddHandler(PointerPressedEvent, Slider_OnPointerPressed, RoutingStrategies.Tunnel, true);
            slider.AddHandler(PointerReleasedEvent, Slider_OnPointerReleased, RoutingStrategies.Tunnel, true);
        }
    }

    private void Slider_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual slider || !e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed) return;
        BeginSliderDrag();
    }

    private void Slider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndSliderDrag();
    }

    // The pointer may be released outside of the slider (no capture on a track click) or the window may lose focus
    private void TopLevel_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndSliderDrag();
    }

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        EndSliderDrag();
    }

    /// <summary>
    ///     No blur, transparent dialog card and overlay, the view itself at 5% opacity.
    ///     Values are changed with SetCurrentValue so the bindings/styles of the root DialogHost stay intact.
    /// </summary>
    private void BeginSliderDrag()
    {
        if (_isSliderDragging) return;
        _isSliderDragging = true;

        var host = DialogService.Host;
        if (host != null)
        {
            _hostBackground = host.Background;
            _hostOverlayBackground = host.OverlayBackground;
            _hostBorderBrush = host.GetValue(DialogHostStyle.BorderBrushProperty);
            _hostBoxShadow = host.GetValue(DialogHostStyle.BoxShadowProperty);

            host.SetCurrentValue(DialogHost.BlurBackgroundProperty, false);
            host.SetCurrentValue(DialogHost.BackgroundProperty, Brushes.Transparent);
            host.SetCurrentValue(DialogHost.OverlayBackgroundProperty, Brushes.Transparent);
            host.SetCurrentValue(DialogHostStyle.BorderBrushProperty, Brushes.Transparent);
            host.SetCurrentValue(DialogHostStyle.BoxShadowProperty, default(BoxShadows));
        }

        _dragTopLevel = TopLevel.GetTopLevel(this);
        _dragTopLevel?.AddHandler(PointerReleasedEvent, TopLevel_OnPointerReleased, RoutingStrategies.Tunnel, true);
        if (_dragTopLevel is WindowBase window)
            window.Deactivated += Window_OnDeactivated;

        Opacity = DRAG_OPACITY;
    }

    /// <summary>
    ///     Restores the dialog host look.
    /// </summary>
    private void EndSliderDrag()
    {
        if (!_isSliderDragging) return;
        _isSliderDragging = false;

        if (_dragTopLevel != null)
        {
            _dragTopLevel.RemoveHandler(PointerReleasedEvent, TopLevel_OnPointerReleased);
            if (_dragTopLevel is WindowBase window)
                window.Deactivated -= Window_OnDeactivated;
            _dragTopLevel = null;
        }

        var host = DialogService.Host;
        if (host != null)
        {
            host.SetCurrentValue(DialogHost.BlurBackgroundProperty, Settings.Instance.ApplyBlurBackground);
            host.SetCurrentValue(DialogHost.BackgroundProperty, _hostBackground);
            host.SetCurrentValue(DialogHost.OverlayBackgroundProperty, _hostOverlayBackground);
            host.SetCurrentValue(DialogHostStyle.BorderBrushProperty, _hostBorderBrush);
            host.SetCurrentValue(DialogHostStyle.BoxShadowProperty, _hostBoxShadow);
        }

        Opacity = 1.0;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Dialog closed in the middle of a drag: never leave the root host transparent
        EndSliderDrag();
        base.OnDetachedFromVisualTree(e);
    }
}
