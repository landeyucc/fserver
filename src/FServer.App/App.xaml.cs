using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace FServer.App
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();

        public App()
        {
#if DEBUG
            AllocConsole();
            Console.Title = "FServer FTP Debug Console";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("===== FServer FTP Debug Console =====");
            Console.ResetColor();
#endif
            DispatcherUnhandledException += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[EXCEPTION] {e.Exception}");
                Console.ResetColor();
                MessageBox.Show(e.Exception.ToString(), "未处理异常",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
#if DEBUG
            FreeConsole();
#endif
            base.OnExit(e);
        }
    }
}
