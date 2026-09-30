using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using NebulaAuth.Utility;

namespace NebulaAuth;

/// <summary>
///     Hints under a password field (<see cref="Target" />): "Caps Lock is on" while the field has the keyboard focus and
///     Caps Lock seems to be on (<see cref="CapsLockTracker" />), and "non-Latin characters" while the text contains
///     letters outside ASCII (<see cref="CheckLayout" />). Informational only; the control hides itself (IsVisible) while
///     there is nothing to show. The password is only inspected, never stored. Look: Components/Components.axaml.
///     <code>&lt;nebulaAuth:PasswordHints Target="{Binding #PasswordBox}" /&gt;</code>
/// </summary>
public partial class PasswordHints : TemplatedControl
{
    public static readonly StyledProperty<TextBox?> TargetProperty =
        AvaloniaProperty.Register<PasswordHints, TextBox?>(nameof(Target));

    public static readonly StyledProperty<bool> CheckLayoutProperty =
        AvaloniaProperty.Register<PasswordHints, bool>(nameof(CheckLayout), true);

    public static readonly StyledProperty<bool> IsSuppressedProperty =
        AvaloniaProperty.Register<PasswordHints, bool>(nameof(IsSuppressed));

    public static readonly DirectProperty<PasswordHints, bool> IsCapsLockOnProperty =
        AvaloniaProperty.RegisterDirect<PasswordHints, bool>(nameof(IsCapsLockOn), o => o.IsCapsLockOn);

    public static readonly DirectProperty<PasswordHints, bool> HasNonLatinProperty =
        AvaloniaProperty.RegisterDirect<PasswordHints, bool>(nameof(HasNonLatin), o => o.HasNonLatin);

    private readonly CapsLockTracker _capsLock = new();
    private TextBox? _attached;
    private bool _isCapsLockOn;
    private bool _hasNonLatin;
    private bool _debugForceCapsLock;
    private bool _isInTree;

    public PasswordHints()
    {
        _capsLock.StateChanged += (_, _) => Update();
        DebugInit(ref _debugForceCapsLock);
        SetCurrentValue(IsVisibleProperty, false);
    }

    /// <summary>The password field.</summary>
    public TextBox? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Show the non-Latin hint (off for a "repeat the password" field under the same hint).</summary>
    public bool CheckLayout
    {
        get => GetValue(CheckLayoutProperty);
        set => SetValue(CheckLayoutProperty, value);
    }

    /// <summary>Hides the hints (e.g. while an error message already names them); the state is still tracked.</summary>
    public bool IsSuppressed
    {
        get => GetValue(IsSuppressedProperty);
        set => SetValue(IsSuppressedProperty, value);
    }

    /// <summary>Caps Lock seems to be on while the field has the focus.</summary>
    public bool IsCapsLockOn
    {
        get => _isCapsLockOn;
        private set => SetAndRaise(IsCapsLockOnProperty, ref _isCapsLockOn, value);
    }

    /// <summary>The text contains letters outside ASCII.</summary>
    public bool HasNonLatin
    {
        get => _hasNonLatin;
        private set => SetAndRaise(HasNonLatinProperty, ref _hasNonLatin, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty && _isInTree) Attach(Target);
        else if (change.Property == CheckLayoutProperty || change.Property == IsSuppressedProperty) Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isInTree = true;
        Attach(Target);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isInTree = false;
        Attach(null);
    }

    private void Attach(TextBox? target)
    {
        if (ReferenceEquals(_attached, target)) return;
        if (_attached != null)
        {
            _attached.RemoveHandler(KeyDownEvent, Target_OnKeyDown);
            _attached.RemoveHandler(KeyUpEvent, Target_OnKeyUp);
            _attached.RemoveHandler(TextInputEvent, Target_OnTextInput);
            _attached.PropertyChanged -= Target_OnPropertyChanged;
        }

        _attached = target;
        _capsLock.Reset();
        if (target != null)
        {
            // Tunnel + handledEventsToo: the TextBox handles the keys and the text input itself
            target.AddHandler(KeyDownEvent, Target_OnKeyDown, RoutingStrategies.Tunnel, true);
            target.AddHandler(KeyUpEvent, Target_OnKeyUp, RoutingStrategies.Tunnel, true);
            target.AddHandler(TextInputEvent, Target_OnTextInput, RoutingStrategies.Tunnel, true);
            target.PropertyChanged += Target_OnPropertyChanged;
        }

        Update();
    }

    private void Target_OnKeyDown(object? sender, KeyEventArgs e)
    {
        _capsLock.OnKeyDown(e.Key, e.KeyModifiers);
    }

    private void Target_OnKeyUp(object? sender, KeyEventArgs e)
    {
        _capsLock.OnKeyUp(e.Key, e.KeyModifiers);
    }

    private void Target_OnTextInput(object? sender, TextInputEventArgs e)
    {
        _capsLock.OnTextInput(e.Text);
    }

    private void Target_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            // A cleared field starts over (the next typed letter tells the state again)
            if (string.IsNullOrEmpty(_attached?.Text)) _capsLock.Reset();
            Update();
        }
        else if (e.Property == IsKeyboardFocusWithinProperty)
        {
            // Caps Lock may be toggled while the focus is elsewhere: forget the guess
            if (_attached is {IsKeyboardFocusWithin: false}) _capsLock.Reset();
            Update();
        }
    }

    private void Update()
    {
        var target = _attached;
        IsCapsLockOn = target is {IsKeyboardFocusWithin: true} && (_capsLock.State == true || _debugForceCapsLock);
        HasNonLatin = target != null && CheckLayout && PasswordInputCheck.HasNonLatinLetters(target.Text);
        SetCurrentValue(IsVisibleProperty, !IsSuppressed && (IsCapsLockOn || HasNonLatin));
    }

    partial void DebugInit(ref bool forceCapsLock);
}
