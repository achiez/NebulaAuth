using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls.Primitives;
using Material.Icons;

namespace NebulaAuth;

/// <summary>
///     Header bar of in-window dialogs: optional icon, title (+ bold accent part) and a close button that closes the
///     dialog through <c>DialogService.CloseDialogCommand</c> with <see cref="CloseParameter" /> as the result.
///     Look: Components/Components.axaml.
/// </summary>
public class DialogHeader : TemplatedControl
{
    public static readonly StyledProperty<MaterialIconKind?> IconKindProperty =
        AvaloniaProperty.Register<DialogHeader, MaterialIconKind?>(nameof(IconKind));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TitleAccentProperty =
        AvaloniaProperty.Register<DialogHeader, string?>(nameof(TitleAccent));

    public static readonly StyledProperty<object?> CloseParameterProperty =
        AvaloniaProperty.Register<DialogHeader, object?>(nameof(CloseParameter));

    public static readonly StyledProperty<bool> IsCloseEnabledProperty =
        AvaloniaProperty.Register<DialogHeader, bool>(nameof(IsCloseEnabled), true);

    /// <summary>
    ///     Icon before the title; null (default) shows no icon.
    /// </summary>
    public MaterialIconKind? IconKind
    {
        get => GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    ///     Bold part displayed after <see cref="Title" /> (e.g. an account name).
    /// </summary>
    public string? TitleAccent
    {
        get => GetValue(TitleAccentProperty);
        set => SetValue(TitleAccentProperty, value);
    }

    /// <summary>
    ///     Result passed to <c>DialogService.Close</c> when the close button is clicked (default null).
    /// </summary>
    public object? CloseParameter
    {
        get => GetValue(CloseParameterProperty);
        set => SetValue(CloseParameterProperty, value);
    }

    public bool IsCloseEnabled
    {
        get => GetValue(IsCloseEnabledProperty);
        set => SetValue(IsCloseEnabledProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new DialogHeaderAutomationPeer(this);
    }

    /// <summary>
    ///     Exposes the title to screen readers and UI Automation (name = Title + TitleAccent).
    /// </summary>
    private sealed class DialogHeaderAutomationPeer(DialogHeader owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore()
        {
            var header = (DialogHeader) Owner;
            return string.IsNullOrEmpty(header.TitleAccent) ? header.Title : header.Title + " " + header.TitleAccent;
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.Header;
        }
    }
}
