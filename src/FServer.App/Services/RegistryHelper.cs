using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace FServer.App.Services
{
    /// <summary>
    /// 开机自启（注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run）。
    /// HKCU 不需要管理员权限；HKLM 才需要。
    /// </summary>
    public static class RegistryHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "FServer.Ftp";

        public static string GetLaunchCommand()
        {
            var exe = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exe)) exe = AppContext.BaseDirectory;
            return $"\"{exe}\"";
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (key == null) return;

                if (enable)
                {
                    key.SetValue(ValueName, GetLaunchCommand(), RegistryValueKind.String);
                }
                else
                {
                    if (key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RegistryHelper] SetAutoStart({enable}) 失败：{ex.Message}");
            }
        }
    }
}
