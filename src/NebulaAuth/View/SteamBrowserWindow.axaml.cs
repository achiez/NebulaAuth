using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;

namespace NebulaAuth.View;

/// <summary>
///     Window of the embedded Steam browser. Only presentation: the session (Core/Browser/BrowserSession) owns the web
///     view, drives navigation and decides when the page is revealed.
/// </summary>
public partial class SteamBrowserWindow : Window
{
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(8);
    private IDisposable? _noticeTimer;
    private Control? _webView;

    public SteamBrowserWindow()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => NavigateBackRequested?.Invoke();
        ForwardButton.Click += (_, _) => NavigateForwardRequested?.Invoke();
        ReloadButton.Click += (_, _) => NavigateReloadRequested?.Invoke();
        CloseButton.Click += (_, _) => Close();
    }

    public event Action? NavigateBackRequested;
    public event Action? NavigateForwardRequested;
    public event Action? NavigateReloadRequested;

    public bool IsWebViewRevealed { get; private set; }

    public void SetAccount(string accountName)
    {
        AccountText.Text = accountName;
        Title = string.Format(Core.LocManager.GetOrDefault("{0} — Steam", "SteamBrowser", "WindowTitle"),
            accountName);
    }

    public void SetLoading(string title, string status)
    {
        LoadingTitle.Text = title;
        LoadingStatus.Text = status;
    }

    /// <summary>
    ///     Adds the web view below the loading panel, kept at 1x1 px in the corner until <see cref="RevealWebView" />.
    /// </summary>
    public void AttachWebView(Control webView)
    {
        _webView = webView;
        webView.Width = 1;
        webView.Height = 1;
        webView.HorizontalAlignment = HorizontalAlignment.Left;
        webView.VerticalAlignment = VerticalAlignment.Top;
        ContentHost.Children.Insert(0, webView);
    }

    public void RevealWebView()
    {
        if (_webView == null) return;
        LoadingPanel.IsVisible = false;
        _webView.Width = double.NaN;
        _webView.Height = double.NaN;
        _webView.HorizontalAlignment = HorizontalAlignment.Stretch;
        _webView.VerticalAlignment = VerticalAlignment.Stretch;
        IsWebViewRevealed = true;
        ReloadButton.IsEnabled = true;
        _webView.Focus();
    }

    /// <summary>Removes the web view from the window (the native engine is released by the control).</summary>
    public void DetachWebView()
    {
        if (_webView == null) return;
        ContentHost.Children.Remove(_webView);
        _webView = null;
        IsWebViewRevealed = false;
    }

    public void SetNavigationState(string? address, bool canGoBack, bool canGoForward)
    {
        AddressBox.Text = address ?? "";
        AddressIcon.Kind = address != null && address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? MaterialIconKind.Lock
            : MaterialIconKind.Web;
        BackButton.IsEnabled = IsWebViewRevealed && canGoBack;
        ForwardButton.IsEnabled = IsWebViewRevealed && canGoForward;
    }

    public void ShowNotice(string text)
    {
        NoticeBox.Text = text;
        NoticeBox.IsVisible = true;
        _noticeTimer?.Dispose();
        _noticeTimer = DispatcherTimer.RunOnce(() => NoticeBox.IsVisible = false, NoticeDuration);
    }
}
