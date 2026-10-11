using System;
using System.Threading.Tasks;

namespace NebulaAuth.NebulaLink.Credentials;

/// <summary>
///     Secure storage of the Achiefy device credential. Never plain text: either the encrypted storage of NebulaAuth or
///     the store of the operating system.
/// </summary>
public interface IDeviceCredentialStore
{
    public const string KIND_ENCRYPTED = "encrypted";
    public const string KIND_OS = "os";

    /// <summary>"encrypted" | "os".</summary>
    string Kind { get; }

    /// <summary>Human-readable name of the backend for logs and the UI ("Windows Credential Manager").</summary>
    string DisplayName { get; }

    /// <summary>Writes, reads back and deletes a probe item. False when the store can't be used.</summary>
    Task<bool> ProbeAsync();

    Task SaveAsync(Guid linkId, string credential);
    Task<string?> LoadAsync(Guid linkId);
    Task DeleteAsync(Guid linkId);
}

/// <summary>The credential store failed (not available, locked keyring, access denied).</summary>
public sealed class CredentialStoreException(string message, Exception? inner = null) : Exception(message, inner);
