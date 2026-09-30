using System.Diagnostics.CodeAnalysis;
using NebulaAuth.Model.Entities;

namespace NebulaAuth.Model;

public partial class SessionHandler //Helpers
{
    private static bool MobileTokenExpired(this Mafile mafile)
    {
        var mobileToken = mafile.SessionData?.GetMobileToken();
        return mobileToken == null || mobileToken.Value.IsExpired;
    }

    private static bool RefreshTokenExpired(this Mafile mafile)
    {
        var refreshToken = mafile.SessionData?.RefreshToken;
        return refreshToken == null || refreshToken.Value.IsExpired;
    }

    private static bool HasPassword(this Mafile mafile, [NotNullWhen(true)] out string? plainPassword)
    {
        plainPassword = GetPassword(mafile);
        return !string.IsNullOrWhiteSpace(plainPassword);
    }

    // Saved only in an encrypted storage (separate password store, by Steam login)
    private static string? GetPassword(Mafile mafile)
    {
        return SteamPasswords.Get(mafile.AccountName);
    }
}