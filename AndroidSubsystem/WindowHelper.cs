using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AndroidSubsystem;

/// <summary>
/// 顶层窗口操作：用来把模拟器窗口真正显示出来并置顶。
/// 只做纯 Win32 调用，不依赖任何外部库。
/// </summary>
internal static class WindowHelper
{
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int GWL_STYLE = -16;
    private const long WS_VISIBLE = 0x10000000;

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    /// <summary>找出标题包含关键字的顶层窗口句柄。</summary>
    public static List<IntPtr> FindByTitle(string keyword)
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsTopLevelAppWindow(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Length > 0 && title.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsTopLevelAppWindow(IntPtr hWnd)
    {
        // 只关心真正带标题的应用窗口（跳过 tool window / 不可见辅助窗口）
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;

        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.Length > 0;
    }

    public static bool IsVisible(IntPtr hWnd) => IsWindowVisible(hWnd);

    public static bool HasVisibleStyle(IntPtr hWnd) =>
        (GetWindowLong(hWnd, GWL_STYLE) & WS_VISIBLE) != 0;

    /// <summary>把窗口从隐藏/最小化状态恢复并置顶；若窗口坐标落在所有屏幕之外，先把它挪回主屏中央。</summary>
    public static void ShowAndActivate(IntPtr hWnd)
    {
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            if (!IsWindowVisible(hWnd)) ShowWindow(hWnd, SW_SHOW);
            EnsureOnScreen(hWnd);
            SetForegroundWindow(hWnd);
        }
        catch { }
    }

    /// <summary>
    /// 窗口完全落在虚拟屏幕之外（换过显示器/分辨率变小）时移回中央；
    /// 窗口比屏幕还高时收缩到屏幕高度以内，否则标题栏会被顶出屏幕。
    /// </summary>
    private static void EnsureOnScreen(IntPtr hWnd)
    {
        Rect r;
        if (!GetWindowRect(hWnd, out r)) return;

        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return;

        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;

        var maxW = vw - 40;
        var maxH = vh - 60;
        var resize = false;
        if (w > maxW) { w = maxW; resize = true; }
        if (h > maxH) { h = maxH; resize = true; }

        var visibleX = Math.Min(r.Right, vx + vw) - Math.Max(r.Left, vx);
        var visibleY = Math.Min(r.Bottom, vy + vh) - Math.Max(r.Top, vy);
        if (!resize && visibleX > 80 && visibleY > 60) return;

        var x = vx + Math.Max(0, (vw - w) / 2);
        var y = vy + Math.Max(0, (vh - h) / 3);
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_SHOWWINDOW);
    }

    public static string Describe(IntPtr hWnd)
    {
        try
        {
            Rect r;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (!GetWindowRect(hWnd, out r)) return sb.ToString();
            return $"{sb} [{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}] visible={IsWindowVisible(hWnd)}";
        }
        catch { return ""; }
    }
}
