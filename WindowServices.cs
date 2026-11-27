namespace InstantReplay;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT;
using WinRT.Interop;

internal static class Chrome
{
    public static void ApplyBackdrop(Window window, bool dark = true) => AttachCorner(window);

    public static void ReapplyBackdrop(Window window, bool dark = true) => AttachCorner(window);

    public static void UpdateBackdropTheme(Window window, bool dark) { }

    private static void AttachCorner(Window window)
    {
        try
        {
            TitleBarButtons.SetCornerPreference(WindowNative.GetWindowHandle(window), round: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"could not set corner preference: {ex.Message}");
        }
    }
}

public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public SettingRow()
    {
        DefaultStyleKey = typeof(SettingRow);
    }
}

public static class HotkeyDisplay
{
    private const uint VkOemComma = 0xBC, VkOemPeriod = 0xBE, VkOemQuestion = 0xBF,
                        VkOemSemicolon = 0xBA, VkOemQuotes = 0xDE, VkOemPlus = 0xBB, VkOemMinus = 0xBD,
                        VkMultiply = 0x6A, VkAdd = 0x6B, VkSubtract = 0x6D, VkDivide = 0x6F, VkDecimal = 0x6E,
                        VkNumPad0 = 0x60, VkNumPad9 = 0x69, VkD0 = 0x30, VkD9 = 0x39, VkSpace = 0x20;

    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & MainWindow.ModControl) != 0) parts.Add("ctrl");
        if ((modifiers & MainWindow.ModAlt) != 0) parts.Add("alt");
        if ((modifiers & MainWindow.ModShift) != 0) parts.Add("shift");
        if ((modifiers & MainWindow.ModWin) != 0) parts.Add("win");

        if (vk != 0) parts.Add(VkName(vk));
        else if (parts.Count == 0) parts.Add("...");

        return string.Join(" + ", parts);
    }

    public static string VkName(uint vk)
    {
        switch (vk)
        {
            case VkMultiply: return "*";
            case VkAdd: return "num +";
            case VkSubtract: return "num -";
            case VkDivide: return "num /";
            case VkDecimal: return "num .";
            case VkOemComma: return ",";
            case VkOemPeriod: return ".";
            case VkOemQuestion: return "/";
            case VkOemSemicolon: return ";";
            case VkOemQuotes: return "'";
            case VkOemPlus: return "+";
            case VkOemMinus: return "-";
            case VkSpace: return "space";
        }
        if (vk is >= VkNumPad0 and <= VkNumPad9) return "num " + (vk - VkNumPad0);
        if (vk is >= VkD0 and <= VkD9) return (vk - VkD0).ToString();

        return ((VirtualKey)vk).ToString().ToLowerInvariant();
    }

    public static uint? ModifierBitForKey(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => MainWindow.ModControl,
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => MainWindow.ModAlt,
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => MainWindow.ModShift,
        VirtualKey.LeftWindows or VirtualKey.RightWindows => MainWindow.ModWin,
        _ => null,
    };
}

internal static class TitleBarButtons
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsPopup = 0x80000000L;
    private const long WsSysMenu = 0x00080000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;

    public static void SetCornerPreference(IntPtr hwnd, bool round)
    {
        try
        {
            int preference = round ? DwmwcpRound : DwmwcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch { }
    }

    public static void StripWindowFrame(IntPtr hwnd)
    {
        try
        {
            long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            long next = (style & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox)) | WsPopup;
            SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(next));

            int noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));

            int corner = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch { }
    }

    public static void Apply(AppWindowTitleBar? tb, bool dark)
    {
        if (tb == null) return;

        tb.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        tb.InactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);

        if (dark)
        {
            tb.ButtonForegroundColor = Color.FromArgb(220, 245, 245, 247);
            tb.ButtonHoverBackgroundColor = Color.FromArgb(30, 255, 255, 255);
            tb.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            tb.ButtonPressedBackgroundColor = Color.FromArgb(20, 255, 255, 255);
            tb.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
            tb.InactiveForegroundColor = Color.FromArgb(120, 245, 245, 247);
        }
        else
        {
            tb.ButtonForegroundColor = Color.FromArgb(220, 29, 29, 31);
            tb.ButtonHoverBackgroundColor = Color.FromArgb(20, 0, 0, 0);
            tb.ButtonHoverForegroundColor = Color.FromArgb(255, 0, 0, 0);
            tb.ButtonPressedBackgroundColor = Color.FromArgb(32, 0, 0, 0);
            tb.ButtonPressedForegroundColor = Color.FromArgb(255, 0, 0, 0);
            tb.InactiveForegroundColor = Color.FromArgb(120, 29, 29, 31);
        }
    }
}

