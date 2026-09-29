#if DEBUG
using System;
using NebulaAuth.Core;
using NebulaAuth.ViewModel.Other;

namespace NebulaAuth.View;

public partial class MafileExporterView
{
    /// <summary>
    ///     NEBULA_PREVIEW=NebulaAuth.View.MafileExporterView. Uses the export templates of the working directory
    ///     (nothing is written). NEBULA_PREVIEW_ARG: empty (initial state), "expanded" (export settings open),
    ///     "edit" (template rename mode), "result" (export result + not exported lines), "error" (export error),
    ///     "all" (expanded + result).
    /// </summary>
    internal static object CreatePreview(string? arg)
    {
        var vm = new MafileExporterVM();
        var view = new MafileExporterView {DataContext = vm};
        var expanded = arg is "expanded" or "all";
        var result = arg is "result" or "all";

        if (expanded)
        {
            view.TemplateExpander.IsExpanded = true;
        }

        if (arg == "edit")
        {
            vm.ToggleEditModeCommand.Execute(null);
        }

        if (result)
        {
            vm.AccountsText = string.Join(Environment.NewLine, "unknown_login", "76561190000000042");
            vm.HintText =
                string.Format(LocManager.GetCodeBehindOrDefault("Exported", "MafileExporterVM.Result.Exported"), 10) +
                string.Format(LocManager.GetCodeBehindOrDefault("NotFound", "MafileExporterVM.Result.NotFound"), 2);
            vm.HintBoxSeverity = HintBoxSeverity.Info;
        }

        if (arg == "error")
        {
            vm.HintText = LocManager.GetCodeBehindOrDefault("InvalidPath", "MafileExporter.InvalidPath");
            vm.HintBoxSeverity = HintBoxSeverity.Error;
        }

        return view;
    }
}
#endif
