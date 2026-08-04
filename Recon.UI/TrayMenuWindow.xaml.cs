using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Recon.UI;

public partial class TrayMenuWindow : Window
{
    #region P/Invoke

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    const int GWL_EXSTYLE    = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WH_MOUSE_LL    = 14;
    const nint WM_LBUTTONDOWN = 0x0201;
    const nint WM_RBUTTONDOWN = 0x0204;

    #endregion

    private LowLevelMouseProc? _hookProc; // field keeps delegate alive — DO NOT inline
    private IntPtr _hook;

    public TrayMenuWindow()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Make the window non-activating: it shows without stealing focus from
        // whatever was active (tray shell, other app). Hover can never trigger
        // OnDeactivated because we were never activated in the first place.
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            // Install a global mouse hook so we can detect clicks anywhere on
            // screen and close the menu when the user clicks outside it.
            _hookProc = MouseHookCallback;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        }
        else
        {
            RemoveHook();
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsVisible && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN))
        {
            var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            GetWindowRect(new WindowInteropHelper(this).Handle, out var r);

            bool outsideWindow = ms.pt.x < r.left || ms.pt.x > r.right
                              || ms.pt.y < r.top  || ms.pt.y > r.bottom;
            if (outsideWindow)
                Dispatcher.BeginInvoke(Hide);
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // Hide after clicking a menu action, but not when toggling a module checkbox.
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (e.OriginalSource is DependencyObject src && IsInsideCheckBox(src))
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Hide);
    }

    private static bool IsInsideCheckBox(DependencyObject obj)
    {
        while (obj != null)
        {
            if (obj is System.Windows.Controls.CheckBox) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    protected override void OnClosed(EventArgs e)
    {
        RemoveHook();
        base.OnClosed(e);
    }

    public void ShowAtTrayPosition()
    {
        var workArea = SystemParameters.WorkArea;
        // Use cached size on subsequent calls; approximate on first
        double w = ActualWidth > 0 ? ActualWidth : 276;
        double h = ActualHeight > 0 ? ActualHeight : 400;
        Left = workArea.Right - w - 16;
        Top  = workArea.Bottom - h - 16;
        Show();
        // Reposition precisely after WPF measures the actual size
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Left = workArea.Right - ActualWidth - 16;
            Top  = workArea.Bottom - ActualHeight - 16;
        }));
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
