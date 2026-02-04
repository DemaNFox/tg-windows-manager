using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;

namespace TelegramTrayLauncher
{
    internal static class ExplorerContextMenuManager
    {
        private const string BaseKeyPath = @"Software\Classes\Directory\shell\TelegramManager";
        private const string GroupFrozen = "Заморозка";
        private const string GroupCrashed = "Вылеты";

        public static void InstallOrUpdate(SettingsStore.Settings? settings, Action<string>? log = null)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return;
                }

                Registry.CurrentUser.DeleteSubKeyTree(BaseKeyPath, throwOnMissingSubKey: false);

                using var root = Registry.CurrentUser.CreateSubKey(BaseKeyPath);
                if (root == null)
                {
                    return;
                }

                root.SetValue("MUIVerb", "Telegram Manager");
                root.SetValue("Icon", "\"" + exePath + "\"");
                root.SetValue("SubCommands", string.Empty);

                CreateAddToGroupSubmenu(root, settings, exePath);
                CreateSimpleCommand(
                    root,
                    "015_show_current_group",
                    "Current group...",
                    "\"" + exePath + "\" --explorer-show-group \"%1\"");
                CreateSimpleCommand(
                    root,
                    "020_remove_from_group",
                    "Remove from group",
                    "\"" + exePath + "\" --explorer-remove-from-group \"%1\"");
            }
            catch (Exception ex)
            {
                log?.Invoke("Failed to update Explorer menu: " + ex.Message);
            }
        }

        private static void CreateAddToGroupSubmenu(RegistryKey root, SettingsStore.Settings? settings, string exePath)
        {
            using var addRoot = root.CreateSubKey(@"shell\010_add_to_group");
            if (addRoot == null)
            {
                return;
            }

            addRoot.SetValue("MUIVerb", "Add to group");
            addRoot.SetValue("SubCommands", string.Empty);

            var groups = GetGroupsForAdd(settings);
            int index = 0;
            foreach (var group in groups)
            {
                CreateSimpleCommand(
                    addRoot,
                    "g" + index.ToString("D3"),
                    group,
                    "\"" + exePath + "\" --explorer-add-to-group \"%1\" \"" + EscapeArg(group) + "\"");
                index++;
            }

            CreateSimpleCommand(
                addRoot,
                "zz_create_group",
                "Create group...",
                "\"" + exePath + "\" --explorer-create-group-and-add \"%1\"");
        }

        private static void CreateSimpleCommand(RegistryKey parent, string keyName, string title, string command)
        {
            using var item = parent.CreateSubKey(@"shell\" + keyName);
            if (item == null)
            {
                return;
            }

            item.SetValue("MUIVerb", title);

            using var commandKey = item.CreateSubKey("command");
            commandKey?.SetValue(string.Empty, command);
        }

        private static List<string> GetGroupsForAdd(SettingsStore.Settings? settings)
        {
            var result = new List<string> { GroupFrozen, GroupCrashed };
            if (settings?.AccountGroups != null)
            {
                foreach (var group in settings.AccountGroups)
                {
                    var name = group?.Name?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!result.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(name);
                    }
                }
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static string EscapeArg(string value) => value.Replace("\"", "\\\"");
    }
}
