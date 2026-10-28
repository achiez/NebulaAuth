using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace NebulaAuth.NebulaLink.Credentials;

/// <summary>
///     Linux Secret Service (GNOME Keyring, KWallet...) through libsecret-1.so.0: schema
///     <c>pro.achiefy.nebulaauth.Link</c> with the attribute <c>link_id</c>, label "NebulaAuth Achiefy link". Uses the
///     non-variadic <c>*v_sync</c> functions with a GHashTable of attributes. Not available without a running and
///     unlocked keyring (headless, WSL): the probe fails and pairing is blocked unless encryption is on.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class LibSecretCredentialStore : IDeviceCredentialStore
{
    private const string SCHEMA_NAME = "pro.achiefy.nebulaauth.Link";
    private const string ATTRIBUTE = "link_id";
    private const string LABEL = "NebulaAuth Achiefy link";

    public string Kind => IDeviceCredentialStore.KIND_OS;
    public string DisplayName => "Secret Service (libsecret)";

    public Task<bool> ProbeAsync()
    {
        return Task.Run(() =>
        {
            var probe = "probe-" + Guid.NewGuid().ToString("N");
            try
            {
                var value = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                Native.Store(probe, value);
                var back = Native.Lookup(probe);
                Native.Clear(probe);
                return back == value;
            }
            catch (Exception ex)
            {
                NebulaLinkLog.Warn("Secret Service probe failed", ex);
                return false;
            }
        });
    }

    public Task SaveAsync(Guid linkId, string credential)
    {
        return Task.Run(() => Native.Store(linkId.ToString("N"), credential));
    }

    public Task<string?> LoadAsync(Guid linkId)
    {
        return Task.Run(() => Native.Lookup(linkId.ToString("N")));
    }

    public Task DeleteAsync(Guid linkId)
    {
        return Task.Run(() => Native.Clear(linkId.ToString("N")));
    }

    private static class Native
    {
        private static readonly IntPtr Secret = NativeLibrary.Load("libsecret-1.so.0");
        private static readonly IntPtr Glib = NativeLibrary.Load("libglib-2.0.so.0");

        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int> StoreV =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int>)
            NativeLibrary.GetExport(Secret, "secret_password_storev_sync");

        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr> LookupV =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)
            NativeLibrary.GetExport(Secret, "secret_password_lookupv_sync");

        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int> ClearV =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)
            NativeLibrary.GetExport(Secret, "secret_password_clearv_sync");

        private static readonly delegate* unmanaged<IntPtr, void> PasswordFree =
            (delegate* unmanaged<IntPtr, void>) NativeLibrary.GetExport(Secret, "secret_password_free");

        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> HashTableNew =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr>) NativeLibrary.GetExport(Glib, "g_hash_table_new");

        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> HashTableInsert =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>) NativeLibrary.GetExport(Glib, "g_hash_table_insert");

        private static readonly delegate* unmanaged<IntPtr, void> HashTableUnref =
            (delegate* unmanaged<IntPtr, void>) NativeLibrary.GetExport(Glib, "g_hash_table_unref");

        private static readonly delegate* unmanaged<IntPtr, void> ErrorFree =
            (delegate* unmanaged<IntPtr, void>) NativeLibrary.GetExport(Glib, "g_error_free");

        private static readonly IntPtr StrHash = NativeLibrary.GetExport(Glib, "g_str_hash");
        private static readonly IntPtr StrEqual = NativeLibrary.GetExport(Glib, "g_str_equal");

        // SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32];
        //                gint reserved; gpointer reserved1..reserved7; } -- kept for the process lifetime
        private static readonly IntPtr Schema = CreateSchema();

        private static IntPtr CreateSchema()
        {
            const int attributeSize = 16; // const gchar *name; SecretSchemaAttributeType type; (+ padding)
            var size = IntPtr.Size + 8 + 32 * attributeSize + 8 + 7 * IntPtr.Size;
            var schema = Marshal.AllocHGlobal(size);
            new Span<byte>((void*) schema, size).Clear();
            Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SCHEMA_NAME));
            Marshal.WriteInt32(schema, IntPtr.Size, 0); // SECRET_SCHEMA_NONE
            var first = schema + IntPtr.Size + 8;
            Marshal.WriteIntPtr(first, 0, Marshal.StringToCoTaskMemUTF8(ATTRIBUTE));
            Marshal.WriteInt32(first, IntPtr.Size, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
            return schema;
        }

        public static void Store(string linkId, string secret)
        {
            var attributes = Attributes(linkId, out var key, out var value);
            var label = Marshal.StringToCoTaskMemUTF8(LABEL);
            var password = Marshal.StringToCoTaskMemUTF8(secret);
            IntPtr error = IntPtr.Zero;
            try
            {
                var ok = StoreV(Schema, attributes, IntPtr.Zero, label, password, IntPtr.Zero, &error);
                ThrowOnError(error, "secret_password_store");
                if (ok == 0) throw new CredentialStoreException("secret_password_store failed");
            }
            finally
            {
                ZeroAndFree(password, secret.Length * 4);
                Marshal.FreeCoTaskMem(label);
                FreeAttributes(attributes, key, value);
            }
        }

        public static string? Lookup(string linkId)
        {
            var attributes = Attributes(linkId, out var key, out var value);
            IntPtr error = IntPtr.Zero;
            try
            {
                var result = LookupV(Schema, attributes, IntPtr.Zero, &error);
                ThrowOnError(error, "secret_password_lookup");
                if (result == IntPtr.Zero) return null;
                try
                {
                    return Marshal.PtrToStringUTF8(result);
                }
                finally
                {
                    PasswordFree(result);
                }
            }
            finally
            {
                FreeAttributes(attributes, key, value);
            }
        }

        public static void Clear(string linkId)
        {
            var attributes = Attributes(linkId, out var key, out var value);
            IntPtr error = IntPtr.Zero;
            try
            {
                ClearV(Schema, attributes, IntPtr.Zero, &error);
                ThrowOnError(error, "secret_password_clear");
            }
            finally
            {
                FreeAttributes(attributes, key, value);
            }
        }

        private static IntPtr Attributes(string linkId, out IntPtr key, out IntPtr value)
        {
            var table = HashTableNew(StrHash, StrEqual);
            key = Marshal.StringToCoTaskMemUTF8(ATTRIBUTE);
            value = Marshal.StringToCoTaskMemUTF8(linkId);
            HashTableInsert(table, key, value);
            return table;
        }

        private static void FreeAttributes(IntPtr table, IntPtr key, IntPtr value)
        {
            HashTableUnref(table);
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(value);
        }

        private static void ThrowOnError(IntPtr error, string call)
        {
            if (error == IntPtr.Zero) return;
            // GError { GQuark domain; gint code; gchar *message; }
            var code = Marshal.ReadInt32(error, 4);
            var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
            ErrorFree(error);
            throw new CredentialStoreException($"{call} failed ({code}): {message}");
        }

        private static void ZeroAndFree(IntPtr ptr, int maxBytes)
        {
            var length = 0;
            while (length < maxBytes && Marshal.ReadByte(ptr, length) != 0) length++;
            new Span<byte>((void*) ptr, length).Clear();
            Marshal.FreeCoTaskMem(ptr);
        }
    }
}
