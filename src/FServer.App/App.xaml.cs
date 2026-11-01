using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FServer.App
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")] private static extern bool AllocConsole();
        [DllImport("kernel32.dll")] private static extern bool FreeConsole();

        private Mutex _singleInstanceMutex;

        /// <summary>托盘图标引用（MainWindow 注入）。Application 退出时必须 Hide，否则托盘残留灰色幽灵图标。</summary>
        internal System.Windows.Forms.NotifyIcon TrayIcon;

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
                ModernDialog.ShowError(e.Exception.ToString(), "未处理异常");
                e.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 单实例保护：Mutex Local\ 前缀（同 session 互斥，无需管理员提升）。
            // 已存在实例 → 弹提示 + 安静退出，不抢焦点、不打扰用户。
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, @"Local\FServer.Ftp.SingleInstance", out createdNew);
            if (!createdNew)
            {
                ModernDialog.ShowInfo(
                    "FServer FTP 已在运行中。\n请从系统托盘打开主窗口。",
                    "应用已启动");

                // ShowDialog 阻塞直到用户点确定，再排队 Shutdown 避免竞态
                Dispatcher.BeginInvoke((Action)Shutdown, DispatcherPriority.Background);
                return;
            }

            // 手动创建 MainWindow（App.xaml 已去掉 StartupUri，避免 WPF 框架强制 Show 导致黑闪屏）
            var main = new MainWindow();
            MainWindow = main;

            if (main.Vm.SilentStartup)
            {
                // WPF Hide 状态下不往 HWND 里 Render 像素，托盘双击 Show 时 OS 先画黑底再轮到 WPF。
                // 解法：屏幕外 Show() 触发首次 Render → ApplicationIdle 后 Hide，用户完全看不到。
                main.Left = -32000;
                main.Top = -32000;
                main.Show();

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    main.Hide();
                    main.Left = double.NaN;
                    main.Top = double.NaN;
                }), DispatcherPriority.ApplicationIdle);
            }
            else
            {
                main.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            // .NET Framework 不会自动清 NotifyIcon，必须显式销毁，否则托盘残留幽灵图标
            if (TrayIcon != null)
            {
                TrayIcon.Visible = false;
                TrayIcon.Dispose();
                TrayIcon = null;
            }
#if DEBUG
            FreeConsole();
#endif
            base.OnExit(e);
        }
    }
}
