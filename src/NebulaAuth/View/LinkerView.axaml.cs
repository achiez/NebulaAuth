using Avalonia.Controls;
using NebulaAuth.ViewModel.Linker;

namespace NebulaAuth.View;

public partial class LinkerView : UserControl
{
    public LinkerView()
    {
        InitializeComponent();
        WizardKeyboard.Attach(this, StepHost, () => (DataContext as LinkAccountVM)?.CurrentStep?.NextCommand);
    }
}
