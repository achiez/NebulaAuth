using System.Linq;
using NebulaAuth.Model.LinkHost;
using NebulaAuth.NebulaLink;
using NebulaAuth.ViewModel.NebulaLink.Demo;

namespace NebulaAuth.ViewModel;

public partial class MainVM
{
    /// <summary>
    ///     NEBULA_MAIN_DEMO=link-online / link-problems / link-paused / link-offline / link-revoked:
    ///     replaces Nebula Link with a fake one around the loaded accounts (accounts 1-4 connected, request history;
    ///     <paramref name="problems" />: a rejected password, a missing Steam login and a failed connect). No files, no
    ///     network. Opening the modal from the status bar shows the same data.
    /// </summary>
    internal void DemoNebulaLink(NebulaLinkState state, bool problems)
    {
        FakeNebulaLink.SetOrigins(true);
        var fake = FakeNebulaLink.CreateFor(MaFiles.ToList(), state, problems);
        NebulaLinkHost.Replace(fake);
        SubscribeNebulaLink();
    }
}
