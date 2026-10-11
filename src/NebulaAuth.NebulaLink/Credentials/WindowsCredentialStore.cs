using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace NebulaAuth.NebulaLink.Credentials;

/// <summary>
///     Windows Credential Manager (generic credential, persisted per local machine and user, protected by DPAPI):
///     target <c>NebulaAuth/Achiefy/{linkId:N}</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : IDeviceCredentialStore
{
    private const string TARGET_PREFIX = "NebulaAuth/Achiefy/";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public string Kind => IDeviceCredentialStore.KIND_OS;
    public string DisplayName => "Windows Credential Manager";

    public Task<bool> ProbeAsync()
    {
        var probeId = Guid.NewGuid();
        try
        {
            var value = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            Write(Target(probeId), value);
            var back = Read(Target(probeId));
            Delete(Target(probeId));
            return Task.FromResult(back == value);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("Windows Credential Manager probe failed", ex);
            try
            {
                Delete(Target(probeId));
            }
            catch
            {
                // ignored
            }

            return Task.FromResult(false);
        }
    }

    public Task SaveAsync(Guid linkId, string credential)
    {
        Write(Target(linkId), credential);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(Guid linkId)
    {
        return Task.FromResult(Read(Target(linkId)));
    }

    public Task DeleteAsync(Guid linkId)
    {
        Delete(Target(linkId));
        return Task.CompletedTask;
    }

    private static string Target(Guid linkId)
    {
        return TARGET_PREFIX + linkId.ToString("N");
    }

    private static void Write(string target, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni("NebulaAuth");
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userPtr
            };
            if (!CredWriteW(ref cred, 0))
                throw new CredentialStoreException($"CredWrite failed ({Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    private static string? Read(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ERROR_NOT_FOUND) return null;
            throw new CredentialStoreException($"CredRead failed ({error})");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return value;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static void Delete(string target)
    {
        if (CredDeleteW(target, CRED_TYPE_GENERIC, 0)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != ERROR_NOT_FOUND) throw new CredentialStoreException($"CredDelete failed ({error})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref CREDENTIAL credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
