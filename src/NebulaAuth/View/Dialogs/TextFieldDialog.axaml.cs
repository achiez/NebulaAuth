using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NebulaAuth.View.Dialogs;

public partial class TextFieldDialog : UserControl
{
    public TextFieldDialog()
    {
        InitializeComponent();
    }

    public TextFieldDialog(string? title, string? msg)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(title))
            TitleHeader.Title = title;

        if (!string.IsNullOrEmpty(msg))
            TextField.PlaceholderText = msg;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Focus after the open animation has attached the popup host
        Dispatcher.UIThread.Post(() => TextField.Focus(), DispatcherPriority.Input);
    }
}
