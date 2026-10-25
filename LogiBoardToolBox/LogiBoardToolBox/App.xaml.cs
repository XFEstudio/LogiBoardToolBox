using LogiBoardToolBox.Profiles.CacheProfiles;
using LogiBoardToolBox.Profiles.CrossVersionProfiles;
using LogiBoardToolBox.Utilities;
using XFEExtension.NetCore.WinUIHelper.Interface.Services;
using XFEExtension.NetCore.WinUIHelper.Utilities;
using XFEExtension.NetCore.WinUIHelper.Utilities.Helper;
using XFEExtension.NetCore.XFEConsole;

namespace LogiBoardToolBox
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 主页窗口
        /// </summary>
        public static MainWindow MainWindow { get; private set; } = null!;
        public static MainPageViewModel MouseWorkspace { get; } = new();

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            XFEConsole.UseXFEConsoleLog();
            XFEConsole.Log.LogPath = $@"{AppPath.LogDictionary}\{DateTime.Now:yyyy-M-d_HH-mm-ss}.log";
            Console.WriteLine("正在启动应用程序...");
            this.InitializeComponent();
            AppThemeHelper.Theme = SystemProfile.Theme;
            PageManager.RegisterPage(typeof(AppShellPage));
            PageManager.RegisterPage(typeof(MainPage));
            PageManager.RegisterPage(typeof(LogViewPage));
            PageManager.RegisterPage(typeof(SettingPage));
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is not Exception ex) return;
            Console.WriteLine($"[ERROR]应用程序发生错误:{ex.Message}");
            Console.WriteLine($"[TRACE]{ex.StackTrace}");
            if (ServiceManager.GetService<IMessageService>() is not { } messageService) return;
            messageService.ShowMessage(ex.Message, "发生错误", InfoBarSeverity.Error);
        }

        private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            if (UiVerification.IsCatalogRequested) UiVerification.CatalogUnhandledErrors.Add(e.Exception.ToString());
            Console.WriteLine($"[ERROR]应用程序发生错误:{e.Message}");
            Console.WriteLine($"[TRACE]{e.Exception.StackTrace}");
            if (ServiceManager.GetService<IMessageService>() is not { } messageService) return;
            messageService.ShowMessage(e.Message, "发生错误", InfoBarSeverity.Error);
            e.Handled = true;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            MainWindow = new();
            MainWindow.Closed += (_, _) =>
            {
                MouseWorkspace.Dispose();
                SystemProfile.SaveProfile();
                CacheProfile.SaveProfile();
            };
            MainWindow.Content = new AppShellPage();
            if (UiVerification.IsRequested)
            {
                var root = (FrameworkElement)MainWindow.Content;
                root.Width = UiVerification.ViewportWidth / MainWindow.DisplayScale; root.Height = UiVerification.ViewportHeight / MainWindow.DisplayScale;
                MainWindow.AppWindow.Show(false);
            }
            else if (Environment.GetCommandLineArgs().Contains("--background-start")) MainWindow.AppWindow.Show(false);
            else MainWindow.Activate();
            AppThemeHelper.MainWindow = MainWindow;
            Console.WriteLine("应用程序已启动");
        }
    }
}
