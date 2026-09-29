using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NebulaAuth.View;

/// <summary>
///     Keyboard handling of the step wizards (LinkerView, MafileMoverView): Enter runs the Next command of the current
///     step, and every new step gets the keyboard focus so Enter keeps reaching the view.
/// </summary>
internal static class WizardKeyboard
{
    /// <summary>
    ///     Class of the element focused when its step appears (steps without an input: Proceed / Finish / Copy R-code).
    ///     Steps without it focus their first TextBox.
    /// </summary>
    public const string INITIAL_FOCUS_CLASS = "initialFocus";

    public static void Attach(UserControl view, ContentControl stepHost, Func<ICommand?> nextCommand)
    {
        // Not a KeyBinding: Avalonia runs KeyBindings before the focused control, so a focused Cancel / Copy / close
        // button would lose Enter to Next. A bubbling handler only sees Enter that no control of the view has handled.
        view.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(e, nextCommand), RoutingStrategies.Bubble);
        // A closed ComboBox opens its drop-down on Enter and handles it, so the bubbling handler would never see it
        view.AddHandler(InputElement.KeyDownEvent, (_, e) => OnPreviewKeyDown(e, nextCommand),
            RoutingStrategies.Tunnel);
        view.Loaded += (_, _) => FocusStep(view, stepHost);
        stepHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty)
                FocusStep(view, stepHost);
        };
    }

    private static void OnPreviewKeyDown(KeyEventArgs e, Func<ICommand?> nextCommand)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        // Proxy selector focused with the drop-down closed: Enter = Next (Alt+Down / F4 still open the list)
        if (e.Source is ComboBox {IsDropDownOpen: false, IsEditable: false})
        {
            e.Handled = true;
            ExecuteNext(nextCommand);
        }
    }

    private static void OnKeyDown(KeyEventArgs e, Func<ICommand?> nextCommand)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;

        // Drop-down opened with the mouse (the focus stays on the ComboBox, which ignores Enter while it is open):
        // Enter closes it instead of proceeding
        if (e.Source is ComboBox {IsDropDownOpen: true} comboBox)
        {
            comboBox.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
            e.Handled = true;
            return;
        }

        // The R-code of the done step is selectable (focusable): Enter there must not close the only screen showing it
        if (e.Source is SelectableTextBlock) return;

        e.Handled = ExecuteNext(nextCommand);
    }

    private static bool ExecuteNext(Func<ICommand?> nextCommand)
    {
        if (nextCommand() is not { } command || !command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }

    /// <summary>
    ///     A new step replaces the focused field (the focus would leave the dialog and Enter would no longer reach the
    ///     view). Focus the element with <see cref="INITIAL_FOCUS_CLASS" />, otherwise the first input of the step.
    /// </summary>
    private static void FocusStep(UserControl view, ContentControl stepHost)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!view.IsLoaded) return;
            var step = stepHost.Content;
            var controls = stepHost.GetVisualDescendants()
                .OfType<Control>()
                .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
                .ToList();
            var target = controls.FirstOrDefault(c => c.Classes.Contains(INITIAL_FOCUS_CLASS))
                         ?? controls.OfType<TextBox>().FirstOrDefault();
            if (target == null) return;
            target.Focus();

            // Something may take the focus away right after the step switch (focus cleared or moved outside the view):
            // check again once the pending input and focus messages are processed
            Dispatcher.UIThread.Post(() =>
            {
                if (!view.IsLoaded || !ReferenceEquals(stepHost.Content, step)) return;
                var focused = TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as Visual;
                if (focused == null || !view.IsVisualAncestorOf(focused))
                    target.Focus();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Input);
    }
}
