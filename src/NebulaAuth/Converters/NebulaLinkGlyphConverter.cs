using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using NebulaAuth.Model.Entities;
using NebulaAuth.NebulaLink;
using NebulaAuth.ViewModel.NebulaLink;
using NebulaAuth.ViewModel.NebulaLink.Pages;

namespace NebulaAuth.Converters;

/// <summary>
///     MultiBinding [Mafile, MainVM.LinkAccountStates] → the Nebula Link icon of an account in the account list: the same
///     link icon in the accent color (ready), orange (needs a Steam login) or red (error: saved password rejected, mafile
///     missing...). <see cref="Tip" /> returns the tooltip text.
/// </summary>
public sealed class NebulaLinkGlyphConverter : IMultiValueConverter
{
    public static readonly NebulaLinkGlyphConverter Ok = new(Mode.Ok);
    public static readonly NebulaLinkGlyphConverter Warn = new(Mode.Warn);
    public static readonly NebulaLinkGlyphConverter Bad = new(Mode.Bad);
    public static readonly NebulaLinkGlyphConverter Tip = new(Mode.Tip);

    private readonly Mode _mode;

    private NebulaLinkGlyphConverter(Mode mode)
    {
        _mode = mode;
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not Mafile mafile ||
            values[1] is not IReadOnlyDictionary<string, string> states ||
            !states.TryGetValue(AccountMatcher.Normalize(mafile.AccountName), out var state))
            return _mode == Mode.Tip ? null : false;
        var kind = AccountRowVM.KindOf(state);
        return _mode switch
        {
            Mode.Tip => kind == RowStatusKind.Ok
                ? NebulaLinkText.Get("Connected to Nebula Link", "RowLinked")
                : "Nebula Link: " + NebulaLinkText.AccountState(state),
            Mode.Ok => kind == RowStatusKind.Ok,
            Mode.Warn => kind == RowStatusKind.Warn,
            _ => kind == RowStatusKind.Bad
        };
    }

    private enum Mode
    {
        Ok,
        Warn,
        Bad,
        Tip
    }
}
