using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace NebulaAuth;

/// <summary>
///     Callout with a severity icon, a text and an optional close button. The box renders nothing while
///     <see cref="Text" /> is null. Look: Components/Components.axaml.
/// </summary>
public class HintBox : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HintBox, string?>(nameof(Text));

    public static readonly StyledProperty<HintBoxSeverity> SeverityProperty =
        AvaloniaProperty.Register<HintBox, HintBoxSeverity>(nameof(Severity));

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<HintBox, bool>(nameof(ShowCloseButton));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<HintBox, ICommand?>(nameof(CloseCommand));

    public static readonly DirectProperty<HintBox, MaterialIconKind> IconKindProperty =
        AvaloniaProperty.RegisterDirect<HintBox, MaterialIconKind>(nameof(IconKind), o => o.IconKind);

    public static readonly DirectProperty<HintBox, ICommand> EffectiveCloseCommandProperty =
        AvaloniaProperty.RegisterDirect<HintBox, ICommand>(nameof(EffectiveCloseCommand), o => o.EffectiveCloseCommand);

    public static readonly RoutedEvent<RoutedEventArgs> ClosedEvent =
        RoutedEvent.Register<HintBox, RoutedEventArgs>(nameof(Closed), RoutingStrategies.Bubble);

    private readonly ICommand _internalCloseCommand;
    private ICommand _effectiveCloseCommand;
    private MaterialIconKind _iconKind = MaterialIconKind.InfoCircleOutline;

    public HintBox()
    {
        _internalCloseCommand = new RelayCommand(Close);
        _effectiveCloseCommand = _internalCloseCommand;
        UpdateSeverityVisuals();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public HintBoxSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    /// <summary>
    ///     Command of the close button. When not set, the button hides the box and raises <see cref="Closed" />.
    /// </summary>
    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>
    ///     <see cref="CloseCommand" /> or the internal close command (bound by the template).
    /// </summary>
    public ICommand EffectiveCloseCommand
    {
        get => _effectiveCloseCommand;
        private set => SetAndRaise(EffectiveCloseCommandProperty, ref _effectiveCloseCommand, value);
    }

    /// <summary>
    ///     Icon matching <see cref="Severity" />.
    /// </summary>
    public MaterialIconKind IconKind
    {
        get => _iconKind;
        private set => SetAndRaise(IconKindProperty, ref _iconKind, value);
    }

    /// <summary>
    ///     Raised when the internal close command hid the box.
    /// </summary>
    public event System.EventHandler<RoutedEventArgs> Closed
    {
        add => AddHandler(ClosedEvent, value);
        remove => RemoveHandler(ClosedEvent, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeverityProperty)
        {
            UpdateSeverityVisuals();
        }
        else if (change.Property == CloseCommandProperty)
        {
            EffectiveCloseCommand = CloseCommand ?? _internalCloseCommand;
        }
    }

    private void UpdateSeverityVisuals()
    {
        IconKind = Severity switch
        {
            HintBoxSeverity.Error => MaterialIconKind.ErrorOutline,
            HintBoxSeverity.Warning => MaterialIconKind.WarningOutline,
            _ => MaterialIconKind.InfoCircleOutline
        };
        PseudoClasses.Set(":info", Severity == HintBoxSeverity.Info);
        PseudoClasses.Set(":warning", Severity == HintBoxSeverity.Warning);
        PseudoClasses.Set(":error", Severity == HintBoxSeverity.Error);
    }

    private void Close()
    {
        SetCurrentValue(IsVisibleProperty, false);
        RaiseEvent(new RoutedEventArgs(ClosedEvent, this));
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new HintBoxAutomationPeer(this);
    }

    /// <summary>
    ///     Exposes the hint text to screen readers and UI Automation.
    /// </summary>
    private sealed class HintBoxAutomationPeer(HintBox owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            return ((HintBox) Owner).Text;
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.Text;
        }
    }
}
