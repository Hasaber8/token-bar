using System.Runtime.InteropServices;

namespace TokenBar.Core;

// Windows ships SQLite. Use its UTF-8 API and READONLY flag; never create or
// modify an agent database. No native dependency needs to be redistributed.
public sealed class SqliteDatabase : IDisposable
{
    private IntPtr handle;
    public SqliteDatabase(string path, bool readOnly = true)
    {
        var status = Native.sqlite3_open_v2(path, out handle, readOnly ? 1 : 6, IntPtr.Zero);
        if (status != 0) { var error = Error(); Dispose(); throw new IOException(error); }
        Native.sqlite3_busy_timeout(handle, 1000);
    }
    private string Error() => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(handle)) ?? "SQLite error";
    public IEnumerable<string?[]> Query(string sql, params long[] parameters)
    {
        if (Native.sqlite3_prepare_v2(handle, sql, -1, out var stmt, IntPtr.Zero) != 0) throw new IOException(Error());
        try
        {
            for (var i = 0; i < parameters.Length; i++)
                if (Native.sqlite3_bind_int64(stmt, i + 1, parameters[i]) != 0) throw new IOException(Error());
            int status;
            while ((status = Native.sqlite3_step(stmt)) == 100)
            {
                var row = new string?[Native.sqlite3_column_count(stmt)];
                for (var i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(stmt, i));
                yield return row;
            }
            if (status != 101) throw new IOException(Error());
        }
        finally { Native.sqlite3_finalize(stmt); }
    }
    public HashSet<string> Columns(string table) => Query($"PRAGMA table_info(\"{table}\")").Select(row => row[1]!).ToHashSet(StringComparer.Ordinal);
    public void Dispose() { if (handle != IntPtr.Zero) { Native.sqlite3_close(handle); handle = IntPtr.Zero; } }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
    }
}
