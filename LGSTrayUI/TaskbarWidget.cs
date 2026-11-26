using LGSTrayCore;
using LGSTrayPrimitives;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LGSTrayUI;

/// <summary>
/// Creates/destroys the taskbar widget according to the "Show on Taskbar" user setting.
/// </summary>
public class TaskbarWidgetManager : IDisposable
{
    private readonly UserSettingsWrapper _userSettings;
    private readonly LogiDeviceCollection _devices;
    private TaskbarWidget? _widget;

    public TaskbarWidgetManager(UserSettingsWrapper userSettings, ILogiDeviceCollection devices)
    {
        _userSettings = userSettings;
        _devices = (LogiDeviceCollection)devices;

        _userSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UserSettingsWrapper.TaskbarWidget))
            {
                Apply();
            }
        };

        Apply();
    }

    private void Apply()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_userSettings.TaskbarWidget)
            {
                if (_widget == null)
                {
                    _widget = new TaskbarWidget(_devices);
                    _widget.Show();
                }
            }
            else
            {
                _widget?.Close();
                _widget = null;
            }
        });
    }

    public void Dispose()
    {
        _widget?.Close();
        _widget = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Small borderless window docked on the taskbar, just left of the notification area,
/// showing "[icon] NN%" for every selected device.
/// </summary>
public class TaskbarWidget : Window
{
    #region Win32
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr val);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    #endregion

    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const int GapPx = 4;

    private readonly LogiDeviceCollection _devices;
    private readonly StackPanel _row;
    private readonly DispatcherTimer _positionTimer;
    private IntPtr _hwnd;
    private IntPtr _owner;

    public TaskbarWidget(LogiDeviceCollection devices)
    {
        _devices = devices;

        Title = "LGSTray Taskbar Widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // invisible but still hit-testable for right-click
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -10000;
        Top = -10000;

        _row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        Content = _row;
        // Don't use the ContextMenu property: the window is WS_EX_NOACTIVATE, so WPF's default
        // handling opens the menu without focus and it closes immediately. Open it ourselves
        // after taking the foreground (same trick tray icons use).
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMenu();
        };

        _devices.Devices.CollectionChanged += OnDevicesChanged;
        foreach (var d in _devices.Devices)
        {
            d.PropertyChanged += OnDevicePropertyChanged;
        }
        CheckTheme.StaticPropertyChanged += OnThemeChanged;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _positionTimer.Tick += (_, _) => Reposition();
        _positionTimer.Start();

        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        Closed += OnClosed;

        Rebuild();
    }

    private static void ShowMenu() => TrayMenuHelper.Show();

    private void OnClosed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        _devices.Devices.CollectionChanged -= OnDevicesChanged;
        foreach (var d in _devices.Devices)
        {
            d.PropertyChanged -= OnDevicePropertyChanged;
        }
        CheckTheme.StaticPropertyChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Rebuild);

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (LogiDevice d in e.NewItems) d.PropertyChanged += OnDevicePropertyChanged;
        }
        if (e.OldItems != null)
        {
            foreach (LogiDevice d in e.OldItems) d.PropertyChanged -= OnDevicePropertyChanged;
        }
        Dispatcher.BeginInvoke(Rebuild);
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogiDeviceViewModel.IsChecked) or nameof(LogiDevice.BatteryPercentage)
            or nameof(LogiDevice.PowerSupplyStatus) or nameof(LogiDevice.DeviceType) or nameof(LogiDevice.DeviceName))
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
    }

    private void Rebuild()
    {
        Brush fg = CheckTheme.LightTheme ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : Brushes.White;
        _row.Children.Clear();

        // Headsets first, then mice, then keyboards
        var selected = _devices.Devices
            .Where(d => d.IsChecked)
            .OrderBy(d => d.DeviceType switch { DeviceType.Headset => 0, DeviceType.Mouse => 1, _ => 2 })
            .ToList();

        foreach (var device in selected)
        {
            if (_row.Children.Count > 0)
            {
                _row.Children.Add(new Border { Width = 14 });
            }
            _row.Children.Add(BuildItem(device, fg));
        }

        if (selected.Count == 0)
        {
            _row.Children.Add(new TextBlock { Text = "LGSTray", Foreground = fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private static StackPanel BuildItem(LogiDevice device, Brush fg)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = device.ToolTipString };

        string glyph = device.DeviceType switch
        {
            DeviceType.Headset => "",
            DeviceType.Keyboard => "",
            _ => "",
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(IconFont),
            FontSize = 16,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 5, 0),
        });

        if (device.PowerSupplyStatus == PowerSupplyStatus.POWER_SUPPLY_STATUS_CHARGING)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily(IconFont),
                FontSize = 11,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 2, 0),
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = device.BatteryPercentage < 0 ? "--" : $"{Math.Round(device.BatteryPercentage)}%",
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || ForegroundIsFullscreen(tray))
        {
            if (IsVisible) Hide();
            return;
        }

        // Owned windows always stay above their owner, so making the taskbar our owner keeps
        // the widget on top of it without fighting it for the topmost z-order (which flickers
        // whenever the taskbar is activated, e.g. when opening the "^" overflow flyout).
        if (_owner != tray)
        {
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, tray);
            _owner = tray;
        }

        if (!IsVisible) Show();

        GetWindowRect(tray, out RECT tb);
        GetWindowRect(_hwnd, out RECT me);

        int right = tb.Right - 300;
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr))
        {
            right = nr.Left;
        }

        int w = me.Right - me.Left;
        int h = me.Bottom - me.Top;
        int x = right - w - GapPx;
        int y = tb.Top + ((tb.Bottom - tb.Top) - h) / 2;
        if (x == me.Left && y == me.Top) return; // already in place, don't touch z-order

        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Hide while a game/video is fullscreen so the widget never overlays it.</summary>
    private bool ForegroundIsFullscreen(IntPtr tray)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || fg == GetShellWindow()) return false;

        // Anything owned by the shell (taskbar, overflow flyout, Start, etc.) is never "fullscreen".
        GetWindowThreadProcessId(tray, out uint shellPid);
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == shellPid) return false;

        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return false;

        if (!GetWindowRect(fg, out RECT r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
            && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}
