using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FServer.App.ViewModels;

namespace FServer.App
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private ScrollViewer _logScroll;
        /// <summary>用户是否正在看历史日志（向上滚动）。true 时不自动拉到底。</summary>
        private bool _userScrollingUp;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel();
            DataContext = _vm;

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _logScroll = FindVisualChild<ScrollViewer>(LogListBox);
            if (_logScroll != null)
            {
                _logScroll.ScrollChanged += LogScroll_ScrollChanged;
                _logScroll.PreviewMouseWheel += LogScroll_PreviewMouseWheel;
            }

            // 订阅集合变化 → 智能滚到底
            _vm.LogLines.CollectionChanged += (s, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    AutoScrollIfNeeded();
            };
        }

        private void LogScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (e.Delta > 0) _userScrollingUp = true;
            else if (e.Delta < 0) _userScrollingUp = false;
        }

        private void LogScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_logScroll == null) return;
            // 到底了（留 5px 容忍）→ 用户主动拉到底部 → 恢复自动滚动
            if (_logScroll.VerticalOffset + _logScroll.ViewportHeight >= _logScroll.ExtentHeight - 5)
                _userScrollingUp = false;
        }

        /// <summary>
        /// 智能自动滚动：当且仅当用户没有在看历史日志时，滚到最新一条。
        /// 关键点：DispatcherPriority.Render —— 等数据绑定 & 布局渲染都跑完，再调用 ScrollIntoView。
        ///          这样才能 100% 滚到刚追加的那条，不会因为虚拟化或布局未更新而滚错位置。
        /// </summary>
        private void AutoScrollIfNeeded()
        {
            if (_userScrollingUp) return;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                var last = LogListBox.Items.Count - 1;
                if (last >= 0) LogListBox.ScrollIntoView(LogListBox.Items[last]);
            }), DispatcherPriority.Render);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void Toggle_Click(object sender, RoutedEventArgs e) => _vm.Toggle();

        private void PickRoot_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 FTP 根目录",
                ShowNewFolderButton = true,
            })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    _vm.RootDir = dlg.SelectedPath;
            }
        }

        private void AddUser_Click(object sender, RoutedEventArgs e) => _vm.AddNewUser();

        private void RemoveUser_Click(object sender, RoutedEventArgs e)
        {
            if (UserGrid.SelectedItem is UserEntryVm u)
                _vm.RemoveUser(u);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _vm.LogLines.Clear();
            _userScrollingUp = false;
        }

        private void RefreshIp_Click(object sender, RoutedEventArgs e)
        {
            _vm.RefreshLocalIps();
        }
    }
}
