using System;
using Avalonia.Input;

namespace NebulaAuth.Utility;

/// <summary>
///     Checks of a typed password for the hints under password fields. Never stores or logs the password.
/// </summary>
public static class PasswordInputCheck
{
    /// <summary>
    ///     True when the text contains a letter outside ASCII (e.g. Cyrillic): usually a wrong keyboard layout.
    /// </summary>
    public static bool HasNonLatinLetters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (c > 127 && char.IsLetter(c)) return true;
        }

        return false;
    }
}

/// <summary>
///     Caps Lock state guessed from typing: Avalonia reports only Alt / Control / Shift / Meta
///     (<see cref="KeyModifiers" />) and no lock-key state, so the case of a typed letter is compared with the Shift key.
///     An uppercase letter without Shift (or a lowercase one with Shift) means Caps Lock is on; the opposite means off.
///     <see cref="State" /> is null until a cased letter was typed. Pressing Caps Lock flips a known state.
/// </summary>
public sealed class CapsLockTracker
{
    private bool _shift;

    public bool? State { get; private set; }

    /// <summary>Raised when <see cref="State" /> changes.</summary>
    public event EventHandler? StateChanged;

    public void OnKeyDown(Key key, KeyModifiers modifiers)
    {
        _shift = modifiers.HasFlag(KeyModifiers.Shift) || key is Key.LeftShift or Key.RightShift;
        if (key == Key.CapsLock && State is { } known) SetState(!known);
    }

    public void OnKeyUp(Key key, KeyModifiers modifiers)
    {
        _shift = modifiers.HasFlag(KeyModifiers.Shift) && key is not (Key.LeftShift or Key.RightShift);
    }

    public void OnTextInput(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var c in text)
        {
            if (!char.IsLetter(c) || char.ToUpperInvariant(c) == char.ToLowerInvariant(c)) continue;
            // macOS: Shift + Caps Lock types uppercase letters, so a letter typed with Shift says nothing there
            if (_shift && OperatingSystem.IsMacOS()) return;
            SetState(char.IsUpper(c) != _shift);
            return;
        }
    }

    public void Reset()
    {
        _shift = false;
        SetState(null);
    }

    private void SetState(bool? state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
