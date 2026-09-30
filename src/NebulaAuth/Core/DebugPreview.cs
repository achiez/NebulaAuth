#if DEBUG
using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Threading;
using NebulaAuth.Model;

namespace NebulaAuth.Core;

/// <summary>
///     Debug builds only: NEBULA_PREVIEW=&lt;full type name of a view&gt; opens that view in the root dialog host right after startup,
///     so a view can be inspected (screenshot) without clicking through the UI. NEBULA_PREVIEW_ARG is passed to the view's optional
///     <c>internal static object CreatePreview(string? arg)</c>, which returns the view with a design-time DataContext
///     (e.g. a specific wizard step). Without that method the view is created with its parameterless constructor.
/// </summary>
public static class DebugPreview
{
    public static void TryShow()
    {
        var typeName = Environment.GetEnvironmentVariable("NEBULA_PREVIEW");
        if (string.IsNullOrWhiteSpace(typeName)) return;
        var arg = Environment.GetEnvironmentVariable("NEBULA_PREVIEW_ARG");
        _ = ShowAsync(typeName, arg);
    }

    private static async Task ShowAsync(string typeName, string? arg)
    {
        await Task.Delay(1500);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var type = typeof(DebugPreview).Assembly.GetType(typeName, true)!;
                var factory = type.GetMethod("CreatePreview",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(string)]);
                var view = factory != null ? factory.Invoke(null, [arg]) : Activator.CreateInstance(type);
                if (view == null) return;
                // A startup dialog (skipped storage files) would make Show fail: the preview replaces it
                if (DialogService.IsDialogOpen)
                {
                    DialogService.Close();
                    await Task.Delay(300);
                }

                await DialogService.Show(view);
            }
            catch (Exception ex)
            {
                Shell.Logger.Error(ex, "DebugPreview failed for {type}", typeName);
                SnackbarController.SendSnackbar("DebugPreview failed: " + ex.GetBaseException().Message);
            }
        });
    }
}
#endif
