using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace NebulaAuth.NebulaLink.Credentials;

/// <summary>
///     macOS Keychain generic password (Security.framework through P/Invoke, no "security" command line, so the secret
///     never appears in a process argument list): service <c>pro.achiefy.nebulaauth</c>, account <c>{linkId:N}</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore : IDeviceCredentialStore
{
    private const string SERVICE = "pro.achiefy.nebulaauth";
    private const string SECURITY = "/System/Library/Frameworks/Security.framework/Security";
    private const string CORE_FOUNDATION = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ERR_SEC_SUCCESS = 0;
    private const int ERR_SEC_ITEM_NOT_FOUND = -25300;
    private const int ERR_SEC_DUPLICATE_ITEM = -25299;
    private const uint K_CF_STRING_ENCODING_UTF8 = 0x08000100;

    public string Kind => IDeviceCredentialStore.KIND_OS;
    public string DisplayName => "macOS Keychain";

    public Task<bool> ProbeAsync()
    {
        var account = "probe-" + Guid.NewGuid().ToString("N");
        try
        {
            var value = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            Upsert(account, value);
            var back = Find(account);
            Remove(account);
            return Task.FromResult(back == value);
        }
        catch (Exception ex)
        {
            NebulaLinkLog.Warn("macOS Keychain probe failed", ex);
            return Task.FromResult(false);
        }
    }

    public Task SaveAsync(Guid linkId, string credential)
    {
        Upsert(linkId.ToString("N"), credential);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(Guid linkId)
    {
        return Task.FromResult(Find(linkId.ToString("N")));
    }

    public Task DeleteAsync(Guid linkId)
    {
        Remove(linkId.ToString("N"));
        return Task.CompletedTask;
    }

    #region Keychain calls

    private static void Upsert(string account, string secret)
    {
        using var cf = new CfScope();
        var data = cf.Data(Encoding.UTF8.GetBytes(secret));
        var query = cf.Dictionary(
            (Sym.SecClass, Sym.SecClassGenericPassword),
            (Sym.SecAttrService, cf.String(SERVICE)),
            (Sym.SecAttrAccount, cf.String(account)));
        var update = cf.Dictionary((Sym.SecValueData, data));
        var status = SecItemUpdate(query, update);
        if (status == ERR_SEC_ITEM_NOT_FOUND)
        {
            var add = cf.Dictionary(
                (Sym.SecClass, Sym.SecClassGenericPassword),
                (Sym.SecAttrService, cf.String(SERVICE)),
                (Sym.SecAttrAccount, cf.String(account)),
                (Sym.SecAttrLabel, cf.String("NebulaAuth Achiefy link")),
                (Sym.SecValueData, data));
            status = SecItemAdd(add, IntPtr.Zero);
            if (status == ERR_SEC_DUPLICATE_ITEM) status = SecItemUpdate(query, update);
        }

        if (status != ERR_SEC_SUCCESS) throw new CredentialStoreException($"Keychain write failed ({status})");
    }

    private static string? Find(string account)
    {
        using var cf = new CfScope();
        var query = cf.Dictionary(
            (Sym.SecClass, Sym.SecClassGenericPassword),
            (Sym.SecAttrService, cf.String(SERVICE)),
            (Sym.SecAttrAccount, cf.String(account)),
            (Sym.SecReturnData, Sym.CFBooleanTrue),
            (Sym.SecMatchLimit, Sym.SecMatchLimitOne));
        var status = SecItemCopyMatching(query, out var result);
        if (status == ERR_SEC_ITEM_NOT_FOUND) return null;
        if (status != ERR_SEC_SUCCESS) throw new CredentialStoreException($"Keychain read failed ({status})");
        if (result == IntPtr.Zero) return null;
        try
        {
            var length = (int) CFDataGetLength(result);
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
            var value = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return value;
        }
        finally
        {
            CFRelease(result);
        }
    }

    private static void Remove(string account)
    {
        using var cf = new CfScope();
        var query = cf.Dictionary(
            (Sym.SecClass, Sym.SecClassGenericPassword),
            (Sym.SecAttrService, cf.String(SERVICE)),
            (Sym.SecAttrAccount, cf.String(account)));
        var status = SecItemDelete(query);
        if (status != ERR_SEC_SUCCESS && status != ERR_SEC_ITEM_NOT_FOUND)
            throw new CredentialStoreException($"Keychain delete failed ({status})");
    }

    #endregion

    #region CoreFoundation helpers

    /// <summary>Owns the CF objects created for one call (released on dispose).</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly System.Collections.Generic.List<IntPtr> _owned = [];

        public IntPtr String(string value)
        {
            var ptr = CFStringCreateWithCString(IntPtr.Zero, value, K_CF_STRING_ENCODING_UTF8);
            if (ptr == IntPtr.Zero) throw new CredentialStoreException("CFStringCreate failed");
            _owned.Add(ptr);
            return ptr;
        }

        public IntPtr Data(byte[] bytes)
        {
            var ptr = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            Array.Clear(bytes);
            if (ptr == IntPtr.Zero) throw new CredentialStoreException("CFDataCreate failed");
            _owned.Add(ptr);
            return ptr;
        }

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            var keys = new IntPtr[pairs.Length];
            var values = new IntPtr[pairs.Length];
            for (var i = 0; i < pairs.Length; i++)
            {
                keys[i] = pairs[i].Key;
                values[i] = pairs[i].Value;
            }

            var ptr = CFDictionaryCreate(IntPtr.Zero, keys, values, pairs.Length, Sym.KeyCallbacks, Sym.ValueCallbacks);
            if (ptr == IntPtr.Zero) throw new CredentialStoreException("CFDictionaryCreate failed");
            _owned.Add(ptr);
            return ptr;
        }

        public void Dispose()
        {
            for (var i = _owned.Count - 1; i >= 0; i--) CFRelease(_owned[i]);
            _owned.Clear();
        }
    }

    /// <summary>Exported constants of Security.framework / CoreFoundation (pointers to CFStringRef / structs).</summary>
    private static class Sym
    {
        private static readonly IntPtr SecurityLib = NativeLibrary.Load(SECURITY);
        private static readonly IntPtr CoreFoundationLib = NativeLibrary.Load(CORE_FOUNDATION);

        public static readonly IntPtr SecClass = Security("kSecClass");
        public static readonly IntPtr SecClassGenericPassword = Security("kSecClassGenericPassword");
        public static readonly IntPtr SecAttrService = Security("kSecAttrService");
        public static readonly IntPtr SecAttrAccount = Security("kSecAttrAccount");
        public static readonly IntPtr SecAttrLabel = Security("kSecAttrLabel");
        public static readonly IntPtr SecValueData = Security("kSecValueData");
        public static readonly IntPtr SecReturnData = Security("kSecReturnData");
        public static readonly IntPtr SecMatchLimit = Security("kSecMatchLimit");
        public static readonly IntPtr SecMatchLimitOne = Security("kSecMatchLimitOne");
        public static readonly IntPtr CFBooleanTrue = Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundationLib, "kCFBooleanTrue"));

        // Addresses of the callback structs themselves (not dereferenced)
        public static readonly IntPtr KeyCallbacks = NativeLibrary.GetExport(CoreFoundationLib, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr ValueCallbacks = NativeLibrary.GetExport(CoreFoundationLib, "kCFTypeDictionaryValueCallBacks");

        private static IntPtr Security(string name)
        {
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLib, name));
        }
    }

    [DllImport(SECURITY)]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(SECURITY)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SECURITY)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SECURITY)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(CORE_FOUNDATION)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CORE_FOUNDATION)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CORE_FOUNDATION)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CORE_FOUNDATION)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CORE_FOUNDATION)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CORE_FOUNDATION)]
    private static extern void CFRelease(IntPtr obj);

    #endregion
}
