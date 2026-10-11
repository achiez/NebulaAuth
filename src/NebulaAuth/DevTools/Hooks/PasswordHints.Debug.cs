using System;

namespace NebulaAuth;

public partial class PasswordHints
{
    /// <summary>
    ///     <c>NEBULA_DEBUG_PASSWORD_HINTS=caps</c> reports Caps Lock as on in every focused password
    ///     field (screenshots without a keyboard).
    /// </summary>
    partial void DebugInit(ref bool forceCapsLock)
    {
        var value = Environment.GetEnvironmentVariable("NEBULA_DEBUG_PASSWORD_HINTS");
        forceCapsLock = value != null && value.Contains("caps", StringComparison.OrdinalIgnoreCase);
    }
}
