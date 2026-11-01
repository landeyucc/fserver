using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FServer.App.ViewModels;

using WinForms = System.Windows.Forms;

namespace FServer.App
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        /// <summary>公开给 App 层读 SilentStartup 等开关。</summary>
        public MainViewModel Vm => _vm;
        private ScrollViewer _logScroll;
        /// <summary>用户是否正在看历史日志（向上滚动）。true 时不自动拉到底。</summary>
        private bool _userScrollingUp;

        private WinForms.NotifyIcon _trayIcon;
        private WinForms.ToolStripMenuItem _trayStartMenuItem;
        private WinForms.ToolStripMenuItem _trayStopMenuItem;
        private bool _appExiting;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            // 所有后台初始化放构造函数里，不依赖 Loaded（静默启动时 MainWindow 永远不 Show → Loaded 永远不触发）
            _vm.LogLines.CollectionChanged += (s, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    AutoScrollIfNeeded();
            };
            _vm.StartLogAutoCleanup();
            _vm.RefreshHistoryLogs();
            InitTrayIcon();

            if (_vm.SilentStartup)
            {
                if (!_vm.IsRunning)
                {
                    try { _vm.Toggle(); }
                    catch { }
                }
                _trayIcon?.ShowBalloonTip(3000, "FServer FTP",
                    "已静默启动：双击托盘图标打开主窗口。",
                    WinForms.ToolTipIcon.Info);
            }

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
            IsVisibleChanged += MainWindow_IsVisibleChanged;
        }

        /// <summary>窗口已初始化但还没 Show。静默启动时不 Show → Loaded 也不会触发，所以 Hide 在此兜底。</summary>
        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            if (_vm.SilentStartup) Hide();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _logScroll = FindVisualChild<ScrollViewer>(LogListBox);
            if (_logScroll != null)
            {
                _logScroll.ScrollChanged += LogScroll_ScrollChanged;
                _logScroll.PreviewMouseWheel += LogScroll_PreviewMouseWheel;
            }
        }

        private void InitTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = FServer.Icons.IconLoader.Idle,
                Visible = true,
                Text = "FServer FTP - 点击打开主窗口",
                BalloonTipTitle = "FServer FTP",
            };

            var menu = new WinForms.ContextMenuStrip();
            _trayStartMenuItem = new WinForms.ToolStripMenuItem("启动 FTP", null, (s, e) =>
            {
                ShowMainWindow();
                if (!_vm.IsRunning) _vm.Toggle();
            });
            _trayStopMenuItem = new WinForms.ToolStripMenuItem("停止 FTP", null, (s, e) =>
            {
                ShowMainWindow();
                if (_vm.IsRunning) _vm.Toggle();
            });
            menu.Items.Add(_trayStartMenuItem);
            menu.Items.Add(_trayStopMenuItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("打开主窗口", null, (s, e) => ShowMainWindow()));
            menu.Items.Add(new WinForms.ToolStripMenuItem("打开历史日志目录", null, (s, e) => _vm.OpenLogFolder()));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(new WinForms.ToolStripMenuItem("退出 FServer FTP", null, (s, e) =>
            {
                _trayIcon.Visible = false;
                if (_vm.IsRunning)
                {
                    if (ModernDialog.Ask("FTP 服务正在运行，确定要退出吗？", "退出确认", this)
                        != MessageBoxResult.Yes) return;
                }
                _appExiting = true;
                Application.Current.Shutdown();
            }));
            _trayIcon.ContextMenuStrip = menu;

            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();

            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsRunning)) UpdateTrayState();
            };
            UpdateTrayState();

            // 注入给 App，退出时 Dispose 清托盘幽灵图标
            ((App)Application.Current).TrayIcon = _trayIcon;
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_appExiting) return;
            e.Cancel = true;
            Hide();
            if (_trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(2000, "FServer FTP 已最小化",
                    "点击托盘图标重新打开主窗口，或从托盘菜单退出。", WinForms.ToolTipIcon.Info);
            }
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ShowInTaskbar = (bool)e.NewValue;
        }

        private void UpdateTrayState()
        {
            if (_trayIcon == null) return;
            _trayStartMenuItem.Enabled = !_vm.IsRunning;
            _trayStopMenuItem.Enabled = _vm.IsRunning;
            _trayIcon.Icon = _vm.IsRunning
                ? FServer.Icons.IconLoader.Active
                : FServer.Icons.IconLoader.Idle;
            _trayIcon.Text = _vm.IsRunning
                ? $"FServer FTP 运行中（端口 {_vm.Port}）"
                : "FServer FTP - 已停止";
        }

        private void ShowMainWindow()
        {
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Focus();
        }

        private void LogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
            var nearBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 5;
            _userScrollingUp = !nearBottom;
        }

        private void LogScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (e.Delta > 0) _userScrollingUp = true;
        }

        private void AutoScrollIfNeeded()
        {
            if (_userScrollingUp || LogListBox.Items.Count == 0) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (LogListBox.Items.Count > 0)
                    LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
            }), DispatcherPriority.Render);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var sub = FindVisualChild<T>(child);
                if (sub != null) return sub;
            }
            return null;
        }

        private void AboutBtn_Click(object sender, RoutedEventArgs e)
        {
            var w = new AboutWindow { Owner = this };
            w.ShowDialog();
        }

        private void Toggle_Click(object sender, RoutedEventArgs e) => _vm.Toggle();

        private void PickRoot_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WinForms.FolderBrowserDialog { Description = "选择 FTP 根目录" };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                _vm.RootDir = dlg.SelectedPath;
            }
        }

        private void AddUser_Click(object sender, RoutedEventArgs e) => _vm.AddNewUser();

        private void RemoveUser_Click(object sender, RoutedEventArgs e)
        {
            if (UserGrid.SelectedItem is UserEntryVm u && !u.IsProtected)
                _vm.RemoveUser(u);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _vm.LogLines.Clear();
        }

        private void RefreshIp_Click(object sender, RoutedEventArgs e) => _vm.RefreshLocalIps();

        private void KickSession_Click(object sender, RoutedEventArgs e)
        {
            if (!(SessionGrid.SelectedItem is SessionEntryVm sess)) return;
            if (ModernDialog.Ask($"确定强制断开在线用户「{sess.Username}」？\n断开后该客户端将收到 421 错误。",
                "强制断开确认", this) != MessageBoxResult.Yes) return;

            var ok = _vm.KickSession(sess.Username);
            if (!ok)
            {
                ModernDialog.ShowError("断开失败：服务未运行或用户不存在。", "断开失败", this);
            }
            RefreshSessions_Click(sender, e);
        }

        private void RefreshSessions_Click(object sender, RoutedEventArgs e)
        {
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => _vm.OpenLogFolder();

        private void RefreshHistoryLogs_Click(object sender, RoutedEventArgs e) => _vm.RefreshHistoryLogs();

        private void HistoryLogGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(HistoryLogGrid.SelectedItem is MainViewModel.HistoryLogFileVm f)) return;
            try
            {
                System.Diagnostics.Process.Start("notepad.exe", f.FullPath);
            }
            catch (Exception ex)
            {
                ModernDialog.ShowError($"打开失败：{ex.Message}", "打开失败", this);
            }
        }

        private void ClearTransfers_Click(object sender, RoutedEventArgs e)
        {
            _vm.Transfers.Clear();
        }

        private void RefreshDashboard_Click(object sender, RoutedEventArgs e)
        {
            _vm.RefreshDashboard();
        }
    }
}
