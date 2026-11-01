using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FServer.App
{
    /// <summary>
    /// 现代化 WPF 弹窗（替代 MessageBox.Show）。
    /// 复用项目的 LightTheme / DarkTheme DynamicResource，视觉与主窗口一致。
    /// 图标用 WPF Geometry（不依赖系统字体，Win7 都能正确显示）。
    /// </summary>
    public partial class ModernDialog : Window
    {
        public enum DialogIcon { Information, Success, Warning, Error, Question }
        public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel, Yes }

        /// <summary>
        /// 与 MessageBoxResult 兼容，方便调用方最小改动。
        /// </summary>
        public new MessageBoxResult DialogResult { get; private set; }

        public ModernDialog(string title, string message,
                            DialogIcon icon = DialogIcon.Information,
                            DialogButtons buttons = DialogButtons.Ok)
        {
            InitializeComponent();

            TitleText.Text = string.IsNullOrWhiteSpace(title) ? string.Empty : title;
            MessageText.Text = message ?? string.Empty;

            ApplyIcon(icon);
            ApplyButtons(buttons);

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    DialogResult = MessageBoxResult.Cancel;
                    Close();
                }
            };
        }

        /// <summary>
        /// 用纯 WPF Geometry 画图标（不依赖系统字体，Segoe Fluent 等字体缺失也能正常显示）。
        /// 所有图形都在 32×32 Canvas 坐标系里画，Viewbox Stretch=Fill 自动缩放。
        /// 5 种风格：Information(蓝i圆) / Success(绿✓圆) / Warning(琥珀⚠三角) / Error(红✕圆) / Question(蓝?圆)。
        /// </summary>
        private void ApplyIcon(DialogIcon icon)
        {
            FrameworkElement shape;
            switch (icon)
            {
                case DialogIcon.Success:
                    shape = BuildCircleIcon(Colors.LimeGreen, BuildCheckPath());
                    break;
                case DialogIcon.Warning:
                    shape = BuildWarningTriangle();
                    break;
                case DialogIcon.Error:
                    shape = BuildCircleIcon((Color)ColorConverter.ConvertFromString("#EF4444"), BuildCrossPath());
                    break;
                case DialogIcon.Question:
                    shape = BuildCircleIcon((Color)ColorConverter.ConvertFromString("#3B82F6"), BuildQuestionPath());
                    break;
                case DialogIcon.Information:
                default:
                    shape = BuildCircleIcon((Color)ColorConverter.ConvertFromString("#3B82F6"), BuildInfoPath());
                    break;
            }
            IconBlock.Child = shape;
        }

        /// <summary>圆形底 + 内部白色 Path（全部在 32×32 Canvas 坐标系里画，Viewbox 自动适配）。</summary>
        private static FrameworkElement BuildCircleIcon(Color bg, Geometry inner)
        {
            var canvas = new Canvas { Width = 32, Height = 32 };

            var circle = new Path
            {
                Data = new EllipseGeometry(new Point(16, 16), 15.5, 15.5),
                Fill = new SolidColorBrush(bg),
            };
            canvas.Children.Add(circle);

            // inner 坐标系 20×20，平移 6px 放到 32×32 居中
            var innerTransformed = new GeometryGroup();
            innerTransformed.Children.Add(inner);
            innerTransformed.Transform = new TranslateTransform(6, 6);

            var innerPath = new Path
            {
                Data = innerTransformed,
                Fill = Brushes.White,
            };
            canvas.Children.Add(innerPath);

            return canvas;
        }

        /// <summary>感叹号三角（黄色背景 + 白色感叹号）。</summary>
        private static FrameworkElement BuildWarningTriangle()
        {
            var canvas = new Canvas { Width = 32, Height = 32 };

            var triangle = new Polygon
            {
                Points = new PointCollection { new Point(16, 3), new Point(30, 28), new Point(2, 28) },
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),
            };
            Canvas.SetLeft(triangle, 0);
            Canvas.SetTop(triangle, 0);
            canvas.Children.Add(triangle);

            var bar = new Rectangle
            {
                Width = 3, Height = 12,
                Fill = Brushes.White,
                RadiusX = 1.5, RadiusY = 1.5,
            };
            Canvas.SetLeft(bar, 14.5);
            Canvas.SetTop(bar, 9);
            canvas.Children.Add(bar);

            var dot = new Ellipse { Width = 3.5, Height = 3.5, Fill = Brushes.White };
            Canvas.SetLeft(dot, 14.25);
            Canvas.SetTop(dot, 22);
            canvas.Children.Add(dot);

            return canvas;
        }

        // 内部图形 Geometry（20×20 坐标系，Viewbox 自动适配）

        /// <summary>i 字：上方圆点 + 下方单竖杆。</summary>
        private static Geometry BuildInfoPath()
            => Geometry.Parse(
                // 圆点（中心 10,4 半径 2.2）
                "M 10 1.8 a 2.2 2.2 0 1 1 0 4.4 a 2.2 2.2 0 1 1 0 -4.4 Z " +
                // 竖杆（居中）
                "M 8.5 8.5 h 3 v 10 h -3 Z");

        /// <summary>勾 ✓。</summary>
        private static Geometry BuildCheckPath()
            => Geometry.Parse(
                "M 4 10.5 L 8 14.5 L 16 6.5 L 16.8 7.3 L 8 16.1 L 3.2 11.3 Z");

        /// <summary>叉 ✕。</summary>
        private static Geometry BuildCrossPath()
            => Geometry.Parse(
                "M 4 4 L 7 4 L 10 9 L 13 4 L 16 4 L 11 10 L 16 16 L 13 16 L 10 11 L 7 16 L 4 16 L 9 10 Z");

        /// <summary>问号 ?：顶部弧 + 中竖 + 底点。</summary>
        private static Geometry BuildQuestionPath()
            => Geometry.Parse(
                "M 7 6 a 3 3 0 1 1 6 0 a 3 3 0 0 1 -1.5 2.6 a 2 2 0 0 0 -1 1.7 v 1.2 h -2 v -1.4 a 4 4 0 0 1 2 -3.4 a 1.5 1.5 0 0 0 0 -0.7 Z " +
                "M 8.8 14 h 2.4 v 2.4 h -2.4 Z");


        private void ApplyButtons(DialogButtons buttons)
        {
            BtnPrimary.Visibility = Visibility.Visible;
            BtnSecondary.Visibility = Visibility.Collapsed;
            BtnTertiary.Visibility = Visibility.Collapsed;

            switch (buttons)
            {
                case DialogButtons.Ok:
                    BtnPrimary.Content = "确定";
                    break;
                case DialogButtons.Yes:
                    BtnPrimary.Content = "确定";
                    break;
                case DialogButtons.OkCancel:
                    BtnPrimary.Content = "确定";
                    BtnSecondary.Content = "取消";
                    BtnSecondary.Visibility = Visibility.Visible;
                    break;
                case DialogButtons.YesNo:
                    BtnPrimary.Content = "是";
                    BtnSecondary.Content = "否";
                    BtnSecondary.Visibility = Visibility.Visible;
                    break;
                case DialogButtons.YesNoCancel:
                    BtnPrimary.Content = "是";
                    BtnSecondary.Content = "否";
                    BtnTertiary.Content = "取消";
                    BtnSecondary.Visibility = Visibility.Visible;
                    BtnTertiary.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void BtnPrimary_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = BtnPrimary.Content.ToString() switch
            {
                "是" => MessageBoxResult.Yes,
                "否" => MessageBoxResult.No,
                "取消" => MessageBoxResult.Cancel,
                _ => MessageBoxResult.OK,
            };
            Close();
        }

        private void BtnSecondary_Click(object sender, RoutedEventArgs e)
        {
            var text = BtnSecondary.Content?.ToString();
            DialogResult = text switch
            {
                "是" => MessageBoxResult.Yes,
                "否" => MessageBoxResult.No,
                "取消" => MessageBoxResult.Cancel,
                _ => MessageBoxResult.Cancel,
            };
            Close();
        }

        private void BtnTertiary_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = MessageBoxResult.Cancel;
            Close();
        }

        /// <summary>主弹窗入口。owner 为 null 时使用 Application.Current.MainWindow；若 MainWindow 也为 null 则 CenterScreen。</summary>
        public static MessageBoxResult Show(string message, string title = "",
            DialogIcon icon = DialogIcon.Information,
            DialogButtons buttons = DialogButtons.Ok,
            Window owner = null)
        {
            var actualOwner = owner ?? Application.Current?.MainWindow;
            var dlg = new ModernDialog(title, message, icon, buttons)
            {
                Owner = actualOwner,
            };
            if (actualOwner == null)
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dlg.Topmost = true;
            }
            dlg.ShowDialog();
            return dlg.DialogResult;
        }

        public static MessageBoxResult ShowInfo(string message, string title = "提示", Window owner = null)
            => Show(message, title, DialogIcon.Information, DialogButtons.Ok, owner);

        public static MessageBoxResult ShowSuccess(string message, string title = "成功", Window owner = null)
            => Show(message, title, DialogIcon.Success, DialogButtons.Ok, owner);

        public static MessageBoxResult ShowWarning(string message, string title = "警告", Window owner = null)
            => Show(message, title, DialogIcon.Warning, DialogButtons.Ok, owner);

        public static MessageBoxResult ShowError(string message, string title = "错误", Window owner = null)
            => Show(message, title, DialogIcon.Error, DialogButtons.Ok, owner);

        public static MessageBoxResult Ask(string message, string title = "确认", Window owner = null)
            => Show(message, title, DialogIcon.Question, DialogButtons.YesNo, owner);

        public static MessageBoxResult AskOkCancel(string message, string title = "确认", Window owner = null)
            => Show(message, title, DialogIcon.Question, DialogButtons.OkCancel, owner);
    }
}
