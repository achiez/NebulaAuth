#if DEBUG
using System;
using Avalonia.Threading;

namespace NebulaAuth.View;

public partial class StorageStartupWindow
{
    /// <summary>
    ///     Debug builds only: <c>NEBULA_DEBUG_UNLOCK_TRY=&lt;password&gt;</c> submits that password once after the window
    ///     opened (wrong password state for screenshots / startup order tests without a keyboard).
    ///     <c>NEBULA_DEBUG_UNLOCK_TEXT=&lt;text&gt;</c> only puts the text into the field (password hints screenshots).
    /// </summary>
    partial void OnDebugOpened()
    {
        var text = Environment.GetEnvironmentVariable("NEBULA_DEBUG_UNLOCK_TEXT");
        if (!string.IsNullOrEmpty(text) && ViewModel.IsUnlockPage) ViewModel.Password = text;
        var password = Environment.GetEnvironmentVariable("NEBULA_DEBUG_UNLOCK_TRY");
        if (string.IsNullOrEmpty(password) || !ViewModel.IsUnlockPage) return;
        DispatcherTimer.RunOnce(() =>
        {
            ViewModel.Password = password;
            ViewModel.UnlockCommand.Execute(null);
        }, TimeSpan.FromMilliseconds(500));
    }
}
#endif