public static class ToastCenter
{
    public const string AumId = "ReplayApp.InstantReplay";
    private static readonly object s_lock = new();
    private static bool _registered;

    public static bool EnsureRegistered()
    {
        lock (s_lock)
        {
            if (_registered) return true;
            try
            {
                EnsureShortcut();
                EnsureRegistryEntry();
                SetCurrentProcessExplicitAppUserModelID(AumId);
                AppNotificationManager.Default.Register();
                _registered = true;
            }
            catch
            {
                _registered = false;
            }
            return _registered;
        }
    }

    public static bool Show(string title, string message)
    {
        try
        {
            if (!EnsureRegistered()) return false;
            var builder = new AppNotificationBuilder().AddText(title).AddText(message);
            AppNotificationManager.Default.Show(builder.BuildNotification());
            return true;
        }
        catch { return false; }
    }

    private static void EnsureShortcut()
    {
        string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "instant replay.lnk");
        if (File.Exists(shortcutPath)) return;

        var link = (IShellLinkW)new CShellLink();
        link.SetPath(Environment.ProcessPath!);
        link.SetArguments("");
        link.SetWorkingDirectory(Path.GetDirectoryName(Environment.ProcessPath!)!);
        link.SetIconLocation(Environment.ProcessPath!, 0);

        var propertyStore = (IPropertyStore)link;
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        using var value = PropVariant.FromString(AumId);
        propertyStore.SetValue(in key, in value);
        propertyStore.Commit();

        ((IPersistFile)link).Save(shortcutPath, false);
    }

    private static void EnsureRegistryEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AumId}");
        key.SetValue("DisplayName", "Instant Replay");
        key.SetValue("IconUri", Environment.ProcessPath!, RegistryValueKind.String);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(in PropertyKey key, in PropVariant pv);
        void Commit();
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid fmtid = fmtid;
        public readonly uint pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropVariant : IDisposable
    {
        private const ushort VT_LPWSTR = 31;
        public readonly ushort vt;
        public readonly ushort wReserved1, wReserved2, wReserved3;
        public readonly IntPtr pData, rawValue2, rawValue3;

        public static PropVariant FromString(string value) => new(VT_LPWSTR, Marshal.StringToCoTaskMemUni(value));
        private PropVariant(ushort vt, IntPtr data) { this.vt = vt; pData = data; }
        public void Dispose() { if (vt == VT_LPWSTR && pData != IntPtr.Zero) Marshal.FreeCoTaskMem(pData); }
    }
}

public enum BadgeKind { None, Recording, Paused, Error }

public static class BadgeGlyphs
{
    private const uint ImageIconTrueColor = 0x00000002;
    private const uint DibRgbColors = 0x00;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    private static BITMAPINFOHEADER TopDownBgra(int width, int height) => new()
    {
        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = width,
        biHeight = -height,
        biPlanes = 1,
        biBitCount = 32,
        biCompression = 0,
    };

