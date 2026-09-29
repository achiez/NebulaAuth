using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Model.Entities;
using NebulaAuth.Model.Mafiles;

namespace NebulaAuth.ViewModel;

public partial class MainVM
{
    public ObservableCollection<MaProxy> Proxies { get; }

    public MaProxy? SelectedProxy
    {
        get => _selectedProxy;
        set
        {
            // The ComboBox writes null back when the account's proxy is not in Proxies (deleted or edited);
            // that must not detach it. Detaching goes through RemoveProxyCommand.
            if (value == null) return;
            SetProxy(value, false);
        }
    }

    public bool IsDefaultProxy => SelectedProxy == null && MaClient.DefaultProxy != null && SelectedMafile != null;
    private bool _internalProxyRefreshInProgress;

    [ObservableProperty] private bool _proxyExist = true;
    private MaProxy? _selectedProxy;

    private void SetProxy(MaProxy? proxy, bool system)
    {
        if (_internalProxyRefreshInProgress)
        {
            return;
        }

        if (!SetProperty(ref _selectedProxy, proxy, nameof(SelectedProxy)))
        {
            return;
        }

        if (!system && SelectedMafile != null)
        {
            var oldProxyId = SelectedMafile.Proxy?.Id;
            SelectedMafile.Proxy = SelectedProxy;
            ProxyAssignmentCache.UpdateAssignment(SelectedMafile.AccountName, oldProxyId, SelectedProxy?.Id);
            Storage.UpdateMafile(SelectedMafile);
        }

        CheckProxyExist();
    }

    [RelayCommand]
    private async Task OpenProxyManager()
    {
        try
        {
            var anyChanges = await DialogsController.ShowProxyManager();
            if (!anyChanges) return;
            _internalProxyRefreshInProgress = true;
            Proxies.Clear();
            foreach (var kvp in ProxyStorage.Proxies)
            {
                Proxies.Add(new MaProxy(kvp.Key, kvp.Value));
            }
        }
        finally
        {
            _internalProxyRefreshInProgress = false;
        }

        // Re-sync from the account (bulk assignment may have changed it). Raising null first makes the ComboBox
        // select the equal item of the refilled list.
        var current = SelectedMafile?.Proxy;
        _selectedProxy = null;
        OnPropertyChanged(nameof(SelectedProxy));
        SetProxy(current, true);
        CheckProxyExist();
    }

    [RelayCommand(CanExecute = nameof(RemoveProxyCanExecute))]
    private void RemoveProxy(Mafile? mafile)
    {
        mafile ??= SelectedMafile;
        if (mafile?.Proxy == null) return;
        var oldProxyId = mafile.Proxy.Id;
        mafile.Proxy = null;
        ProxyAssignmentCache.UpdateAssignment(mafile.AccountName, oldProxyId, null);
        if (mafile == SelectedMafile)
            SetProxy(null, false); //Not system, triggered by user
    }

    private bool RemoveProxyCanExecute(Mafile? mafile)
    {
        mafile ??= SelectedMafile;
        return mafile is {Proxy: not null};
    }

    [RelayCommand(CanExecute = nameof(AssignFreeProxyCanExecute))]
    private void AssignFreeProxy(Mafile? mafile)
    {
        mafile ??= SelectedMafile;
        if (mafile == null) return;

        int? defaultProxyId = null;
        if (MaClient.DefaultProxy != null)
        {
            var defKvp = ProxyStorage.Proxies.FirstOrDefault(kvp => kvp.Value.Equals(MaClient.DefaultProxy));
            if (defKvp.Value != null)
                defaultProxyId = defKvp.Key;
        }

        var freeProxy = ProxyAssignmentCache.GetRandomFreeProxy(ProxyStorage.Proxies, defaultProxyId);
        if (freeProxy == null) return;

        var newProxy = new MaProxy(freeProxy.Value.Key, freeProxy.Value.Value);
        var oldProxyId = mafile.Proxy?.Id;
        mafile.Proxy = newProxy;
        ProxyAssignmentCache.UpdateAssignment(mafile.AccountName, oldProxyId, newProxy.Id);
        Storage.UpdateMafile(mafile);

        if (mafile == SelectedMafile)
            SetProxy(newProxy, true); // system=true: update UI only, assignment already saved
    }

    private bool AssignFreeProxyCanExecute(Mafile? mafile)
    {
        return ProxyStorage.Proxies.Count > 0;
    }

    private void CheckProxyExist()
    {
        if (SelectedProxy == null)
        {
            ProxyExist = true;
        }
        else
        {
            var selectedId = SelectedProxy.Id;
            ProxyExist = ProxyStorage.Proxies.TryGetValue(selectedId, out var existedProxy)
                         && SelectedProxy.Data
                             .Equals(
                                 existedProxy); //Id is not important in 'Equals()' as we extract it from the dictionary
        }

        OnPropertyChanged(nameof(ProxyExist));
        OnPropertyChanged(nameof(IsDefaultProxy));
    }
}