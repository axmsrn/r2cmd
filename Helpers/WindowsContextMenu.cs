using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace R2Cmd;

// Native Windows Explorer context menu (7-Zip, antivirus, "Properties", "Send to",
// "Format" for drives, ...).
//
// Path resolution goes through SHParseDisplayName + SHBindToParent: the shell itself
// works out the parent folder, so drive roots ("C:\") need no special casing.
//
// IContextMenu2/3 must receive menu messages while the popup is open, otherwise
// submenus ("Send to", "Open with") come up empty — that is what MenuMessageWindow does.
public static class WindowsContextMenu
{
    // Opens the native Windows "Properties" dialog for a path (Alt+Enter).
    public static void ShowProperties(string? path, Window? ownerWindow)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return;
        if (path.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase)) return;

        path = path.Replace('/', '\\');

        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOCLOSEPROCESS,
            hwnd = ownerWindow != null ? new WindowInteropHelper(ownerWindow).Handle : IntPtr.Zero,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOWNORMAL
        };

        try { ShellExecuteEx(ref info); } catch { }
    }

    // Shows the menu for one path.
    public static bool Show(string? path, Window? ownerWindow)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return Show(new[] { path }, ownerWindow);
    }

    // =========================================================================
    // Commands after which the selection is still needed: the clipboard ones
    // are followed by a paste, and Properties only looks. Canonical shell verbs,
    // which do not depend on the Windows display language.
    // =========================================================================
    private static readonly HashSet<string> s_keepSelectionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "properties", "copy", "cut", "copyaspath"
    };

    // Shows the menu for several items. All of them must live in the same folder —
    // that is how the shell context menu works; the parent is taken from the first one.
    //
    // Returns true when a command that acts on the items was chosen (an archiver's
    // "Add to archive", Delete, Send to, ...), so the caller can clear the marks,
    // as it does after its own operations. The command itself usually runs in
    // another process, so "chosen" is all that can be known, not "finished".
    public static bool Show(IEnumerable<string> paths, Window? ownerWindow)
    {
        var list = paths?
            .Where(p => !string.IsNullOrEmpty(p))
            .Where(p => !p.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.StartsWith(@"\\Network", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Replace('/', '\\'))
            .ToList();

        if (list == null || list.Count == 0) return false;

        var fullPidls = new List<IntPtr>();
        var childPidls = new List<IntPtr>();
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        IntPtr hMenu = IntPtr.Zero;
        MenuMessageWindow? msgWindow = null;

        try
        {
            Guid iidIShellFolder = typeof(IShellFolder).GUID;

            foreach (string p in list)
            {
                if (SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0) continue;
                fullPidls.Add(pidl);

                // The parent folder is taken once, from the first item that resolves.
                if (parentFolder == null)
                {
                    if (SHBindToParent(pidl, ref iidIShellFolder, out parentFolder, out IntPtr childPidl) != 0)
                    {
                        parentFolder = null;
                        continue;
                    }
                    childPidls.Add(childPidl);   // owned by the full pidl, must not be freed separately
                }
                else
                {
                    if (SHBindToParent(pidl, ref iidIShellFolder, out IShellFolder _, out IntPtr childPidl) == 0)
                        childPidls.Add(childPidl);
                }
            }

            if (parentFolder == null || childPidls.Count == 0) return false;

            IntPtr hwnd = ownerWindow != null ? new WindowInteropHelper(ownerWindow).Handle : IntPtr.Zero;

            Guid iidIContextMenu = typeof(IContextMenu).GUID;
            parentFolder.GetUIObjectOf(hwnd, (uint)childPidls.Count, childPidls.ToArray(),
                                       ref iidIContextMenu, IntPtr.Zero, out contextMenu);
            if (contextMenu == null) return false;

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return false;

            contextMenu.QueryContextMenu(hMenu, 0, CmdFirst, CmdLast, CMF_NORMAL | CMF_EXPLORE);

            // Submenus are populated only if the shell handler gets menu messages.
            msgWindow = new MenuMessageWindow(contextMenu);

            GetCursorPos(out POINT pt);
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);

            int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                       pt.X, pt.Y, msgWindow.Handle, IntPtr.Zero);
            if (cmd <= 0) return false;   // dismissed

            // Read before invoking: some handlers release their state afterwards
            string verb = GetVerb(contextMenu, (uint)(cmd - CmdFirst));

            var info = new CMINVOKECOMMANDINFO
            {
                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                hwnd = hwnd,
                lpVerb = (IntPtr)(cmd - CmdFirst),
                nShow = SW_SHOWNORMAL
            };
            contextMenu.InvokeCommand(ref info);

            return !s_keepSelectionVerbs.Contains(verb);
        }
        catch
        {
            // any shell failure — just show nothing
            return false;
        }
        finally
        {
            msgWindow?.Dispose();
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (contextMenu != null) Marshal.ReleaseComObject(contextMenu);
            if (parentFolder != null) Marshal.ReleaseComObject(parentFolder);
            foreach (var p in fullPidls) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    // The language-independent verb of a menu command ("copy", "properties").
    // Third-party handlers often have none; that counts as acting on the items.
    private static string GetVerb(IContextMenu menu, uint commandOffset)
    {
        const int capacity = 256;
        IntPtr buffer = Marshal.AllocHGlobal(capacity * 2);
        try
        {
            int hr = menu.GetCommandString(commandOffset, GCS_VERBW, 0, buffer, capacity);
            return hr == 0 ? Marshal.PtrToStringUni(buffer) ?? "" : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Tiny message-only window that forwards menu messages to IContextMenu2/3.
    // TrackPopupMenuEx is given this window as owner, so the messages land here.
    private sealed class MenuMessageWindow : IDisposable
    {
        private readonly HwndSource _source;
        private readonly IContextMenu _menu;

        public IntPtr Handle => _source.Handle;

        public MenuMessageWindow(IContextMenu menu)
        {
            _menu = menu;
            _source = new HwndSource(new HwndSourceParameters("R2CmdShellMenu")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            });
            _source.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
            {
                if (_menu is IContextMenu3 m3)
                {
                    m3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr res);
                    handled = true;
                    return res;
                }
                if (_menu is IContextMenu2 m2)
                {
                    m2.HandleMenuMsg((uint)msg, wParam, lParam);
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
    }

    // ---- constants ----------------------------------------------------------
    private const uint CmdFirst = 1;
    private const uint CmdLast = 0x7FFF;
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXPLORE = 0x00000004;
    private const uint GCS_VERBW = 0x00000004;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int SW_SHOWNORMAL = 1;

    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;

    // ---- COM ----------------------------------------------------------------
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl,
            ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
            [In] ref Guid riid, IntPtr rgfReserved, out IContextMenu ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(uint idcmd, uint uflags, uint pwReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(uint idcmd, uint uflags, uint pwReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint iMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        [PreserveSig] int GetCommandString(uint idcmd, uint uflags, uint pwReserved, IntPtr pszName, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    // ---- P/Invoke -----------------------------------------------------------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, [In] ref Guid riid, out IShellFolder ppv,
        out IntPtr ppidlLast);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y,
        IntPtr hwnd, IntPtr lptpm);
}