    private static IntPtr CreateIconFromBgra(byte[] bgra, int width, int height)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dib = IntPtr.Zero, mask = IntPtr.Zero;
        GCHandle maskPin = default;
        try
        {
            BITMAPINFOHEADER bi = TopDownBgra(width, height);
            dib = CreateDIBSection(screen, ref bi, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
            Marshal.Copy(bgra, 0, bits, width * height * 4);
            var maskBits = new byte[((width + 15) / 16) * 2 * height];
            maskPin = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
            mask = CreateBitmap(width, height, 1, 1, maskPin.AddrOfPinnedObject());
            if (mask == IntPtr.Zero) return IntPtr.Zero;
            var ii = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = dib };
            return CreateIconIndirect(ref ii);
        }
        catch { return IntPtr.Zero; }
        finally
        {
            if (maskPin.IsAllocated) maskPin.Free();
            if (mask != IntPtr.Zero) DeleteObject(mask);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static IntPtr LoadAppIcon(int size)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "icon.ico");
        if (File.Exists(path))
        {
            IntPtr icon = LoadImageW(IntPtr.Zero, path, ImageIconTrueColor, size, size, 0x10);
            if (icon != IntPtr.Zero) return icon;
        }
        return IntPtr.Zero;
    }

    private static void DrawStatusEmblem(byte[] bgra, int size, BadgeKind kind, double fillFraction)
    {
        double c = size / 2.0;
        double outerR = Math.Max(3.0, size * 0.5 * fillFraction);
        double haloW = Math.Max(1.2, size * 0.085);
        double innerR = Math.Max(2.0, outerR - haloW);
        Color baseTop, baseBot;
        if (kind == BadgeKind.Recording)
        {
            baseTop = Color.FromArgb(255, 242, 245, 250);
            baseBot = Color.FromArgb(255, 206, 212, 222);
        }
        else if (kind == BadgeKind.Paused)
        {
            baseTop = Color.FromArgb(255, 42, 46, 56);
            baseBot = Color.FromArgb(255, 24, 27, 34);
        }
        else
        {
            baseTop = Color.FromArgb(255, 168, 174, 186);
            baseBot = Color.FromArgb(255, 118, 124, 136);
        }

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - c;
                double dy = y + 0.5 - c;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double haloCov = Math.Clamp(outerR - d + 0.5, 0, 1);
                if (haloCov <= 0) continue;

                double innerCov = Math.Clamp(innerR - d + 0.5, 0, 1);
                double tY = Math.Clamp((dy + innerR) / Math.Max(1.0, 2.0 * innerR), 0, 1);

                byte r = 13, g = 14, b = 20;
                double outA = haloCov * 0.94;

                if (innerCov > 0)
                {
                    double ir = baseTop.R + (baseBot.R - baseTop.R) * tY;
                    double ig = baseTop.G + (baseBot.G - baseTop.G) * tY;
                    double ib = baseTop.B + (baseBot.B - baseTop.B) * tY;

                    // Crisp glass highlight along inner top rim
                    double rimDist = Math.Abs(d - (innerR - 0.6));
                    if (dy < 0 && rimDist < 1.2)
                    {
                        double rim = (1.0 - rimDist / 1.2) * (1.0 - tY) * 0.52;
                        ir += (255 - ir) * rim;
                        ig += (255 - ig) * rim;
                        ib += (255 - ib) * rim;
                    }

                    if (kind == BadgeKind.Paused)
                    {
                        double barH = innerR * 0.44;
                        double barW = Math.Max(0.8, innerR * 0.14);
                        double gap = Math.Max(1.3, innerR * 0.25);
                        if (Math.Abs(dy) <= barH && (Math.Abs(Math.Abs(dx) - gap) <= barW))
                        {
                            ir = 200; ig = 205; ib = 214;
                        }
                    }
                    else if (kind == BadgeKind.Error)
                    {
                        bool topBar = dy >= -innerR * 0.48 && dy <= innerR * 0.10 && Math.Abs(dx) <= Math.Max(0.8, innerR * 0.13);
                        bool dot = Math.Sqrt(dx * dx + (dy - innerR * 0.36) * (dy - innerR * 0.36)) <= Math.Max(0.9, innerR * 0.14);
                        if (topBar || dot)
                        {
                            ir = 18; ig = 20; ib = 24;
                        }
                    }
                    else
                    {
                        double centerDot = Math.Sqrt(dx * dx + dy * dy);
                        double coreR = innerR * 0.34;
                        if (centerDot <= coreR)
                        {
                            double coreFade = Math.Clamp(1.0 - centerDot / Math.Max(0.5, coreR), 0, 1);
                            ir += (245 - ir) * coreFade * 0.32;
                            ig += (247 - ig) * coreFade * 0.32;
                            ib += (250 - ib) * coreFade * 0.32;
                        }
                    }

                    r = (byte)Math.Round(13 * (1 - innerCov) + ir * innerCov);
                    g = (byte)Math.Round(14 * (1 - innerCov) + ig * innerCov);
                    b = (byte)Math.Round(20 * (1 - innerCov) + ib * innerCov);
                    outA = Math.Max(outA, innerCov);
                }

                int o = (y * size + x) * 4;
                bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = (byte)Math.Round(Math.Clamp(outA, 0, 1) * 255);
            }
    }

    public static IntPtr CreateOverlayIcon(BadgeKind kind, int size)
    {
        if (kind == BadgeKind.None) return IntPtr.Zero;
        var bgra = new byte[size * size * 4];
        DrawStatusEmblem(bgra, size, kind, fillFraction: 0.88);
        return CreateIconFromBgra(bgra, size, size);
    }

    public static IntPtr CreateBadgeIcon(BadgeKind kind, int size)
    {
        IntPtr appIcon = LoadAppIcon(size);
        if (appIcon == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (!TryReadIconBgra(appIcon, size, out byte[] baseBgra)) return IntPtr.Zero;
            int badgeSize = Math.Max(11, (int)Math.Round(size * 0.46));
            var badge = new byte[badgeSize * badgeSize * 4];
            DrawStatusEmblem(badge, badgeSize, kind, fillFraction: 0.94);
            OverlayBgra(baseBgra, size, badge, badgeSize, size - badgeSize, size - badgeSize);
            return CreateIconFromBgra(baseBgra, size, size);
        }
        finally { DestroyIcon(appIcon); }
    }

    private static void OverlayBgra(byte[] baseBgra, int baseSize, byte[] badgeBgra, int badgeSize, int offsetX, int offsetY)
    {
        for (int y = 0; y < badgeSize; y++)
            for (int x = 0; x < badgeSize; x++)
            {
                int src = y * badgeSize + x;
                byte a = badgeBgra[src * 4 + 3];
                if (a == 0) continue;
                int dx = offsetX + x, dy = offsetY + y;
                if (dx < 0 || dy < 0 || dx >= baseSize || dy >= baseSize) continue;
                int dst = (dy * baseSize + dx) * 4;
                float t = a / 255f;
                baseBgra[dst + 0] = (byte)(badgeBgra[src * 4 + 0] * t + baseBgra[dst + 0] * (1 - t));
                baseBgra[dst + 1] = (byte)(badgeBgra[src * 4 + 1] * t + baseBgra[dst + 1] * (1 - t));
                baseBgra[dst + 2] = (byte)(badgeBgra[src * 4 + 2] * t + baseBgra[dst + 2] * (1 - t));
                baseBgra[dst + 3] = (byte)Math.Min(255, a + baseBgra[dst + 3] * (1 - t));
            }
    }

    private static bool TryReadIconBgra(IntPtr icon, int size, out byte[] bgra)
    {
        bgra = new byte[size * size * 4];
        if (!GetIconInfo(icon, out ICONINFO info)) return false;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return false;
            BITMAPINFOHEADER bi = TopDownBgra(size, size);
            buffer = Marshal.AllocHGlobal(bgra.Length);
            if (GetDIBits(screen, info.hbmColor, 0, (uint)size, buffer, ref bi, DibRgbColors) == 0) return false;
            Marshal.Copy(buffer, bgra, 0, bgra.Length);
            bool anyAlpha = false;
            for (int k = 3; k < bgra.Length; k += 4) if (bgra[k] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int k = 3; k < bgra.Length; k += 4) bgra[k] = 255;
            return true;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

public sealed class TaskbarBadge : IDisposable
{
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarInstance { }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwnd, IntPtr tab);
        void UnregisterTab(IntPtr hwnd);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr rect);
    }

    private readonly IntPtr _hwnd;
    private ITaskbarList3? _taskbar;
    private BadgeKind _kind = BadgeKind.None;
    private bool _taskbarVisible = true;

    public TaskbarBadge(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try { var instance = (ITaskbarList3)new TaskbarInstance(); instance.HrInit(); _taskbar = instance; }
        catch { }
    }

    public void SetTaskbarVisible(bool visible)
    {
        _taskbarVisible = visible;
        if (!visible) Clear();
        else _kind = BadgeKind.None;
    }
    public void Reset() => _kind = BadgeKind.None;

    public void SetOverlay(IntPtr icon, BadgeKind kind)
    {
        if (_kind == kind || _taskbar == null || !_taskbarVisible) return;
        _kind = kind;
        try
        {
            _taskbar.SetOverlayIcon(_hwnd, icon, kind switch
            {
                BadgeKind.Recording => "recording",
                BadgeKind.Paused => "paused",
                BadgeKind.Error => "error",
                _ => null,
            });
        }
        catch { }
    }

    public void Clear() => SetOverlay(IntPtr.Zero, BadgeKind.None);

    public void Dispose()
    {
        try { _taskbar?.SetOverlayIcon(_hwnd, IntPtr.Zero, null); } catch { }
        if (_taskbar != null && Marshal.IsComObject(_taskbar)) Marshal.ReleaseComObject(_taskbar);
        _taskbar = null;
    }
}

public sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    public const int IconInfo = 0x1, IconWarning = 0x2, IconError = 0x3;
    public const uint WM_TRAYICON = 0x8001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private const int Uid = 1;

    public event Action? DoubleClicked;
    public event Action? BalloonClicked;
    public Func<List<TrayMenuItem>>? MenuItemsProvider { get; set; }

    public TrayIcon(IntPtr hwnd, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _hIcon = LoadImageW(IntPtr.Zero, iconPath, 2, 16, 16, 0x10);
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = Uid,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = (int)WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip,
        };
        Shell_NotifyIcon(NimAdd, ref data);
        var version = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = Uid, uTimeoutOrVersion = 4 };
        Shell_NotifyIcon(NimSetVersion, ref version);
    }

    public void SetIcon(IntPtr icon)
    {
        IntPtr handle = icon == IntPtr.Zero ? _hIcon : icon;
        if (handle == IntPtr.Zero) return;
        var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = Uid, uFlags = NifIcon, hIcon = handle };
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void ShowBalloonTip(string title, string message, int iconFlag)
    {
        var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = Uid, uFlags = NifInfo, szInfo = message, szInfoTitle = title, dwInfoFlags = iconFlag };
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void HandleMessage(IntPtr lParam)
    {
        int evt = (int)lParam & 0xFFFF;
        if (evt == 0x0203) DoubleClicked?.Invoke();
        else if (evt == 0x0405) BalloonClicked?.Invoke();
        else if (evt is 0x0205 or 0x007B) ShowContextMenu();
    }

    private void ShowContextMenu()
    {
        var items = MenuItemsProvider?.Invoke();
        if (items == null || items.Count == 0) return;

        IntPtr menu = CreatePopupMenu();
        var actions = new Dictionary<uint, Action?>();
        uint id = 1000;
        foreach (var item in items)
        {
            if (item.IsSeparator) { AppendMenuW(menu, 0x800, 0, ""); continue; }
            uint flags = (item.IsChecked ? 0x08u : 0u) | (item.IsEnabled ? 0u : 0x01u);
            AppendMenuW(menu, flags, id, item.Text);
            actions[id++] = item.OnClick;
        }

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(menu, 0x0102, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        if (cmd != 0 && actions.TryGetValue((uint)cmd, out var action)) action?.Invoke();
    }

    public void Dispose()
    {
        var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = Uid };
        Shell_NotifyIcon(NimDelete, ref data);
    }
}

