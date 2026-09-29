using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model.MafileExport;
using NebulaAuth.Model.Mafiles;

namespace NebulaAuth.ViewModel.Other;

public partial class MafileExporterVM : ObservableObject, IDisposable
{
    public MafileExportTemplateVM? CurrentTemplate
    {
        get => _currentTemplate;
        set => SetCurrentTemplate(value);
    }


    public ObservableCollection<MafileExportTemplateVM> Templates { get; }

    [ObservableProperty] private string? _accountsText;

    private string? _cachedName;
    private MafileExportTemplateVM? _currentTemplate;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddTemplateCommand), nameof(RemoveCurrentTemplateCommand))]
    private bool _editMode;

    [ObservableProperty] private HintBoxSeverity _hintBoxSeverity = HintBoxSeverity.Error;

    [ObservableProperty] private string? _hintText;

    public MafileExporterVM()
    {
        var templates = MafileExporterStorage.Templates
            .Select(MafileExportTemplateVM.FromModel);


        Templates = new ObservableCollection<MafileExportTemplateVM>(templates);
        MafileExporterStorage.Templates.CollectionChanged += OnTemplatesOnCollectionChanged;
        CurrentTemplate = Templates.FirstOrDefault();
    }

    private void SetCurrentTemplate(MafileExportTemplateVM? value)
    {
        if (_currentTemplate != null)
        {
            _currentTemplate.PropertyChanged -= OnCurrentTemplatePropertyChanged;
        }

        SetProperty(ref _currentTemplate, value, nameof(CurrentTemplate));
        if (value != null)
        {
            value.PropertyChanged += OnCurrentTemplatePropertyChanged;
        }

        ToggleEditModeCommand.NotifyCanExecuteChanged();
        RemoveCurrentTemplateCommand.NotifyCanExecuteChanged();
        OpenSelectDirectoryDialogCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void OnTemplatesOnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (MafileExportTemplate newItem in e.NewItems!)
                {
                    Templates.Add(MafileExportTemplateVM.FromModel(newItem));
                }

                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (MafileExportTemplate oldItem in e.OldItems!)
                {
                    var vmToRemove = Templates.FirstOrDefault(x => x.Name == oldItem.Name);
                    if (vmToRemove != null)
                    {
                        Templates.Remove(vmToRemove);
                    }
                }

                break;
            case NotifyCollectionChangedAction.Replace:
                foreach (MafileExportTemplate newItem in e.NewItems!)
                {
                    var vmToReplace = Templates.FirstOrDefault(x => x.Name == newItem.Name);
                    if (vmToReplace != null)
                    {
                        var index = Templates.IndexOf(vmToReplace);
                        Templates[index] = MafileExportTemplateVM.FromModel(newItem);
                    }
                }

                break;
            case NotifyCollectionChangedAction.Reset:
                Templates.Clear();
                break;
            case NotifyCollectionChangedAction.Move:
            default:
                break;
        }
    }

    private void OnCurrentTemplatePropertyChanged(object? sender, PropertyChangedEventArgs propertyChangedEventArgs)
    {
        var currentTemplate = CurrentTemplate;
        if (currentTemplate == null) return;
        var key = propertyChangedEventArgs.PropertyName == nameof(MafileExportTemplateVM.Name)
            ? _cachedName!
            : currentTemplate.Name;

        var existed = MafileExporterStorage.GetTemplate(key);
        if (existed != null)
        {
            currentTemplate.ApplyTo(existed);
            MafileExporterStorage.Save();
        }
        else
        {
            MafileExporterStorage.AddTemplate(currentTemplate.ToModel());
        }
    }

    [RelayCommand(CanExecute = nameof(CurrentTemplateNotNull))]
    private void ToggleEditMode()
    {
        EditMode = !EditMode;
        if (EditMode) _cachedName = CurrentTemplate?.Name;
    }

    [RelayCommand(CanExecute = nameof(NotEditMode))]
    private void AddTemplate()
    {
        ResetHintText();
        var name = "New Template";
        var i = 0;
        while (Templates.Any(x => x.Name == name))
        {
            name = $"New Template ({++i})";
        }

        var template = new MafileExportTemplate
        {
            Name = name,
            UseLoginAsMafileName = false,
            IncludeSharedSecret = true,
            IncludeIdentitySecret = true,
            IncludeRCode = true,
            IncludeSessionData = true,
            IncludeOtherInfo = true,
            IncludeNebulaProxy = true,
            IncludeNebulaPassword = true,
            IncludeNebulaGroup = true,
            ExportToZip = false,
            Path = null
        };
        MafileExporterStorage.AddTemplate(template);
        CurrentTemplate = Templates.FirstOrDefault(x => x.Name == name);
    }

    [RelayCommand(CanExecute = nameof(CurrentTemplateNotNullAndNotEditMode))]
    private void RemoveCurrentTemplate()
    {
        if (CurrentTemplate != null)
        {
            ResetHintText();
            var index = Templates.IndexOf(CurrentTemplate);
            var existed = MafileExporterStorage.GetTemplate(CurrentTemplate.Name);
            if (existed != null)
            {
                MafileExporterStorage.DeleteTemplate(existed);
            }

            if (Templates.Count > 0)
            {
                if (index >= Templates.Count)
                {
                    index = Templates.Count - 1;
                }

                CurrentTemplate = Templates[index];
            }
            else
            {
                CurrentTemplate = null;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CurrentTemplateNotNull))]
    private async Task OpenSelectDirectoryDialog()
    {
        if (CurrentTemplate == null) return;
        var selectedPath = await FileDialogs.PickFolderAsync();
        if (selectedPath != null && CurrentTemplate != null)
        {
            CurrentTemplate.Path = selectedPath;
        }
    }

    [RelayCommand(CanExecute = nameof(CurrentTemplateNotNull))]
    private async Task Export()
    {
        var template = CurrentTemplate;
        if (template == null) return;

        string[] split;
        if (string.IsNullOrWhiteSpace(AccountsText))
        {
            split = Storage.MaFiles.Select(m => m.AccountName).ToArray();
        }
        else
        {
            split = Regex
                .Split(AccountsText, "\r\n|\r|\n")
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(ExtractLogin)
                .ToArray();
        }

        ResetHintText();
        ExportResult res;
        try
        {
            res = await MafileExporter.ExportMafiles(split, template.ToModel());
        }
        catch (Exception ex)
        {
            SetHintText(ex.Message);
            return;
        }

        if (!res.Success)
        {
            SetHintText(res.Error);
            return;
        }

        var hint = string.Format(LocManager.GetCodeBehindOrDefault("Exported", "MafileExporterVM.Result.Exported"),
            res.Exported.Count);

        if (res.NotFound.Count > 0)
        {
            hint += string.Format(LocManager.GetCodeBehindOrDefault("NotFound", "MafileExporterVM.Result.NotFound"),
                res.NotFound.Count);
        }

        if (res.Conflict.Count > 0)
        {
            hint += string.Format(LocManager.GetCodeBehindOrDefault("Conflict", "MafileExporterVM.Result.Conflict"),
                res.Conflict.Count);
        }

        SetHintText(hint, HintBoxSeverity.Info);
        var errors = res.NotFound.Concat(res.Conflict).ToHashSet();
        var errorLines = split.Where(errors.Contains);
        var text = string.Join(Environment.NewLine, errorLines);

        AccountsText = text;
    }

    // Strips the password part from "login:password" input lines.
    // SteamId64 values contain no colon, so any colon signals the login:password format.
    private static string ExtractLogin(string line)
    {
        var idx = line.IndexOf(':');
        return idx > 0 ? line[..idx] : line;
    }

    private void SetHintText(string? text, HintBoxSeverity severity = HintBoxSeverity.Error)
    {
        HintText = text;
        HintBoxSeverity = severity;
    }

    private void ResetHintText()
    {
        SetHintText(null);
    }

    #region CanExecute

    private bool CurrentTemplateNotNull()
    {
        return CurrentTemplate != null;
    }

    private bool CurrentTemplateNotNullAndNotEditMode()
    {
        return CurrentTemplateNotNull() && NotEditMode();
    }

    private bool NotEditMode()
    {
        return !EditMode;
    }

    #endregion

    public void Dispose()
    {
        MafileExporterStorage.Templates.CollectionChanged -= OnTemplatesOnCollectionChanged;
        if (_currentTemplate != null)
        {
            _currentTemplate.PropertyChanged -= OnCurrentTemplatePropertyChanged;
        }
    }
}

