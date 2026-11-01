using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace FServer.App
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = v != null
                ? $"版本 {v.Major}.{v.Minor}.{v.Build}"
                : "版本 1.0.0";
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Hyperlink 点击 → 系统默认浏览器打开。</summary>
        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* 静默：没浏览器或系统关联失败 */ }
            e.Handled = true;
        }
    }
}
