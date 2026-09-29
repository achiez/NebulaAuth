using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NebulaAuth.Core;
using NebulaAuth.Model;

namespace NebulaAuth.Theme;

/// <summary>
///     Debug-only showcase of the style system (see StyleGallery.axaml).
/// </summary>
public partial class StyleGallery : UserControl
{
    public StyleGallery()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Items of the DataTemplate sample ({Tr} inside a DataTemplate).
    /// </summary>
    public string[] SampleKeys { get; } = ["first", "second"];

    private void Theme_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button {Tag: string tag})
        {
            Settings.Instance.ThemeType = (ThemeType) int.Parse(tag);
        }
    }

    private void Language_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button {Tag: string tag})
        {
            var language = (LocalizationLanguage) int.Parse(tag);
            Settings.Instance.Language = language;
            LocManager.SetApplicationLocalization(language);
        }
    }

    private async void Confirm_OnClick(object? sender, RoutedEventArgs e)
    {
        var result = await DialogsController.ShowConfirmCancelDialog("Remove the selected mafile?");
        SnackbarController.SendSnackbar("ConfirmCancelDialog result: " + result);
    }

    private async void TextField_OnClick(object? sender, RoutedEventArgs e)
    {
        var result = await DialogsController.ShowTextFieldDialog(null, "Group name");
        SnackbarController.SendSnackbar("TextFieldDialog result: " + (result ?? "null"));
    }

    private void Snackbar_OnClick(object? sender, RoutedEventArgs e)
    {
        SnackbarController.SendSnackbar("Code copied " + DateTime.Now.ToLongTimeString());
    }

    private void SnackbarAction_OnClick(object? sender, RoutedEventArgs e)
    {
        SnackbarController.SendSnackbarWithButton("Update available: 1.9.0", "Update",
            () => SnackbarController.SendSnackbar("Action clicked"));
    }
}
