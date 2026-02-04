using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal static class ExplorerGroupCommandHandler
    {
        private const string GroupUngrouped = "Без группы";
        private const string GroupFrozen = "Заморозка";
        private const string GroupCrashed = "Вылеты";

        public static bool TryHandle(string[]? args, Action<string>? log = null)
        {
            if (args == null || args.Length == 0)
            {
                return false;
            }

            var command = args[0]?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            if (command.Equals("--explorer-add-to-group", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    MessageBox.Show("Invalid arguments for add-to-group command.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return true;
                }

                AddToGroup(args[1], args[2], log);
                return true;
            }

            if (command.Equals("--explorer-remove-from-group", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    MessageBox.Show("Invalid arguments for remove-from-group command.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return true;
                }

                RemoveFromGroup(args[1], log);
                return true;
            }

            if (command.Equals("--explorer-show-group", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    MessageBox.Show("Invalid arguments for show-group command.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return true;
                }

                ShowCurrentGroup(args[1]);
                return true;
            }

            if (command.Equals("--explorer-create-group-and-add", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2)
                {
                    MessageBox.Show("Invalid arguments for create-group command.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return true;
                }

                CreateGroupAndAdd(args[1], log);
                return true;
            }

            return false;
        }

        private static void AddToGroup(string accountFolder, string groupName, Action<string>? log)
        {
            groupName = NormalizeGroupName(groupName);
            if (string.IsNullOrWhiteSpace(groupName))
            {
                MessageBox.Show("Group name is empty.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!TryResolveAccount(accountFolder, out var accountName, out var error))
            {
                MessageBox.Show(error, "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var store = new SettingsStore();
            var settings = store.Load();
            EnsureGroupExists(settings, groupName);

            var state = GetOrCreateState(settings, accountName);
            if (string.Equals(groupName, GroupFrozen, StringComparison.OrdinalIgnoreCase))
            {
                state.Status = AccountStatus.Frozen;
                state.GroupName = null;
            }
            else if (string.Equals(groupName, GroupCrashed, StringComparison.OrdinalIgnoreCase))
            {
                state.Status = AccountStatus.Crashed;
                state.GroupName = null;
            }
            else
            {
                state.Status = AccountStatus.Active;
                state.GroupName = groupName;
            }

            store.Save(settings);
            ExplorerContextMenuManager.InstallOrUpdate(settings, log);
            MessageBox.Show("Account added to group: " + groupName, "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void RemoveFromGroup(string accountFolder, Action<string>? log)
        {
            if (!TryResolveAccount(accountFolder, out var accountName, out var error))
            {
                MessageBox.Show(error, "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var store = new SettingsStore();
            var settings = store.Load();
            var state = GetOrCreateState(settings, accountName);
            state.Status = AccountStatus.Active;
            state.GroupName = null;
            store.Save(settings);
            ExplorerContextMenuManager.InstallOrUpdate(settings, log);
            MessageBox.Show("Account removed from group.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void ShowCurrentGroup(string accountFolder)
        {
            if (!TryResolveAccount(accountFolder, out var accountName, out var error))
            {
                MessageBox.Show(error, "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var store = new SettingsStore();
            var settings = store.Load();
            var state = GetOrCreateState(settings, accountName);

            string current = state.Status switch
            {
                AccountStatus.Frozen => GroupFrozen,
                AccountStatus.Crashed => GroupCrashed,
                _ => string.IsNullOrWhiteSpace(state.GroupName) ? GroupUngrouped : state.GroupName!
            };

            MessageBox.Show(
                "Account: " + accountName + Environment.NewLine + "Current group: " + current,
                "Telegram Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static void CreateGroupAndAdd(string accountFolder, Action<string>? log)
        {
            var groupName = PromptGroupName();
            if (groupName == null)
            {
                return;
            }

            AddToGroup(accountFolder, groupName, log);
        }

        private static bool TryResolveAccount(string folder, out string accountName, out string error)
        {
            accountName = string.Empty;
            error = string.Empty;

            try
            {
                var fullPath = Path.GetFullPath(folder ?? string.Empty);
                if (!Directory.Exists(fullPath))
                {
                    error = "Selected folder does not exist.";
                    return false;
                }

                if (!Directory.Exists(Path.Combine(fullPath, "tdata")))
                {
                    error = "Selected folder is not a Telegram account folder (missing tdata).";
                    return false;
                }

                if (!File.Exists(Path.Combine(fullPath, "Telegram.exe")))
                {
                    error = "Selected folder is not a Telegram account folder (missing Telegram.exe).";
                    return false;
                }

                accountName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(accountName))
                {
                    error = "Failed to resolve account name from folder path.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to validate account folder: " + ex.Message;
                return false;
            }
        }

        private static string? PromptGroupName()
        {
            using var form = new Form
            {
                Width = 360,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Create group",
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                TopMost = true
            };

            var label = new Label
            {
                Text = "Group name:",
                Left = 10,
                Top = 15,
                Width = 320
            };

            var input = new TextBox
            {
                Left = 10,
                Top = 38,
                Width = 320
            };

            var ok = new Button
            {
                Text = "OK",
                Left = 174,
                Top = 72,
                Width = 75,
                DialogResult = DialogResult.OK
            };
            var cancel = new Button
            {
                Text = "Cancel",
                Left = 255,
                Top = 72,
                Width = 75,
                DialogResult = DialogResult.Cancel
            };

            form.Controls.Add(label);
            form.Controls.Add(input);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            if (form.ShowDialog() != DialogResult.OK)
            {
                return null;
            }

            var name = NormalizeGroupName(input.Text);
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Group name is empty.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            if (IsReservedGroupName(name))
            {
                MessageBox.Show("This group name is reserved.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            return name;
        }

        private static void EnsureGroupExists(SettingsStore.Settings settings, string groupName)
        {
            if (string.Equals(groupName, GroupFrozen, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(groupName, GroupCrashed, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!settings.AccountGroups.Any(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase)))
            {
                settings.AccountGroups.Add(new AccountGroup { Name = groupName });
            }
        }

        private static AccountState GetOrCreateState(SettingsStore.Settings settings, string accountName)
        {
            if (settings.AccountStates.TryGetValue(accountName, out var state) && state != null)
            {
                return state;
            }

            state = new AccountState();
            settings.AccountStates[accountName] = state;
            return state;
        }

        private static bool IsReservedGroupName(string value)
        {
            return string.Equals(value, GroupUngrouped, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, GroupFrozen, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, GroupCrashed, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGroupName(string? value)
        {
            return (value ?? string.Empty).Trim();
        }
    }
}
