namespace NebulaAuth.Core.Updates;

/// <summary>
///     Where 2.x clients look for updates and which key must have signed the feed. The feed and changelog addresses are
///     pinned here together with the key on purpose (data the updater fetches, never shown or opened as links); the
///     release page users open comes from links.json (<see cref="LinksManager.ReleasesUrl" />).
/// </summary>
public static class UpdateTrust
{
    /// <summary>
    ///     Signed feeds (<c>releases.{channel}.json</c> + <c>.sig</c>), committed by the release workflow after the GitHub
    ///     release is published. Asset URLs inside the feeds are absolute release download links, so no GitHub API
    ///     request (and its rate limit) is involved.
    /// </summary>
    public const string FEED_BASE_URL =
        "https://raw.githubusercontent.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/master/NebulaAuth/feeds/";

    /// <summary>
    ///     JSON changelogs (<c>changelog/{version}.json</c>).
    /// </summary>
    public const string CHANGELOG_BASE_URL =
        "https://raw.githubusercontent.com/achiez/NebulaAuth-Steam-Desktop-Authenticator-by-Achies/master/changelog/";

    // =================================================================================================================
    //  PINNED FEED PUBLIC KEY (ECDSA P-256, SubjectPublicKeyInfo PEM).
    //  The only place the key lives. See .github/workflows/publish.yml ("Check the pinned feed key"): the release
    //  workflow refuses to publish while this does not match the private key in the FEED_SIGNING_KEY secret.
    //  Never put a private key into the repository.
    // =================================================================================================================
    public const string FEED_PUBLIC_KEY_PEM = """
                                              -----BEGIN PUBLIC KEY-----
                                              MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEN2QKkKbeNUbZVQ75+OoAbtHfM9iL
                                              W9l+D6x1PEtZYVmdHvBP9l1E58iPsJrZISNQSmOmgJrLVqLs8w0seivlTw==
                                              -----END PUBLIC KEY-----
                                              """;
}
