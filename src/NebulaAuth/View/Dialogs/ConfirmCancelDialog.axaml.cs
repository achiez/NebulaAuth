using Avalonia.Controls;
using Material.Icons;

namespace NebulaAuth.View.Dialogs;

/// <summary>
///     Content of <see cref="ConfirmCancelDialog" />. Every part is optional.
/// </summary>
/// <param name="Title">Header title (default: "Confirm action").</param>
/// <param name="TitleAccent">Bold part after the title (e.g. the account name).</param>
/// <param name="Icon">Header icon.</param>
/// <param name="Description">Main text: what the action does.</param>
/// <param name="Hint">Callout under the description: consequence or reassurance.</param>
/// <param name="HintSeverity">Callout color: Warning for reversible actions, Error for irreversible ones.</param>
/// <param name="ConfirmText">Confirm button text (default: OK).</param>
/// <param name="IsDanger">Red confirm button (destructive, irreversible action).</param>
public record ConfirmDialogOptions(
    string? Title = null,
    string? TitleAccent = null,
    MaterialIconKind? Icon = null,
    string? Description = null,
    string? Hint = null,
    HintBoxSeverity HintSeverity = HintBoxSeverity.Info,
    string? ConfirmText = null,
    bool IsDanger = false);

public partial class ConfirmCancelDialog : UserControl
{
    public ConfirmCancelDialog()
    {
        InitializeComponent();
    }

    /// <summary>Single message shown as an info callout.</summary>
    public ConfirmCancelDialog(string msg)
    {
        InitializeComponent();
        ConfirmHint.Text = msg;
    }

    public ConfirmCancelDialog(ConfirmDialogOptions options)
    {
        InitializeComponent();
        if (options.Title != null) Header.Title = options.Title;
        Header.TitleAccent = options.TitleAccent;
        Header.IconKind = options.Icon;
        DescriptionText.Text = options.Description;
        DescriptionText.IsVisible = !string.IsNullOrEmpty(options.Description);
        ConfirmHint.Text = options.Hint;
        ConfirmHint.Severity = options.HintSeverity;
        if (options.ConfirmText != null) ConfirmButton.Content = options.ConfirmText;
        if (options.IsDanger)
        {
            // Red button with a readable text color in every theme (see the "OnDanger" style)
            ConfirmButton.Classes.Remove("Primary");
            ConfirmButton.Classes.Add("Danger");
            ConfirmButton.Classes.Add("OnDanger");
        }
    }
}
