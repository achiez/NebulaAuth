using NebulaAuth.Core;

namespace NebulaAuth;

public partial class App
{
    static partial void DebugOnStarted()
    {
        DebugPreview.TryShow();
    }
}
