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
    }

    private void Telegram_OnClick(object? sender, RoutedEventArgs e)
    {
        PlatformUtils.OpenUrl("https://t.me/nebulaauth");
    }

    private void GitHub_OnClick(object? sender, RoutedEventArgs e)
    {
        PlatformUtils.OpenUrl("https://github.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies");
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
