using System;
using Microsoft.Win32;

namespace ScreenshotSaver
{
    public static class StartupManager
    {
        private const string AppName = "ScreenshotSaver";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                if (key == null) return false;
                var value = key.GetValue(AppName) as string;
                if (string.IsNullOrEmpty(value)) return false;

                string currentExePath = Environment.ProcessPath ?? "";
                return value.Contains(currentExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;

                if (enable)
                {
                    string path = Environment.ProcessPath ?? "";
                    if (!string.IsNullOrEmpty(path))
                    {
                        // Wrap execution path in quotes to handle spaces correctly
                        key.SetValue(AppName, $"\"{path}\" --background");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch
            {
                // Ignore registry access errors (e.g. if blocked by antivirus/permissions)
            }
        }
    }
}
