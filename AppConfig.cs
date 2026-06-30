using System;
using System.IO;
using System.Text.Json;

namespace ScreenshotSaver
{
    public class AppConfig
    {
        public string SaveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "скрины");
        public bool AutoSaveEnabled { get; set; } = true;
        public bool RunAtStartup { get; set; } = false;
        public bool MinimizeToTrayOnClose { get; set; } = true;

        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenshotSaver"
        );
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        // Ensure directory exists if path is valid
                        if (!string.IsNullOrWhiteSpace(config.SaveFolder) && !Directory.Exists(config.SaveFolder))
                        {
                            try
                            {
                                Directory.CreateDirectory(config.SaveFolder);
                            }
                            catch { }
                        }
                        return config;
                    }
                }
            }
            catch
            {
                // Ignore load errors, return default config
            }

            var defaultConfig = new AppConfig();
            defaultConfig.EnsureFolderExists();
            return defaultConfig;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }

        public void EnsureFolderExists()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(SaveFolder) && !Directory.Exists(SaveFolder))
                {
                    Directory.CreateDirectory(SaveFolder);
                }
            }
            catch { }
        }
    }
}
