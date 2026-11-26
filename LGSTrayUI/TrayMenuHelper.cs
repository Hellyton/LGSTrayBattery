using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace LGSTrayUI;

/// <summary>
/// Opens the shared "SysTrayMenu" so it stays open.
///
/// On Windows 11, when a tray icon lives in the "^" overflow flyout, the flyout closes right
/// after the right-click and takes the foreground with it, so a menu opened immediately (as
/// Hardcodet does) is dismissed at once. We open it ourselves a moment later, after giving the
/// foreground to a tiny invisible host window owned by this process.
/// </summary>
public static class TrayMenuHelper
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

    private static Window? _host;

    public static void Attach(TaskbarIcon icon)
    {
        icon.ContextMenu = null; // disable Hardcodet's own (immediately-dismissed) menu
        icon.TrayRightMouseUp += async (_, _) =>
        {
            await Task.Delay(200); // let the overflow flyout finish closing
            Show();
        };
    }

    public static void Show()
    {
        var menu = (ContextMenu)Application.Current.FindResource("SysTrayMenu");
        if (menu.IsOpen) menu.IsOpen = false;

        var host = _host ??= CreateHost();
        host.Show();
        SetForegroundWindow(new WindowInteropHelper(host).Handle);

        menu.PlacementTarget = host;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static Window CreateHost()
    {
        var w = new Window
        {
            Title = "LGSTray Menu Host",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            Left = -100,
            Top = -100,
            Topmost = true,
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW); // keep out of Alt+Tab
        };

        var menu = (ContextMenu)Application.Current.FindResource("SysTrayMenu");
        menu.Closed += (_, _) => w.Hide();

        return w;
    }
}
