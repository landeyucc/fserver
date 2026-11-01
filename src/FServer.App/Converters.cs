using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FServer.App
{
    public class BoolToStatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value is bool b && b;
            return v
                ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
                : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    public class BoolToRunTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "运行中" : "已停止";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// SelectedItem → bool：null 返回 false；有值且 IsProtected=true 时返回 false（按钮禁用）。
    /// 其他情况返回 true（按钮可点）。
    /// </summary>
    public class ObjectToEnabledConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return false;
            var pi = value.GetType().GetProperty("IsProtected");
            if (pi != null && pi.PropertyType == typeof(bool))
                return !(bool)pi.GetValue(value);
            return true;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// IsProtected → ToolTip 文本：protected 给提示；否则 null。
    /// </summary>
    public class BoolToProtectedTipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? "guest 是系统保护账号，不能删除（可在【启用】列禁用登录）" : null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// bool → !bool 的简单取反。用于 IsProtected→IsEnabled 场景。
    /// </summary>
    public class BoolInvertConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is bool b && b);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is bool b && b);
    }

    /// <summary>
    /// bool → Style：true=运行态用 StopButton (红)，false=停止态用 PrimaryButton (主题蓝)。
    /// 两个 Style key 通过 ConverterParameter 传入，格式 "TrueKey|FalseKey"。
    /// </summary>
    public class BoolToStyleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var p = (parameter as string)?.Split('|');
            if (p == null || p.Length != 2) return Binding.DoNothing;
            var trueKey = p[0];  // IsRunning=true 时用哪个 Style
            var falseKey = p[1]; // IsRunning=false 时用哪个 Style
            var key = (value is bool b && b) ? trueKey : falseKey;
            return Application.Current.TryFindResource(key) as Style ?? Binding.DoNothing;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// FtpLogLevel → Brush：ERROR 红、WARN 黄、INFO 灰、Debug 更淡。
    /// 用于日志 ListBox 条目按级别着色，关键问题一眼看见。
    /// </summary>
    public class LogLevelToBrushConverter : IValueConverter
    {
        private static readonly Brush Red    = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
        private static readonly Brush Amber  = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
        private static readonly Brush Info   = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
        private static readonly Brush Debug  = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Interop.FtpLogLevel l)
            {
                return l switch
                {
                    Interop.FtpLogLevel.Error  => Red,
                    Interop.FtpLogLevel.Warn  => Amber,
                    Interop.FtpLogLevel.Info  => Info,
                    _                          => Debug,
                };
            }
            return Info;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// string → string：空字符串返回 null（让 ToolTip 不显示），否则原样返回。
    /// 用于"本机 IP" ToolTip 有值才弹提示。
    /// </summary>
    public class EmptyTextOrList : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrWhiteSpace(value as string) ? null : value;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
