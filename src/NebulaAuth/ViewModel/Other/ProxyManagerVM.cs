using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AchiesUtilities.Collections;
using AchiesUtilities.Web.Proxy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Utility;

namespace NebulaAuth.ViewModel.Other;

public partial class ProxyManagerVM : ObservableObject
{
    private const string LOCALIZATION_KEY = "ProxyManagerVM";

    private static readonly Regex IdRegex = new(@"\{(\d+)\}$", RegexOptions.Compiled);
    public ObservableDictionary<int, ProxyData> Proxies => ProxyStorage.Proxies;
    public bool AnyChanges { get; private set; }

    public bool DisplayProtocol
    {
        get => Settings.Instance.ProxyManagerDisplayProtocol;
        set
        {
            Settings.Instance.ProxyManagerDisplayProtocol = value;
            OnPropertyChanged();
        }
    }

    public bool DisplayCredentials
    {
        get => Settings.Instance.ProxyManagerDisplayCredentials;
        set
        {
            Settings.Instance.ProxyManagerDisplayCredentials = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _addProxyField = string.Empty;
    [ObservableProperty] private KeyValuePair<int, ProxyData>? _defaultProxy;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private KeyValuePair<int, ProxyData>? _selectedProxy;

    public ProxyManagerVM()
    {
        if (MaClient.DefaultProxy != null)
            DefaultProxy = Proxies.FirstOrDefault(kvp => kvp.Value.Equals(MaClient.DefaultProxy));
    }

    [RelayCommand]
    private void AddProxy()
    {
        AnyChanges = true;
        var input = AddProxyField;
        if (string.IsNullOrEmpty(input)) return;


        // Both line endings: Enter inserts Environment.NewLine, pasted text keeps its own (cross-platform)
        var split = input
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(x => x.Trim())
            .ToArray();

        if (split.Length == 0) return;

        bool? idPresent = null;
        var proxies = new List<KeyValuePair<int?, ProxyData>>();
        var i = 0;


        foreach (var s in split.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            i++;
            var str = s;
            int? id = null;
            var idMatch = IdRegex.Match(str);
            if (idMatch.Success)
            {
                id = int.Parse(idMatch.Groups[1].Value);
                str = IdRegex.Replace(str, "");
            }

            idPresent ??= idMatch.Success;
            if (idPresent.Value != idMatch.Success)
            {
                SetError(GetLocalizationOrDefault("WrongFormatSomeIdsMissing"));
                return;
            }


            if (ProxyStorage.DefaultScheme.TryParse(str, out var proxy) || ProxyStorage.SignAtScheme.TryParse(str, out proxy))
            {
                if (id != null && proxies.Any(kvp => kvp.Key == id))
                {
                    SetError(string.Format(GetLocalizationOrDefault("DuplicateId"), id));
                    return;
                }

                proxies.Add(KeyValuePair.Create(id, proxy));
            }
            else
            {
                if (split.Length == 1)
                {
                    SetError(GetLocalizationOrDefault("WrongFormat"));
                    return;
                }

                SetError(string.Format(GetLocalizationOrDefault("WrongFormatOnLine"), i));
                return;
            }
        }

        ProxyStorage.SetProxies(proxies);
        ProxyStorage.SortCollection();
        AddProxyField = string.Empty;
        SetError(null);
        CheckIfDefaultProxyStay();
    }

    private void SetError(string? err)
    {
        ErrorText = err;
    }

    [RelayCommand]
    public void ClearError()
    {
        SetError(null);
    }

    private void CheckIfDefaultProxyStay()
    {
        if (!DefaultProxy.HasValue || Proxies.Any(kvp => kvp.Equals(DefaultProxy.Value))) return;
        DefaultProxy = null;
        MaClient.DefaultProxy = null;
    }

    [RelayCommand]
    private void RemoveProxy(object? target)
    {
        var targetProxy = target as KeyValuePair<int, ProxyData>? ?? SelectedProxy;
        AnyChanges = true;

        if (targetProxy == null) return;
        var s = targetProxy.Value;


        KeyValuePair<int, ProxyData>? nextNeighbor = null;
        KeyValuePair<int, ProxyData>? prevNeighbor = null;
        foreach (var id in Proxies.Keys.Order())
        {
            if (id < s.Key)
            {
                prevNeighbor = KeyValuePair.Create(id, Proxies[id]);
            }
            else if (id > s.Key)
            {
                nextNeighbor = KeyValuePair.Create(id, Proxies[id]);
                break;
            }
        }

        ProxyStorage.RemoveProxy(s.Key);
        SelectedProxy = nextNeighbor ?? prevNeighbor;
        CheckIfDefaultProxyStay();
    }

    [RelayCommand]
    private void SetDefault(object? arg)
    {
        if (arg is not KeyValuePair<int, ProxyData> proxy) return;
        AnyChanges = true;
        DefaultProxy = proxy;
        MaClient.DefaultProxy = proxy.Value;
        ProxyStorage.Save();
    }

    [RelayCommand]
    private void RemoveDefault()
    {
        AnyChanges = true;
        DefaultProxy = null;
        MaClient.DefaultProxy = null;
        ProxyStorage.Save();
    }

    [RelayCommand]
    private void CopyProxy(ProxyData? data)
    {
        if (data == null) return;
        try
        {
            ClipboardHelper.Set(ProxyStorage.GetProxyString(data));
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex);
        }
    }

    [RelayCommand]
    private void CopyProxyAddress(ProxyData? obj)
    {
        if (obj == null) return;
        ClipboardHelper.Set(obj.ToString(ProxyStorage.ADDRESS_FORMAT));
    }

    private static string GetLocalizationOrDefault(string key)
    {
        return LocManager.GetCodeBehindOrDefault(key, LOCALIZATION_KEY, key);
    }
}