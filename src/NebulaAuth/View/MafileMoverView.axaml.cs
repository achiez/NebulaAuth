using Avalonia.Controls;
using NebulaAuth.ViewModel.MafileMover;

namespace NebulaAuth.View;

public partial class MafileMoverView : UserControl
{
    public MafileMoverView()
    {
        InitializeComponent();
        WizardKeyboard.Attach(this, StepHost, () => (DataContext as MafileMoverVM)?.CurrentStep?.NextCommand);
    }
}
