using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AchiesUtilities.Web.Proxy;
using NebulaAuth.Core;
using NebulaAuth.Model;
using NebulaAuth.ViewModel.Linker;

namespace NebulaAuth.View;

public partial class LinkerView
{
    /// <summary>
    ///     Preview (NEBULA_PREVIEW=NebulaAuth.View.LinkerView). <paramref name="arg" /> = step number 1..7
    ///     (1 auth, 2 email auth code, 3 phone, 4 confirm email link, 5 SMS, 6 email code, 7 done) plus optional flags:
    ///     "e" shows a sample error, "r" = retry text on step 4, "l" = step 1 while logging in (spinner), "n" = step 1 with
    ///     a Cyrillic password (keyboard layout hint).
    ///     Examples: "1e", "4r", "1l". No network: the preview steps are not connected to the linking flow.
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        arg = (arg ?? "1").Trim().ToLowerInvariant();
        var digits = new string(arg.TakeWhile(char.IsDigit).ToArray());
        var flags = arg[digits.Length..];
        var stepNumber = int.TryParse(digits, out var n) ? n : 1;

        var vm = new LinkAccountVM();
        LinkAccountStepVM step = stepNumber switch
        {
            2 => new LinkAccountEmailAuthStepVM(),
            3 => new LinkAccountPhoneStepVM(),
            4 => new LinkAccountConfirmEmailStepVM(flags.Contains('r')),
            5 => new LinkAccountSmsStepVM("+7 *** ***-**-45") {SmsCode = "12345"},
            6 => new LinkAccountEmailCodeStepVM(),
            7 => new LinkAccountDoneStepVM("R12345", "76561199000000001"),
            _ => CreateAuthPreviewStep(flags.Contains('l'), flags.Contains('n'))
        };
        vm.CurrentStep = step;
        vm.Tip = step.Tip;
        if (flags.Contains('e'))
        {
            vm.Error = LocManager.GetCodeBehindOrDefault("CantLogin", LinkAccountVM.LOCALIZATION_KEY, "CantLogin") +
                       "InvalidPassword (5): the account name or password that you have entered is incorrect";
        }

        var view = new LinkerView {DataContext = vm};
        view.DetachedFromVisualTree += (_, _) => vm.Dispose();
        return view;
    }

    private static LinkAccountAuthStepVM CreateAuthPreviewStep(bool loggingIn, bool nonLatinPassword)
    {
        var proxy = ProxyStorage.Proxies.Count > 0 ? ProxyStorage.Proxies.First() : (KeyValuePair<int, ProxyData>?) null;
        var step = new LinkAccountAuthStepVM(proxy)
        {
            Login = "design_user",
            Callback = () => Task.Delay(Timeout.Infinite)
        };
        if (nonLatinPassword) step.Password = "пароль123";
        if (loggingIn)
        {
            step.Password = "design_password";
            step.NextCommand.Execute(null);
        }

        return step;
    }
}
