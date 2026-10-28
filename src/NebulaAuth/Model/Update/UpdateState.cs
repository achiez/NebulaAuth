namespace NebulaAuth.Model.Update;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed
}

public enum UpdateFailureKind
{
    None,

    /// <summary>The feed or a package could not be downloaded.</summary>
    Network,

    /// <summary>The feed signature is invalid, the feed is not for this channel, or it is older than one already seen.</summary>
    Signature,

    /// <summary>A downloaded package does not match the SHA-256 of the signed feed.</summary>
    Checksum,

    /// <summary>The program folder (or its packages folder) is not writable for this user.</summary>
    NoWriteAccess,

    /// <summary>Not enough free space for the download or the installation.</summary>
    NoSpace,

    /// <summary>The updater ran but the old version is still installed (detected on the next start).</summary>
    ApplyFailed,

    /// <summary>Another NebulaAuth process or update operation uses the program folder.</summary>
    Busy,

    /// <summary>Anything else (details in the log).</summary>
    Unknown
}
