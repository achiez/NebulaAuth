using System.Collections.Generic;
using AchiesUtilities.Web.Proxy;
using Avalonia.Threading;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class ProxyManagerView
{
    /// <summary>
    ///     Preview (NEBULA_PREVIEW=NebulaAuth.View.ProxyManagerView). When the storage is empty, adds fake 127.0.0.1
    ///     proxies in memory (the preview itself never saves them). NEBULA_PREVIEW_ARG: none = manager tab with a default
    ///     proxy and a selected row, "nodefault", "error" (parse error shown), "assign" (assignment tab with a result).
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        if (ProxyStorage.Proxies.Count == 0)
        {
            (ProxyProtocol protocol, int port, string? user, string? pass)[] fake =
            [
                (ProxyProtocol.HTTP, 8080, null, null),
                (ProxyProtocol.HTTP, 8081, "user1", "pass1"),
                (ProxyProtocol.SOCKS5, 1080, "alpha", "s3cret"),
                (ProxyProtocol.HTTP, 3128, null, null),
                (ProxyProtocol.HTTPs, 8443, "admin", "hunter2"),
                (ProxyProtocol.SOCKS4, 1081, null, null),
                (ProxyProtocol.HTTP, 8082, "user2", "pass2"),
                (ProxyProtocol.SOCKS5, 1082, "bravo", "p4ss"),
                (ProxyProtocol.HTTP, 8083, null, null),
                (ProxyProtocol.HTTP, 8084, "user3", "pass3"),
                (ProxyProtocol.SOCKS5, 1083, null, null),
                (ProxyProtocol.HTTP, 8085, "user4", "pass4")
            ];
            for (var i = 0; i < fake.Length; i++)
            {
                var (protocol, port, user, pass) = fake[i];
                ProxyStorage.Proxies.Add(i, new ProxyData(protocol, "127.0.0.1", port, user!, pass!));
            }

            // Account counters of the rows (in-memory cache only)
            ProxyAssignmentCache.UpdateAssignment("preview_a", null, 1);
            ProxyAssignmentCache.UpdateAssignment("preview_b", null, 1);
            ProxyAssignmentCache.UpdateAssignment("preview_c", null, 4);
        }

        var vm = new ProxyManagerVM();
        if (arg != "nodefault" && vm.DefaultProxy == null && ProxyStorage.Proxies.TryGetValue(2, out var def))
            vm.DefaultProxy = KeyValuePair.Create(2, def);

        if (arg == "error")
        {
            vm.AddProxyField = "127.0.0.1:9000\r\n127.0.0.1:port";
            vm.ErrorText = string.Format(
                LocManager.GetCodeBehindOrDefault("WrongFormatOnLine", "ProxyManagerVM", "WrongFormatOnLine"), 2);
        }

        var view = new ProxyManagerView {DataContext = vm};
        if (arg == "assign")
        {
            view.Tabs.SelectedIndex = 1;
            vm.AssignmentInput =
                "alphaecho:127.0.0.1:8080\r\nbravo:socks5://user:pass@127.0.0.1:1080\r\ncharlie:3\r\ndelta";
            vm.AssignmentTip = string.Format(
                LocManager.GetCodeBehindOrDefault("AssignmentPartialSuccess", "ProxyManagerVM",
                    "AssignmentPartialSuccess"), 3, 1, 0);
        }
        else
        {
            // Select a row once the list has its containers
            view.Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (ProxyStorage.Proxies.TryGetValue(1, out var p))
                    vm.SelectedProxy = KeyValuePair.Create(1, p);
            }, DispatcherPriority.Background);
        }

        return view;
    }
}