public partial class MafileExportTemplateVM : ObservableObject
{
    [ObservableProperty] private bool _exportToZip;
    [ObservableProperty] private bool _includeIdentitySecret;
    [ObservableProperty] private bool _includeNebulaGroup;
    [ObservableProperty] private bool _includeNebulaPassword;
    [ObservableProperty] private bool _includeNebulaProxy;
    [ObservableProperty] private bool _includeOtherInfo;
    [ObservableProperty] private bool _includeRCode;
    [ObservableProperty] private bool _includeSessionData;
    [ObservableProperty] private bool _includeSharedSecret;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private bool _useLoginAsMafileName;


    public static MafileExportTemplateVM FromModel(MafileExportTemplate x)
    {
        return new MafileExportTemplateVM
        {
            Name = x.Name,
            UseLoginAsMafileName = x.UseLoginAsMafileName,
            IncludeSharedSecret = x.IncludeSharedSecret,
            IncludeIdentitySecret = x.IncludeIdentitySecret,
            IncludeRCode = x.IncludeRCode,
            IncludeSessionData = x.IncludeSessionData,
            IncludeOtherInfo = x.IncludeOtherInfo,
            IncludeNebulaProxy = x.IncludeNebulaProxy,
            IncludeNebulaPassword = x.IncludeNebulaPassword,
            IncludeNebulaGroup = x.IncludeNebulaGroup,
            ExportToZip = x.ExportToZip,
            Path = x.Path
        };
    }

    public MafileExportTemplate ToModel()
    {
        return new MafileExportTemplate
        {
            Name = Name,
            UseLoginAsMafileName = UseLoginAsMafileName,
            IncludeSharedSecret = IncludeSharedSecret,
            IncludeIdentitySecret = IncludeIdentitySecret,
            IncludeRCode = IncludeRCode,
            IncludeSessionData = IncludeSessionData,
            IncludeOtherInfo = IncludeOtherInfo,
            IncludeNebulaProxy = IncludeNebulaProxy,
            IncludeNebulaPassword = IncludeNebulaPassword,
            IncludeNebulaGroup = IncludeNebulaGroup,
            ExportToZip = ExportToZip,
            Path = Path
        };
    }

    public void ApplyTo(MafileExportTemplate model)
    {
        model.Name = Name;
        model.UseLoginAsMafileName = UseLoginAsMafileName;
        model.IncludeSharedSecret = IncludeSharedSecret;
        model.IncludeIdentitySecret = IncludeIdentitySecret;
        model.IncludeRCode = IncludeRCode;
        model.IncludeSessionData = IncludeSessionData;
        model.IncludeOtherInfo = IncludeOtherInfo;
        model.IncludeNebulaProxy = IncludeNebulaProxy;
        model.IncludeNebulaPassword = IncludeNebulaPassword;
        model.IncludeNebulaGroup = IncludeNebulaGroup;
        model.ExportToZip = ExportToZip;
        model.Path = Path;
    }
}