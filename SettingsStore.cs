using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
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
            EnsureDefaultTemplate(settings.Templates);
            return settings;
        }

        private static void EnsureDefaultTemplate(List<TemplateSetting> templates)
        {
            var existing = templates.FirstOrDefault(t =>
                string.Equals(t.Text, TemplateDefaults.DefaultText, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                templates.Add(new TemplateSetting
                {
                    Text = TemplateDefaults.DefaultText,
                    Key = Keys.None,
                    IsDefault = true
                });
            }
            else
            {
                existing.IsDefault = true;
                existing.Text = TemplateDefaults.DefaultText;
            }
        }
    }
}
