using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NebulaAuth.View;

public partial class MafileExporterView : UserControl
{
    public MafileExporterView()
    {
        InitializeComponent();
        // Tunnel: also runs when the TextBox handles the click itself
        AccountsTB.AddHandler(PointerReleasedEvent, AccountsTB_PointerReleased, RoutingStrategies.Tunnel);
        // Raised after the TextBox selected the word, so SelectAll wins
        PathTB.AddHandler(DoubleTappedEvent, PathTB_DoubleClick, RoutingStrategies.Bubble, true);
        // Enter (KeyBinding) leaves the edit mode and hides the focused name box: give the focus back to the selector
        EditTemplateNameTextBox.AddHandler(KeyDownEvent, EditTemplateNameTextBox_KeyDown, RoutingStrategies.Bubble, true);
        // The body scrolls when the settings are expanded: keep a new export result / error visible.
        // The HintBox is the last element of the body: scroll to the end so the body's bottom margin stays visible too
        ResultHint.PropertyChanged += (_, e) =>
        {
            if (e.Property != HintBox.TextProperty || ResultHint.Text == null) return;
            Dispatcher.UIThread.Post(() => BodyScroll.ScrollToEnd(), DispatcherPriority.Background);
        };
    }

    private void PathTB_DoubleClick(object? sender, TappedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void AccountsTB_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        TemplateExpander.IsExpanded = false;
    }

    private void AccountsTB_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        TemplateExpander.IsExpanded = false;
    }

    private void EditButton_Click(object? sender, RoutedEventArgs e)
    {
        // Runs after the command has switched the edit mode; Focus fails (no-op) when the name box got hidden
        Dispatcher.UIThread.Post(() =>
        {
            if (EditTemplateNameTextBox.Focus())
            {
                EditTemplateNameTextBox.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void EditTemplateNameTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || EditTemplateNameTextBox.IsVisible) return;
        Dispatcher.UIThread.Post(() => TemplatesComboBox.Focus(), DispatcherPriority.Input);
    }
}
