using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class SettingsStore
    {
        private const string SettingsFileName = "settings.json";
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TelegramManager");

        public class Settings
        {
            public string? Scale { get; set; }
            public bool TemplatesEnabled { get; set; } = true;
            public List<TemplateSetting> Templates { get; set; } = new List<TemplateSetting>();
        }

        public Settings Load()
        {
            try
            {
                var file = Path.Combine(ConfigDir, SettingsFileName);
                if (!File.Exists(file))
                {
                    return CreateDefault();
                }

                var json = File.ReadAllText(file);
                var settings = JsonSerializer.Deserialize<Settings>(json);
                return Normalize(settings);
            }
            catch
            {
                return CreateDefault();
            }
        }

        public void Save(Settings settings)
        {
            try
            {
                settings = Normalize(settings);
                Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(ConfigDir, SettingsFileName), json);
            }
            catch
            {
                // ignore save errors
            }
        }

        private static Settings CreateDefault() => Normalize(new Settings());

        private static Settings Normalize(Settings? settings)
        {
            settings ??= new Settings();
            settings.Templates ??= new List<TemplateSetting>();
            return settings;
        }
    }
}
