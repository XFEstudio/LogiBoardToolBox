using Windows.UI.ViewManagement;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;

namespace LogiBoardToolBox
{
    /// <summary>
    /// 主窗体
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public static UISettings UISettings { get; set; } = new();
        internal double DisplayScale => GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        public MainWindow()
        {
            this.InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)(1600 * scale), workArea.Width - 40), Math.Min((int)(900 * scale), workArea.Height - 40)));
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Black;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Black;
            AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
            if (Utilities.UiVerification.IsRequested)
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (IntPtr.Size == 8) SetWindowLongPtr(handle, -20, new(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000));
                else SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000);
                AppWindow.ResizeClient(new(Utilities.UiVerification.ViewportWidth, Utilities.UiVerification.ViewportHeight));
            }
            else if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Maximize();
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/appicon.ico"));
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
            UISettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(() => AppThemeHelper.ChangeTheme(AppThemeHelper.Theme));
            AppWindow.Closing += OnClosing;
        }
        private bool closing;
        private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (closing) return;
            args.Cancel = true; closing = true;
            try { await App.MouseWorkspace.FlushImmediateSaveAsync(); }
            catch (Exception ex) { closing = false; App.MouseWorkspace.ShowError(ex); return; }
            try { await App.MouseWorkspace.ShutdownAsync(); }
            catch (Exception ex) { Console.WriteLine("关闭时恢复设备：" + ex.Message); }
            finally { Close(); }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
