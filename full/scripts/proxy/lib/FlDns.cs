using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using YamlDotNet.Serialization;

namespace FlDns {
  public static class YamlReader {
    public static string ToJson(string yaml) {
      object doc = new DeserializerBuilder().Build().Deserialize(yaml, typeof(object));
      return new SerializerBuilder().JsonCompatible().Build().Serialize(doc);
    }
  }
  public sealed class SqliteDb : IDisposable {
    private IntPtr db;
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
















    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_open_v2Delegate(byte[] path, out IntPtr db, int flags, IntPtr vfs);
    private static sqlite3_open_v2Delegate sqlite3_open_v2;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_closeDelegate(IntPtr db);
    private static sqlite3_closeDelegate sqlite3_close;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_busy_timeoutDelegate(IntPtr db, int ms);
    private static sqlite3_busy_timeoutDelegate sqlite3_busy_timeout;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr sqlite3_errmsgDelegate(IntPtr db);
    private static sqlite3_errmsgDelegate sqlite3_errmsg;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_prepare_v2Delegate(IntPtr db, byte[] sql, int count, out IntPtr stmt, IntPtr tail);
    private static sqlite3_prepare_v2Delegate sqlite3_prepare_v2;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_stepDelegate(IntPtr stmt);
    private static sqlite3_stepDelegate sqlite3_step;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_finalizeDelegate(IntPtr stmt);
    private static sqlite3_finalizeDelegate sqlite3_finalize;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_column_countDelegate(IntPtr stmt);
    private static sqlite3_column_countDelegate sqlite3_column_count;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr sqlite3_column_nameDelegate(IntPtr stmt, int col);
    private static sqlite3_column_nameDelegate sqlite3_column_name;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_column_typeDelegate(IntPtr stmt, int col);
    private static sqlite3_column_typeDelegate sqlite3_column_type;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate Int64 sqlite3_column_int64Delegate(IntPtr stmt, int col);
    private static sqlite3_column_int64Delegate sqlite3_column_int64;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate double sqlite3_column_doubleDelegate(IntPtr stmt, int col);
    private static sqlite3_column_doubleDelegate sqlite3_column_double;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr sqlite3_column_textDelegate(IntPtr stmt, int col);
    private static sqlite3_column_textDelegate sqlite3_column_text;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr sqlite3_backup_initDelegate(IntPtr dest, byte[] destName, IntPtr src, byte[] srcName);
    private static sqlite3_backup_initDelegate sqlite3_backup_init;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_backup_stepDelegate(IntPtr backup, int pages);
    private static sqlite3_backup_stepDelegate sqlite3_backup_step;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int sqlite3_backup_finishDelegate(IntPtr backup);
    private static sqlite3_backup_finishDelegate sqlite3_backup_finish;
    private static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
    private static string Text(IntPtr p) {
      if (p == IntPtr.Zero) return null;
      int n=0; while (Marshal.ReadByte(p,n)!=0) n++;
      byte[] bytes=new byte[n]; Marshal.Copy(p,bytes,0,n); return Encoding.UTF8.GetString(bytes);
    }
    [DllImport("kernel32", CharSet=CharSet.Ansi, ExactSpelling=true, SetLastError=true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    private static IntPtr Export(IntPtr module, string name) {
      IntPtr function=GetProcAddress(module,name);
      if(function==IntPtr.Zero) throw new IOException("SQLite library lacks required API: "+name);
      return function;
    }
    public static void Load(string path) {
      // Resolve exports from the exact handle; read-only previews and fixtures use system winsqlite3.
      IntPtr module=LoadLibraryEx(Path.GetFullPath(path),IntPtr.Zero,8);
      if(module==IntPtr.Zero) throw new IOException("Cannot load selected SQLite library. Use matching 64-bit Windows PowerShell.");
      sqlite3_open_v2 = (sqlite3_open_v2Delegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_open_v2"),typeof(sqlite3_open_v2Delegate));
      sqlite3_close = (sqlite3_closeDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_close"),typeof(sqlite3_closeDelegate));
      sqlite3_busy_timeout = (sqlite3_busy_timeoutDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_busy_timeout"),typeof(sqlite3_busy_timeoutDelegate));
      sqlite3_errmsg = (sqlite3_errmsgDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_errmsg"),typeof(sqlite3_errmsgDelegate));
      sqlite3_prepare_v2 = (sqlite3_prepare_v2Delegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_prepare_v2"),typeof(sqlite3_prepare_v2Delegate));
      sqlite3_step = (sqlite3_stepDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_step"),typeof(sqlite3_stepDelegate));
      sqlite3_finalize = (sqlite3_finalizeDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_finalize"),typeof(sqlite3_finalizeDelegate));
      sqlite3_column_count = (sqlite3_column_countDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_column_count"),typeof(sqlite3_column_countDelegate));
      sqlite3_column_name = (sqlite3_column_nameDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_column_name"),typeof(sqlite3_column_nameDelegate));
      sqlite3_column_type = (sqlite3_column_typeDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_column_type"),typeof(sqlite3_column_typeDelegate));
      sqlite3_column_int64 = (sqlite3_column_int64Delegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_column_int64"),typeof(sqlite3_column_int64Delegate));
      sqlite3_column_double = (sqlite3_column_doubleDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_column_double"),typeof(sqlite3_column_doubleDelegate));
      sqlite3_column_text = (sqlite3_column_textDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_column_text"),typeof(sqlite3_column_textDelegate));
      sqlite3_backup_init = (sqlite3_backup_initDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_backup_init"),typeof(sqlite3_backup_initDelegate));
      sqlite3_backup_step = (sqlite3_backup_stepDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_backup_step"),typeof(sqlite3_backup_stepDelegate));
      sqlite3_backup_finish = (sqlite3_backup_finishDelegate)Marshal.GetDelegateForFunctionPointer(Export(module,"sqlite3_backup_finish"),typeof(sqlite3_backup_finishDelegate));
    }
    public SqliteDb(string path, bool writable) {
      int rc=sqlite3_open_v2(Utf8(path),out db,writable ? 6 : 1,IntPtr.Zero);
      if(rc!=0) { string msg=Text(sqlite3_errmsg(db)); Dispose(); throw new IOException(msg); }
      sqlite3_busy_timeout(db,3000);
    }
    public List<Dictionary<string,object>> Query(string sql) {
      IntPtr stmt;
      if(sqlite3_prepare_v2(db,Utf8(sql),-1,out stmt,IntPtr.Zero)!=0) throw new IOException(Text(sqlite3_errmsg(db)));
      var rows=new List<Dictionary<string,object>>();
      try {
        int rc;
        while ((rc=sqlite3_step(stmt))==100) {
          var row=new Dictionary<string,object>();
          for(int i=0;i<sqlite3_column_count(stmt);i++) {
            object val=null;
            switch(sqlite3_column_type(stmt,i)) {
              case 1: val=sqlite3_column_int64(stmt,i); break;
              case 2: val=sqlite3_column_double(stmt,i); break;
              case 3: val=Text(sqlite3_column_text(stmt,i)); break;
              case 4: throw new IOException("Unexpected BLOB in configuration metadata.");
            }
            row[Text(sqlite3_column_name(stmt,i))]=val;
          }
          rows.Add(row);
        }
        if(rc!=101) throw new IOException(Text(sqlite3_errmsg(db)));
        return rows;
      } finally { sqlite3_finalize(stmt); }
    }
    public void Execute(string sql) { Query(sql); }
    public void Backup(string path) {
      using(var dest=new SqliteDb(path,true)) {
        IntPtr b=sqlite3_backup_init(dest.db,Utf8("main"),db,Utf8("main"));
        if(b==IntPtr.Zero) throw new IOException(Text(sqlite3_errmsg(dest.db)));
        int rc=0;
        try {
          for(int attempts=0;attempts<100;attempts++) {
            rc=sqlite3_backup_step(b,-1);
            if(rc!=5 && rc!=6) break;
            System.Threading.Thread.Sleep(30);
          }
        } finally { int finish=sqlite3_backup_finish(b); if(rc==101 && finish!=0) rc=finish; }
        if(rc!=101) throw new IOException("SQLite backup failed: " + rc);
      }
    }
    public void Dispose() { if(db!=IntPtr.Zero) { sqlite3_close(db); db=IntPtr.Zero; } }
  }
}
