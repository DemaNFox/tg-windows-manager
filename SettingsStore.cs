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
            public string? LastSeenAboutVersion { get; set; }
            public List<TemplateSetting> Templates { get; set; } = new List<TemplateSetting>();
            public List<AccountGroup> AccountGroups { get; set; } = new List<AccountGroup>();
            public Dictionary<string, AccountState> AccountStates { get; set; } = new Dictionary<string, AccountState>(StringComparer.OrdinalIgnoreCase);
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
            settings.AccountGroups ??= new List<AccountGroup>();
            settings.AccountStates ??= new Dictionary<string, AccountState>(StringComparer.OrdinalIgnoreCase);
            settings.LastSeenAboutVersion = string.IsNullOrWhiteSpace(settings.LastSeenAboutVersion)
                ? null
                : settings.LastSeenAboutVersion.Trim();
            EnsureDefaultTemplate(settings.Templates);
            NormalizeAccountGroups(settings.AccountGroups);
            NormalizeAccountStates(settings.AccountStates);
            return settings;
        }

        private static void NormalizeAccountGroups(List<AccountGroup> groups)
        {
            var unique = new Dictionary<string, AccountGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (group == null)
                {
                    continue;
                }

                var name = (group.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!unique.ContainsKey(name))
                {
                    unique[name] = new AccountGroup { Name = name };
                }
            }

            groups.Clear();
            groups.AddRange(unique.Values);
        }

        private static void NormalizeAccountStates(Dictionary<string, AccountState> states)
        {
            var normalized = new Dictionary<string, AccountState>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in states)
            {
                var key = pair.Key;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var state = pair.Value ?? new AccountState();
                var groupName = state.GroupName;
                state.GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
                normalized[key.Trim()] = state;
            }

            states.Clear();
            foreach (var pair in normalized)
            {
                states[pair.Key] = pair.Value;
            }
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
