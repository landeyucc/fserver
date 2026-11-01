using System;
using System.IO;
using System.Reflection;
using System.Drawing;

namespace FServer.Icons
{
    /// <summary>
    /// 从 FServer.Icons.dll 嵌入资源加载 .ico。
    /// 返回 System.Drawing.Icon，托盘 NotifyIcon 直接能用。
    /// </summary>
    public static class IconLoader
    {
        private const string NameSpace = "FServer.Icons";

        private static readonly Assembly Assembly = typeof(IconLoader).Assembly;

        private static Icon _idleIcon;
        private static Icon _activeIcon;

        /// <summary>托盘未激活图标（FTP 停止）。</summary>
        public static Icon Idle => _idleIcon ??= Load("ftp.ico");

        /// <summary>托盘激活图标（FTP 运行中）。</summary>
        public static Icon Active => _activeIcon ??= Load("ftp2.ico");

        private static Icon Load(string resourceName)
        {
            var full = $"{NameSpace}.{resourceName}";
            using var stream = Assembly.GetManifestResourceStream(full)
                ?? throw new FileNotFoundException($"Embedded resource not found: {full}");
            return new Icon(stream);
        }
    }
}
