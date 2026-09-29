using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NebulaAuth.Core;

namespace NebulaAuth;

/// <summary>
///     Bottom notification bound to a <see cref="SnackbarMessageQueue" />.
///     The queue decides what is shown and for how long (one message at a time, duplicates discarded); the control
///     animates in/out on <see cref="SnackbarMessageQueue.IsActive" />, runs the action button and pauses the timer while
///     the pointer is over the message.
///     <code>&lt;nebulaAuth:Snackbar MessageQueue="{Binding MessageQueue}" /&gt;</code>
/// </summary>
public partial class Snackbar : UserControl
{
    public static readonly StyledProperty<SnackbarMessageQueue?> MessageQueueProperty =
        AvaloniaProperty.Register<Snackbar, SnackbarMessageQueue?>(nameof(MessageQueue));

    private bool _attached;
    private bool _paused;

    public Snackbar()
    {
        InitializeComponent();
        // Local null value: the card must not inherit the view model DataContext before MessageQueue is set
        Card.DataContext = null;
        Card.PointerEntered += Card_OnPointerEntered;
        Card.PointerExited += Card_OnPointerExited;
    }

    public SnackbarMessageQueue? MessageQueue
    {
        get => GetValue(MessageQueueProperty);
        set => SetValue(MessageQueueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MessageQueueProperty)
        {
            if (change.GetOldValue<SnackbarMessageQueue?>() is { } old && _paused)
            {
                old.Resume();
                _paused = false;
            }

            if (_attached)
            {
                change.GetOldValue<SnackbarMessageQueue?>()?.Detach();
                change.GetNewValue<SnackbarMessageQueue?>()?.Attach();
            }

            // The inner card binds to the queue; the control keeps the inherited DataContext for its own bindings
            Card.DataContext = change.GetNewValue<SnackbarMessageQueue?>();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        // The queue waits for an attached Snackbar before it starts a message timer
        MessageQueue?.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_paused)
        {
            MessageQueue?.Resume();
            _paused = false;
        }

        MessageQueue?.Detach();
    }

    private void ActionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        MessageQueue?.InvokeAction();
    }

    private void Card_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_paused || MessageQueue == null) return;
        MessageQueue.Pause();
        _paused = true;
    }

    private void Card_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_paused || MessageQueue == null) return;
        MessageQueue.Resume();
        _paused = false;
    }
}
