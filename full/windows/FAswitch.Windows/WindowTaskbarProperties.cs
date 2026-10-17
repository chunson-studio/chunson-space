using System.Runtime.InteropServices;

namespace FAswitch.Windows;

public sealed record WindowTaskbarIdentity(string AppId, string IconResource, string RelaunchCommand, string DisplayName);

public sealed class NativeWindowTaskbarProperties
{
    public bool Apply(nint window, WindowTaskbarIdentity identity)
    {
        if (window == 0 || new[] { identity.AppId, identity.IconResource, identity.RelaunchCommand, identity.DisplayName }.Any(string.IsNullOrWhiteSpace)) return false;
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(window, ref iid, out store) < 0) return false;
            // Set the relaunch values before assigning the explicit taskbar group.
            (uint Id, string Text)[] values = [(2, identity.RelaunchCommand), (3, identity.IconResource), (4, identity.DisplayName), (5, identity.AppId)];
            foreach (var value in values)
            {
                var key = new PropertyKey(value.Id); var property = PropVariant.FromString(value.Text);
                try { if (store.SetValue(ref key, ref property) < 0) return false; }
                finally { PropVariantClear(ref property); }
            }
            if (store.Commit() < 0) return false;
            foreach (var value in values)
            {
                var key = new PropertyKey(value.Id);
                if (store.GetValue(ref key, out var property) < 0) return false;
                try { if (property.Type != 31 || Marshal.PtrToStringUni(property.Pointer) != value.Text) return false; }
                finally { PropVariantClear(ref property); }
            }
            return true;
        }
        catch (COMException) { return false; }
        finally { if (store is not null) Marshal.FinalReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(uint id)
    {
        public Guid Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        public uint Id = id;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
        public static PropVariant FromString(string value) => new() { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant property);
}