public sealed class TrayMenuItem
{
    public string Text { get; init; } = "";
    public Action? OnClick { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsChecked { get; init; }
    public bool IsEnabled { get; init; } = true;
    public static TrayMenuItem Separator() => new() { IsSeparator = true };
}

public static class UiHover
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> Wired = new();
    private static readonly Windows.UI.ViewManagement.UISettings SystemMotion = new();

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(UiHover),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static readonly DependencyProperty HoverIconToneProperty =
        DependencyProperty.RegisterAttached("HoverIconTone", typeof(string), typeof(UiHover),
            new PropertyMetadata("", OnHoverIconToneChanged));

    public static string GetHoverIconTone(DependencyObject obj) => (string)obj.GetValue(HoverIconToneProperty);
    public static void SetHoverIconTone(DependencyObject obj, string value) => obj.SetValue(HoverIconToneProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button || e.NewValue is not true) return;
        button.Loaded += (_, _) => Wire(button);
        if (button.IsLoaded) Wire(button);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> IconWired = new();
    private static readonly SolidColorBrush WhiteBrush = new(Color.FromArgb(255, 255, 255, 255));

    private static void OnHoverIconToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button || string.IsNullOrEmpty(e.NewValue as string)) return;
        button.Loaded += (_, _) => WireIconTone(button);
        if (button.IsLoaded) WireIconTone(button);
    }

    private static void WireIconTone(Button button)
    {
        if (!IconWired.TryAdd(button, new object())) return;
        void Apply(bool hover)
        {
            string tone = GetHoverIconTone(button);
            Brush? target = hover
                ? (tone == "White" ? WhiteBrush : ResolveThemeBrush(button, "TextPrimaryBrush"))
                : ResolveThemeBrush(button, "TextSecondaryBrush") ?? button.Foreground;
            if (target == null || button.Content is not DependencyObject root) return;
            RecolorShapes(root, target);
        }

        button.PointerEntered += (_, _) => Apply(true);
        button.PointerExited += (_, _) => Apply(false);
        button.PointerCaptureLost += (_, _) => Apply(false);
        button.ActualThemeChanged += (_, _) => Apply(false);
    }

    private static Brush? ResolveThemeBrush(FrameworkElement el, string key)
    {
        string themeKey = el.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(themeKey, out var obj) && obj is ResourceDictionary td &&
                td.TryGetValue(key, out var val) && val is Brush b)
                return b;
        }
        return null;
    }

    private static void RecolorShapes(DependencyObject parent, Brush brush)
    {
        if (parent is Microsoft.UI.Xaml.Shapes.Path p)
        {
            if (p.Fill != null) p.Fill = brush;
            if (p.Stroke != null) p.Stroke = brush;
        }
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++) RecolorShapes(VisualTreeHelper.GetChild(parent, i), brush);
    }

    private static void Wire(Button button)
    {
        if (!GetIsEnabled(button) || !Wired.TryAdd(button, new object())) return;
        button.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AnimateScale(button, 0.98f)), true);
        button.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AnimateScale(button, 1.0f)), true);
        button.AddHandler(UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AnimateScale(button, 1.0f)), true);
        button.AddHandler(UIElement.PointerCanceledEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => AnimateScale(button, 1.0f)), true);
        button.IsEnabledChanged += (_, _) => { if (!button.IsEnabled) AnimateScale(button, 1.0f); };
    }

    private static void AnimateScale(FrameworkElement element, float target)
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
        if (!SystemMotion.AnimationsEnabled || !GetIsEnabled(element) || element is Button { IsEnabled: false })
        {
            visual.StopAnimation("Scale");
            visual.Scale = System.Numerics.Vector3.One;
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.30f, 0.12f), new System.Numerics.Vector2(0.25f, 1));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, new System.Numerics.Vector3(target, target, 1), easing);
        animation.Duration = TimeSpan.FromMilliseconds(target < 1 ? 145 : 250);
        visual.CenterPoint = new System.Numerics.Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        visual.StartAnimation("Scale", animation);
    }
}

