using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NebulaAuth.ViewModel.NebulaLink;

namespace NebulaAuth.View;

public partial class NebulaLinkView : UserControl
{
    private const double COMPACT_WIDTH = 440;
    private const double WIDE_WIDTH = 1040;
    private const double WIDE_HEIGHT = 620;

    // The accounts page shows every column from this modal width on (ManagePageVM.IsNarrow)
    private const double WIDE_LAYOUT = 860;

    // Card margin from the window edges (dialog positioner: 16 on each side)
    private const double WINDOW_GAP = 32;

    // The window content starts below the title bar band
    private const double TITLE_BAR = 30;

    private TopLevel? _topLevel;
    private NebulaLinkVM? _vm;

    /// <summary>Size and position of the main window before "Expand" (restored by "Collapse" and on close).</summary>
    private (double Width, double Height, PixelPoint Position)? _restore;

    public NebulaLinkView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null) _topLevel.PropertyChanged += TopLevelOnPropertyChanged;
        Fit();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Collapse();
        if (_topLevel != null) _topLevel.PropertyChanged -= TopLevelOnPropertyChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void TopLevelOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == TopLevel.ClientSizeProperty) Fit();
    }

    /// <summary>
    ///     Pairing and state pages: a compact card as tall as its content. Accounts table: about 1040x620. Never larger
    ///     than the window (the pages scroll, the actions stay visible).
    /// </summary>
    private void Fit()
    {
        if (_topLevel == null) return;
        var size = _topLevel.ClientSize;
        var maxWidth = Math.Floor(Math.Max(340, size.Width - WINDOW_GAP));
        var maxHeight = Math.Floor(Math.Max(360, size.Height - WINDOW_GAP - TITLE_BAR));
        if (_vm?.IsManage == true)
        {
            Width = Math.Min(WIDE_WIDTH, maxWidth);
            Height = Math.Min(WIDE_HEIGHT, maxHeight);
            MaxHeight = double.PositiveInfinity;
        }
        else
        {
            Width = Math.Min(COMPACT_WIDTH, maxWidth);
            Height = double.NaN;
            MaxHeight = maxHeight;
        }

        if (_vm != null) _vm.ModalWidth = Width;
        var manage = _vm?.IsManage == true;
        ExpandButton.IsVisible = manage && _restore == null && Width < WIDE_LAYOUT && ExpandedWidth() is not null;
        CollapseButton.IsVisible = manage && _restore != null;
    }

    /// <summary>Client width of the main window for the wide accounts table (within the screen); null when it can't grow enough.</summary>
    private double? ExpandedWidth()
    {
        if (_topLevel is not Window {WindowState: WindowState.Normal} window) return null;
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen == null) return null;
        var frame = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
        var available = screen.WorkingArea.Width / screen.Scaling - frame;
        var target = Math.Min(WIDE_WIDTH + WINDOW_GAP, Math.Floor(available));
        return target >= WIDE_LAYOUT + WINDOW_GAP && target > window.ClientSize.Width ? target : null;
    }

    private void ExpandOnClick(object? sender, RoutedEventArgs e)
    {
        if (_topLevel is not Window window || ExpandedWidth() is not { } width) return;
        _restore = (window.Width, window.Height, window.Position);
        window.Width = width;
        // Keep the widened window inside the working area of its screen
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen != null)
        {
            var outer = (int) Math.Ceiling((window.FrameSize?.Width ?? width) * screen.Scaling);
            var right = screen.WorkingArea.Right;
            if (window.Position.X + outer > right)
                window.Position = new PixelPoint(Math.Max(screen.WorkingArea.X, right - outer), window.Position.Y);
        }

        Fit();
    }

    private void CollapseOnClick(object? sender, RoutedEventArgs e)
    {
        Collapse();
        Fit();
    }

    /// <summary>Restores the main window size of before "Expand" (no-op when it was not expanded).</summary>
    private void Collapse()
    {
        if (_restore is not { } restore || _topLevel is not Window window) return;
        _restore = null;
        window.Width = restore.Width;
        window.Height = restore.Height;
        window.Position = restore.Position;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.LayoutChanged -= Fit;
        _vm = DataContext as NebulaLinkVM;
        if (_vm != null) _vm.LayoutChanged += Fit;
        Fit();
    }
}
