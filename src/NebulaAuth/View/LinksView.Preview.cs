#if DEBUG
using Avalonia.Threading;

namespace NebulaAuth.View;

public partial class LinksView
{
    /// <summary>
    ///     Debug preview hook: NEBULA_PREVIEW=NebulaAuth.View.LinksView. No design-time state is needed: the
    ///     Website/Documentation buttons follow the real LinksManager fetch.
    ///     NEBULA_PREVIEW_ARG=offline forces the state where links.json was not fetched (offline, blocked, or the view
    ///     opened before the fetch finished): both buttons disabled.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var view = new LinksView();
        if (arg == "offline")
        {
            // Posted so it runs after OnLoaded has applied the LinksManager state
            view.Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                view.WebsiteButton.IsEnabled = false;
                view.DocumentationButton.IsEnabled = false;
            });
        }

        return view;
    }
}
#endif