internal static class PlayerWebViewShared
{
    public static string ToPlainJson(string message)
    {
        string s = message;
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try { s = System.Text.Json.JsonSerializer.Deserialize<string>(s) ?? s; } catch { }
        }
        return s;
    }

    public static async Task WaitForLoadedAsync(FrameworkElement element, TimeSpan timeout)
    {
        if (element.IsLoaded) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = (_, _) => tcs.TrySetResult();
        element.Loaded += handler;
        try { await Task.WhenAny(tcs.Task, Task.Delay(timeout)); }
        finally { element.Loaded -= handler; }
    }
}

public sealed class ClipNotificationWindow : Window
{
    private static ClipNotificationWindow? _instance;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly Border _card;
    private readonly TextBlock _message;
    private readonly TextBlock _detail;
    private readonly FontIcon _statusIcon;
    private readonly TranslateTransform _contentOffset = new();
    private readonly DispatcherTimer _lifetime = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _motion = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private long _motionStart;
    private int _motionFrom, _motionTo, _motionDuration, _currentX, _shownX, _hiddenX, _y;
    private bool _leaving;
    private double _motionOpacity;
    private double _motionContentY;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private ClipNotificationWindow()
    {
        Title = "instant replay notification";
        _message = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 245, 247))
        };
        _detail = new TextBlock
        {
            Text = "saved to desktop",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 161, 161, 170))
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = "instant replay",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 140, 142, 152))
        });
        text.Children.Add(_message);
        text.Children.Add(_detail);

        _statusIcon = new FontIcon
        {
            Glyph = "\uE73E", FontSize = 17,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 184, 190, 203)),
        };

        var content = new Grid
        {
            ColumnSpacing = 14,
            Margin = new Thickness(18, 12, 18, 12),
            RenderTransform = _contentOffset
        };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(255, 43, 48, 59)), Child = _statusIcon,
        });
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 27, 30, 37)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content,
            RequestedTheme = ElementTheme.Dark,
            IsHitTestVisible = false
        };
        Content = _card;

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        }
        SetStyle(_hwnd, -20, new IntPtr((GetStyle(_hwnd, -20).ToInt64() | 0x08000080L) & ~0x00040000L));
        Chrome.ApplyBackdrop(this, dark: true);
        TitleBarButtons.StripWindowFrame(_hwnd);
        if (!SetWindowDisplayAffinity(_hwnd, 0x11)) Logger.Debug("notification capture exclusion unavailable.");

        _lifetime.Tick += (_, _) => { _lifetime.Stop(); BeginSlide(_hiddenX, 265, leaving: true); };
        _motion.Tick += (_, _) => AdvanceSlide();
        Closed += (_, _) => { _lifetime.Stop(); _motion.Stop(); _instance = null; };
    }

    public static void Show(int seconds, string category) => ShowResult(Math.Max(1, seconds), category, false);
    public static void ShowError(string? detail = null) => ShowResult(0, detail ?? "see the debug log for details", true);

    private static void ShowResult(int seconds, string category, bool failed)
    {
        try
        {
            _instance ??= new ClipNotificationWindow();
            _instance.Present(seconds, category, failed);
        }
        catch { Shutdown(); }
    }

    private void Present(int seconds, string category, bool failed)
    {
        bool visible = _lifetime.IsEnabled || _motion.IsEnabled;
        _lifetime.Stop(); _motion.Stop();
        _message.Text = failed ? "could not save replay" : $"saved the last {seconds} {(seconds == 1 ? "second" : "seconds")}";
        _detail.Text = failed ? category.ToLowerInvariant() : $"saved to {ReplayItem.FormatCategory(category)}";
        _statusIcon.Glyph = failed ? "\uE783" : "\uE73E";
        _statusIcon.Foreground = new SolidColorBrush(failed
            ? Color.FromArgb(255, 255, 153, 164)
            : Color.FromArgb(255, 184, 190, 203));

        var foreground = GetForegroundWindow();
        var display = DisplayArea.GetFromWindowId(
            foreground == IntPtr.Zero ? _appWindow.Id : Win32Interop.GetWindowIdFromWindow(foreground),
            DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        double scale = Math.Max(1, GetDpiForWindow(_hwnd) / 96.0);
        int width = Math.Min((int)Math.Round(340 * scale), area.Width - 24);
        int height = (int)Math.Round(84 * scale);
        int margin = (int)Math.Round(20 * scale);
        int shownX = area.X + margin;
        int y = area.Y + margin;
        bool sameAnchor = visible && _shownX == shownX && _y == y;
        _shownX = shownX;
        _hiddenX = _shownX - (int)Math.Round(12 * scale);
        _y = y;
        if (!sameAnchor)
        {
            _currentX = _uiSettings.AnimationsEnabled ? _hiddenX : _shownX;
            _card.Opacity = _uiSettings.AnimationsEnabled ? 0 : 1;
            _contentOffset.Y = _uiSettings.AnimationsEnabled ? 6 : 0;
        }
        _appWindow.MoveAndResize(new RectInt32(_currentX, _y, width, height));
        _appWindow.Show(false);
        SetWindowPos(_hwnd, new IntPtr(-1), _currentX, _y, 0, 0, 0x0011);
        BeginSlide(_shownX, 340, leaving: false);
    }

    private void BeginSlide(int destination, int milliseconds, bool leaving)
    {
        _motion.Stop();
        if (!_uiSettings.AnimationsEnabled)
        {
            if (leaving) HideSafely();
            else
            {
                _card.Opacity = 1;
                _contentOffset.Y = 0;
                _currentX = destination;
                SetWindowPos(_hwnd, new IntPtr(-1), _currentX, _y, 0, 0, 0x0011);
                _lifetime.Start();
            }
            return;
        }
        _motionFrom = _currentX; _motionTo = destination;
        _motionOpacity = _card.Opacity;
        _motionContentY = _contentOffset.Y;
        _motionDuration = milliseconds; _leaving = leaving;
        _motionStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _motion.Start();
    }

    private void AdvanceSlide()
    {
        try
        {
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_motionStart).TotalMilliseconds;
            double progress = Math.Clamp(elapsed / _motionDuration, 0, 1);
            double eased = progress * progress * (3 - 2 * progress);
            _currentX = (int)Math.Round(_motionFrom + (_motionTo - _motionFrom) * eased);
            _card.Opacity = _motionOpacity + ((_leaving ? 0 : 1) - _motionOpacity) * eased;
            _contentOffset.Y = _motionContentY + ((_leaving ? -4 : 0) - _motionContentY) * eased;
            if (!SetWindowPos(_hwnd, new IntPtr(-1), _currentX, _y, 0, 0, 0x0011))
            {
                HideSafely();
                return;
            }
            if (progress < 1) return;
            _motion.Stop();
            if (_leaving) HideSafely(); else _lifetime.Start();
        }
        catch { HideSafely(); }
    }

    private void HideSafely()
    {
        _lifetime.Stop(); _motion.Stop();
        try
        {
            _appWindow.Hide();
            SetWindowPos(_hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
        }
        catch { Shutdown(); }
    }

    public static void Shutdown()
    {
        var window = _instance; _instance = null;
        if (window == null) return;
        window._lifetime.Stop(); window._motion.Stop();
        try { window.Close(); } catch { }
    }

    public static int RecordedSeconds(string? path, int fallback)
    {
        try
        {
            using var stream = File.OpenRead(path!);
            using var reader = new BinaryReader(stream);
            uint U32() => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
            ulong U64() => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(reader.ReadBytes(8));
            long end = stream.Length;
            while (stream.Position + 8 <= end)
            {
                long start = stream.Position;
                ulong length = U32(); string type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (length == 1) length = U64();
                if (length == 0) length = (ulong)(end - start);
                if (length < (ulong)(stream.Position - start) || length > (ulong)(end - start)) break;
                if (type == "moov") { end = start + (long)length; continue; }
                if (type == "mvhd")
                {
                    int version = reader.ReadByte(); stream.Position += 3;
                    if (version != 0 && version != 1) break;
                    stream.Position += version == 1 ? 16 : 8;
                    uint timescale = U32(); ulong duration = version == 1 ? U64() : U32();
                    if (timescale == 0) break;
                    return (int)Math.Clamp(Math.Round((double)duration / timescale), 1, int.MaxValue);
                }
                stream.Position = start + (long)length;
            }
        }
        catch { }
        return Math.Max(1, fallback);
    }
}

