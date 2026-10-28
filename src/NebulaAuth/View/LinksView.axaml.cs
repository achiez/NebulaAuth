using Avalonia.Controls;
using Avalonia.Interactivity;
using NebulaAuth.Core;
using NebulaAuth.Utility;

namespace NebulaAuth.View;

public partial class LinksView : UserControl
{
    public LinksView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        WebsiteButton.IsEnabled = LinksManager.WebsiteUrl != null;
        DocumentationButton.IsEnabled = LinksManager.DocumentationUrl != null;
        TelegramButton.IsEnabled = LinksManager.TelegramUrl != null;
        GitHubButton.IsEnabled = LinksManager.GitHubUrl != null;
    }

    private void Telegram_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LinksManager.TelegramUrl != null)
            PlatformUtils.OpenUrl(LinksManager.TelegramUrl);
    }

    private void GitHub_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LinksManager.GitHubUrl != null)
            PlatformUtils.OpenUrl(LinksManager.GitHubUrl);
    }

    private void Website_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LinksManager.WebsiteUrl != null)
            PlatformUtils.OpenUrl(LinksManager.WebsiteUrl);
    }

    private void Documentation_OnClick(object? sender, RoutedEventArgs e)
    {
        if (LinksManager.DocumentationUrl != null)
            PlatformUtils.OpenUrl(LinksManager.DocumentationUrl);
    }

    private void CheckForUpdates_OnClick(object? sender, RoutedEventArgs e)
    {
        DialogService.Close();
        UpdateManager.CheckForUpdates(true);
    }
}
