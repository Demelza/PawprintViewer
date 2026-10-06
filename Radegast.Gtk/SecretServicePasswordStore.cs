using System.Runtime.InteropServices;

namespace Radegast.Gtk;

/// <summary>libsecret's vector APIs avoid native varargs and keep secrets out of files/arguments.</summary>
internal sealed class SecretServicePasswordStore : ILoginPasswordStore
{
    private const string Secret = "libsecret-1.so.0", Glib = "libglib-2.0.so.0", Gio = "libgio-2.0.so.0";
    private const string Attribute = "account-id";

    public Task<string?> LookupAsync(string key, CancellationToken token) => Task.Run(() => Invoke(key, token,
        (schema, attributes, cancellable) =>
        {
            var password = secret_password_lookupv_sync(schema, attributes, cancellable, out var error);
            try { CheckError(error); return password == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(password); }
            finally { if (password != IntPtr.Zero) secret_password_free(password); }
        }), token);

    public Task StoreAsync(string key, string label, string password, CancellationToken token) => Task.Run(() => Invoke(key, token,
        (schema, attributes, cancellable) =>
        {
            var stored = secret_password_storev_sync(schema, attributes, "default", label, password, cancellable, out var error);
            CheckError(error);
            if (stored == 0) throw new InvalidOperationException("The desktop keyring did not save the password.");
            return true;
        }), token);

    private static T Invoke<T>(string key, CancellationToken token, Func<IntPtr, IntPtr, IntPtr, T> operation)
    {
        IntPtr types = IntPtr.Zero, schema = IntPtr.Zero, attributes = IntPtr.Zero, cancellable = IntPtr.Zero;
        try
        {
            token.ThrowIfCancellationRequested();
            // GLib owns copied UTF-8 keys/values until each table is released.
            var library = NativeLibrary.Load(Glib);
            try
            {
                var hash = NativeLibrary.GetExport(library, "g_str_hash");
                var equal = NativeLibrary.GetExport(library, "g_str_equal");
                var free = NativeLibrary.GetExport(library, "g_free");
                types = g_hash_table_new_full(hash, equal, free, IntPtr.Zero);
                g_hash_table_insert(types, g_strdup(Attribute), IntPtr.Zero); // SECRET_SCHEMA_ATTRIBUTE_STRING = 0.
                schema = secret_schema_newv("org.pawprint.Viewer.Login", 0, types);
                attributes = g_hash_table_new_full(hash, equal, free, free);
                g_hash_table_insert(attributes, g_strdup(Attribute), g_strdup(key));
                cancellable = g_cancellable_new();
                using var registration = token.Register(() => g_cancellable_cancel(cancellable));
                token.ThrowIfCancellationRequested();
                var result = operation(schema, attributes, cancellable);
                token.ThrowIfCancellationRequested();
                return result;
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException("Password saving requires libsecret and a desktop Secret Service keyring.", ex);
        }
        catch (InvalidOperationException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally
        {
            if (cancellable != IntPtr.Zero) g_object_unref(cancellable);
            if (attributes != IntPtr.Zero) g_hash_table_unref(attributes);
            if (schema != IntPtr.Zero) secret_schema_unref(schema);
            if (types != IntPtr.Zero) g_hash_table_unref(types);
        }
    }

    private static void CheckError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        try
        {
            var details = Marshal.PtrToStructure<NativeError>(error);
            throw new InvalidOperationException("Desktop keyring: " + (Marshal.PtrToStringUTF8(details.Message) ?? "Password operation failed."));
        }
        finally { g_error_free(error); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeError { public uint Domain; public int Code; public IntPtr Message; }

    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_schema_newv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, IntPtr attributes);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_schema_unref(IntPtr schema);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password, IntPtr cancellable, out IntPtr error);
    [DllImport(Secret, CallingConvention = CallingConvention.Cdecl)]
    private static extern void secret_password_free(IntPtr password);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_hash_table_new_full(IntPtr hash, IntPtr equal, IntPtr keyDestroy, IntPtr valueDestroy);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_strdup([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(Glib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_error_free(IntPtr error);
    [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr g_cancellable_new();
    [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_cancellable_cancel(IntPtr cancellable);
    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    private static extern void g_object_unref(IntPtr instance);
}