public sealed class PlayerFullScreenWindow : Window
{
    private readonly WebView2 _web = new();
    public PlayerFullScreenWindow()
    {
        Title = "instant replay";
        _web.DefaultBackgroundColor = Colors.Black;
        Content = _web;
    }

    public bool WebReady { get; private set; }
    public event Action<bool>? NavigationCompleted;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetOwnerLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public async Task OpenAsync(string url, Action<string> onMessage, IntPtr ownerHwnd = default)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (ownerHwnd != IntPtr.Zero) SetOwnerLongPtr(hwnd, -8, ownerHwnd);
            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            appWindow?.SetPresenter(AppWindowPresenterKind.FullScreen);
            Activate();

            await PlayerWebViewShared.WaitForLoadedAsync(_web, TimeSpan.FromSeconds(6));
            await _web.EnsureCoreWebView2Async();
            var cwv = _web.CoreWebView2!;
            cwv.Settings.IsZoomControlEnabled = false;
            cwv.Settings.AreBrowserAcceleratorKeysEnabled = false;
            cwv.WebMessageReceived += (_, e) => onMessage(PlayerWebViewShared.ToPlainJson(e.WebMessageAsJson ?? ""));
            cwv.NavigationCompleted += (_, e) =>
            {
                try { _web.Focus(FocusState.Programmatic); } catch { }
                NavigationCompleted?.Invoke(e.IsSuccess);
            };
            cwv.Navigate(url);
            try { _web.Focus(FocusState.Programmatic); } catch { }
            WebReady = true;
        }
        catch (Exception ex) { Logger.Error($"fullscreen webview2 init failed: {ex.Message}", ex); }
    }

    public void SendScript(string js)
    {
        try { if (_web.CoreWebView2 != null) _ = _web.CoreWebView2.ExecuteScriptAsync(js); }
        catch { }
    }

    public void Shutdown()
    {
        try { _web.Close(); } catch { }
        try { Close(); } catch { }
    }
}
