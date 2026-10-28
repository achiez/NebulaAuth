using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.Utility;
using SteamLib.SteamMobile;

namespace NebulaAuth.ViewModel;

public partial class MainVM
{
    [ObservableProperty] private string _code = "Code";
    [ObservableProperty] private double _codeProgress;
    private Timer _codeTimer;


    [MemberNotNull(nameof(_codeTimer))]
    private void CreateCodeTimer()
    {
        CodeProgress = CalculateCodeProgress();
        _codeTimer = new Timer(UpdateCode, null, 0, 1000);
    }

    private void UpdateCode(object? state = null)
    {
        var currentTime = TimeAligner.GetSteamTime();
        var untilChange = currentTime % 30;
        if (untilChange != 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            CodeProgress = -1;
            CodeProgress = 30;
            if (AppHost.IsMainWindowMinimized) return;
            if (SelectedMafile == null) return;
            Code = SteamGuardCodeGenerator.GenerateCode(SelectedMafile.SharedSecret);
        }, DispatcherPriority.Normal);
    }


    private long CalculateCodeProgress()
    {
        var currentTime = TimeAligner.GetSteamTime();
        var untilChange = currentTime % 30;
        return 30 - untilChange;
    }


    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CopyCode()
    {
        var selectedMafile = SelectedMafile;
        if (selectedMafile == null) return;
        try
        {
            if (ClipboardHelper.Set(Code))
                Code = LocManager.GetOrDefault("CodeCopied", "MainWindow", "CodeCopied");
        }
        catch (Exception ex)
        {
            Shell.Logger.Error(ex);
        }
        finally
        {
            await Task.Delay(200);
            selectedMafile = SelectedMafile;
            if (selectedMafile != null)
                Code = SteamGuardCodeGenerator.GenerateCode(selectedMafile.SharedSecret);
        }
    }
}