using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Application = System.Windows.Application;

namespace ScreenshotSaver
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool startMinimized = e.Args.Any(arg => 
                arg.Equals("--background", StringComparison.OrdinalIgnoreCase) || 
                arg.Equals("-b", StringComparison.OrdinalIgnoreCase)
            );

            var mainWindow = new MainWindow(startMinimized);
            
            // Create HWND handle programmatically so WndProc hook is active even if hidden
            var helper = new WindowInteropHelper(mainWindow);
            helper.EnsureHandle();

            if (!startMinimized)
            {
                mainWindow.Show();
                mainWindow.Activate();
            }
        }
    }
}

