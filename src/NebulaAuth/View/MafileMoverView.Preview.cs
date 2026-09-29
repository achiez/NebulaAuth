#if DEBUG
using System.Linq;
using System.Threading.Tasks;
using NebulaAuth.Core;
using NebulaAuth.ViewModel.MafileMover;

namespace NebulaAuth.View;

public partial class MafileMoverView
{
    /// <summary>
    ///     Debug preview (NEBULA_PREVIEW=NebulaAuth.View.MafileMoverView, NEBULA_PREVIEW_ARG=step): "1" auth, "2" guard code,
    ///     "3" sms, "4" done. Suffixes: "f" filled fields, "l" login in progress (with "f"), "r" guard code retry tip,
    ///     "e" sample error. No network.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        arg = (arg ?? "1").Trim().ToLowerInvariant();
        var vm = new MafileMoverVM();
        var defaultProxy = (vm.CurrentStep as MafileMoverAuthStepVM)?.SelectedProxy;
        var filled = arg.Contains('f');
        MafileMoverStepVM step = arg.FirstOrDefault() switch
        {
            '2' => new MafileMoverGuardCodeStepVM(arg.Contains('r')) {GuardCode = filled ? "F4K3C" : string.Empty},
            '3' => new MafileMoverSmsStepVM {SmsCode = filled ? "12345" : string.Empty},
            '4' => new MafileMoverDoneStepVM("R12345", "76561198000000001"),
            _ => new MafileMoverAuthStepVM(defaultProxy)
            {
                // "l": login request in progress (the command never completes in the preview)
                Callback = () => arg.Contains('l') ? new TaskCompletionSource().Task : Task.CompletedTask,
                Login = filled ? "preview_login" : string.Empty,
                Password = filled ? "preview_password" : string.Empty
            }
        };
        vm.CurrentStep = step;
        vm.Tip = step.Tip;
        if (arg.Contains('l'))
            step.NextCommand.Execute(null);
        if (arg.Contains('e'))
        {
            vm.Error = step is MafileMoverSmsStepVM
                ? LocManager.GetCodeBehindOrDefault("SmsCodeFailed", MafileMoverVM.LOCALIZATION_KEY, "SmsCodeFailed")
                : LocManager.GetCodeBehindOrDefault("GuardIsNotActive", MafileMoverVM.LOCALIZATION_KEY, "GuardIsNotActive");
        }

        return new MafileMoverView {DataContext = vm};
    }
}
#endif
