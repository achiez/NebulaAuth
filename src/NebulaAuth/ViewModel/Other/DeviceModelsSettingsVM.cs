using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.DeviceModels;

namespace NebulaAuth.ViewModel.Other;

/// <summary>
///     Settings -> Devices: one list of the phone models new accounts get (built-in and the user's own). Any model can be
///     removed while at least one remains; removed built-in models can be restored.
/// </summary>
public partial class DeviceModelsSettingsVM : ObservableObject
{
    [ObservableProperty] private string? _error;

    [ObservableProperty] private string? _newModel;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRemovedBuiltIns))]
    private int _removedBuiltInCount;

    public DeviceModelsSettingsVM()
    {
        Sync();
    }

    /// <summary>The list: custom names first, then the built-in models.</summary>
    public ObservableCollection<DeviceModelItemVM> Items { get; } = [];

    /// <summary>Only one model is left: it can't be removed.</summary>
    public bool IsLastModel => Items.Count <= 1;

    public bool HasRemovedBuiltIns => RemovedBuiltInCount > 0;

    public int MaxLength => DeviceModelCatalog.MAX_CUSTOM_LENGTH;

    partial void OnNewModelChanged(string? value)
    {
        Error = null;
    }

    /// <summary>Adds the typed name; the name of a removed built-in model puts that model back.</summary>
    [RelayCommand]
    private void Add()
    {
        var result = DeviceNames.AddModel(NewModel, out _);
        if (result != DeviceModelValidation.Ok)
        {
            Error = GetErrorText(result);
            return;
        }

        Sync();
        NewModel = string.Empty;
        Error = null;
    }

    [RelayCommand]
    private void Remove(DeviceModelItemVM? item)
    {
        if (item == null || IsLastModel) return;
        DeviceNames.RemoveModel(item.Name);
        Sync();
        Error = null;
    }

    [RelayCommand]
    private void RestoreBuiltIn()
    {
        DeviceNames.RestoreBuiltInModels();
        Sync();
    }

    /// <summary>Brings <see cref="Items" /> in line with the saved list, changing only what differs (keeps the scroll).</summary>
    private void Sync()
    {
        var entries = DeviceNames.GetEntries();
        var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal);
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!names.Contains(Items[i].Name))
                Items.RemoveAt(i);

        for (var i = 0; i < entries.Count; i++)
        {
            if (i < Items.Count && Items[i].Name == entries[i].Name) continue;
            var existing = Items.FirstOrDefault(x => x.Name == entries[i].Name);
            if (existing != null) Items.Remove(existing);
            Items.Insert(i, existing ?? new DeviceModelItemVM(entries[i]));
        }

        var canRemove = Items.Count > 1;
        foreach (var item in Items) item.CanRemove = canRemove;
        RemovedBuiltInCount = DeviceNames.GetRemovedBuiltInCount();
        OnPropertyChanged(nameof(IsLastModel));
    }

    public static string GetErrorText(DeviceModelValidation result)
    {
        return result switch
        {
            DeviceModelValidation.Empty => Loc("Enter a model name.", "Empty"),
            DeviceModelValidation.TooLong => string.Format(
                Loc("The name is too long (at most {0} characters).", "TooLong"),
                DeviceModelCatalog.MAX_CUSTOM_LENGTH),
            DeviceModelValidation.InvalidCharacters => Loc("The name contains invalid characters.", "InvalidCharacters"),
            DeviceModelValidation.Duplicate => Loc("This model is already in the list.", "Duplicate"),
            _ => string.Empty
        };
    }

    private static string Loc(string def, string key)
    {
        return LocManager.GetOrDefault(def, "SettingsDialog", "Devices", "Errors", key);
    }
}

/// <summary>A row of the Devices list.</summary>
public partial class DeviceModelItemVM(DeviceModelEntry entry) : ObservableObject
{
    /// <summary>False for the last model of the list.</summary>
    [ObservableProperty] private bool _canRemove = true;

    /// <summary>The device name sent to Steam.</summary>
    public string Name { get; } = entry.Name;

    public bool IsBuiltIn => entry.IsBuiltIn;

    /// <summary>Brand and marketing name of a built-in model ("Samsung Galaxy S23"); null for a custom name.</summary>
    public string? Description => entry.BuiltIn?.DisplayName;
}
