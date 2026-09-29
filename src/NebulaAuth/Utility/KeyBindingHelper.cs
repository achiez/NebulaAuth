using System;
using System.Linq;
using Avalonia;
using Avalonia.Input;

namespace NebulaAuth.Utility;

public static class KeyBindingHelper
{
    /// <summary>
    ///     Replaces Ctrl in <paramref name="gesture" /> with the platform command modifier (Cmd on macOS).
    ///     XAML gestures are written for Windows/Linux, where "Ctrl" is the command modifier.
    /// </summary>
    public static KeyGesture? WithCommandModifier(KeyGesture? gesture, KeyModifiers commandModifiers)
    {
        if (gesture == null || commandModifiers == KeyModifiers.Control ||
            !gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
            return gesture;
        return new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Control) | commandModifiers);
    }

    /// <summary>
    ///     macOS only: the "delete" key of Mac keyboards sends Backspace (Delete needs Fn). Adds a Backspace binding with
    ///     the same command and parameter next to every plain Delete binding of <paramref name="element" />.
    ///     Use it only where Backspace has no other meaning (not in editable text).
    /// </summary>
    public static void AddMacBackspaceForDelete(InputElement element)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var deleteBindings = element.KeyBindings
            .Where(b => b.Gesture is {Key: Key.Delete, KeyModifiers: KeyModifiers.None})
            .ToList();
        foreach (var binding in deleteBindings)
        {
            var alias = new KeyBinding {Gesture = new KeyGesture(Key.Back)};
            alias.Bind(KeyBinding.CommandProperty, binding.GetObservable(KeyBinding.CommandProperty));
            alias.Bind(KeyBinding.CommandParameterProperty, binding.GetObservable(KeyBinding.CommandParameterProperty));
            element.KeyBindings.Add(alias);
        }
    }
}
