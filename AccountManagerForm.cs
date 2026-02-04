using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class AccountManagerForm : Form
    {
        private const string GroupUngrouped = "\u0411\u0435\u0437 \u0433\u0440\u0443\u043f\u043f\u044b";
        private const string GroupFrozen = "\u0417\u0430\u043c\u043e\u0440\u043e\u0437\u043a\u0430";
        private const string GroupCrashed = "\u0412\u044b\u043b\u0435\u0442\u044b";

        private readonly string _baseDir;
        private readonly TelegramProcessManager _processManager;
        private readonly SettingsStore _settingsStore;
        private SettingsStore.Settings _settings;
        private List<TelegramProcessManager.TelegramExecutable> _executables = new List<TelegramProcessManager.TelegramExecutable>();

        private readonly TextBox _searchBox;
        private readonly ListBox _groupList;
        private readonly TextBox _newGroupBox;
        private readonly Button _addGroupButton;
        private readonly ComboBox _removeGroupCombo;
        private readonly Button _removeGroupButton;
        private bool _hasChanges;
        private FileSystemWatcher? _baseDirWatcher;
        private System.Windows.Forms.Timer? _rescanTimer;
        private readonly object _rescanLock = new object();

        public AccountManagerForm(string baseDir, TelegramProcessManager processManager, SettingsStore settingsStore, SettingsStore.Settings settings)
        {
            _baseDir = baseDir ?? string.Empty;
            _processManager = processManager;
            _settingsStore = settingsStore;
            _settings = settings;

            Text = "\u0423\u043f\u0440\u0430\u0432\u043b\u0435\u043d\u0438\u0435 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u0430\u043c\u0438";
            Width = 920;
            Height = 650;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 560);

            var searchLabel = new Label
            {
                Text = "\u041f\u043e\u0438\u0441\u043a:",
                Left = 12,
                Top = 12,
                AutoSize = true
            };

            _searchBox = new TextBox
            {
                Left = 70,
                Top = 8,
                Width = 320
            };
            _searchBox.TextChanged += (_, __) => RefreshSections();

            _groupList = new ListBox
            {
                Left = 12,
                Top = 38,
                Width = 870,
                Height = 470,
                BorderStyle = BorderStyle.FixedSingle,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22,
                SelectionMode = SelectionMode.MultiExtended,
                AllowDrop = true
            };
            _groupList.DrawItem += GroupListOnDrawItem;
            _groupList.MouseDown += GroupListOnMouseDown;
            _groupList.MouseUp += GroupListOnMouseUp;
            _groupList.DragEnter += GroupListOnDragEnter;
            _groupList.DragDrop += GroupListOnDragDrop;

            var groupBoxLabel = new Label
            {
                Text = "\u0413\u0440\u0443\u043f\u043f\u044b:",
                Left = 12,
                Top = 520,
                AutoSize = true
            };

            _newGroupBox = new TextBox
            {
                Left = 80,
                Top = 516,
                Width = 220
            };

            _addGroupButton = new Button
            {
                Text = "\u0414\u043e\u0431\u0430\u0432\u0438\u0442\u044c",
                Left = 310,
                Top = 514,
                Width = 90
            };
            _addGroupButton.Click += (_, __) => AddGroup();

            _removeGroupCombo = new ComboBox
            {
                Left = 420,
                Top = 516,
                Width = 220,
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            _removeGroupButton = new Button
            {
                Text = "\u0423\u0434\u0430\u043b\u0438\u0442\u044c",
                Left = 650,
                Top = 514,
                Width = 90
            };
            _removeGroupButton.Click += (_, __) => RemoveGroup();

            var closeButton = new Button
            {
                Text = "\u0417\u0430\u043a\u0440\u044b\u0442\u044c",
                Left = 792,
                Top = 514,
                Width = 90,
                DialogResult = DialogResult.OK
            };
            closeButton.Click += (_, __) => Close();

            Controls.Add(searchLabel);
            Controls.Add(_searchBox);
            Controls.Add(_groupList);
            Controls.Add(groupBoxLabel);
            Controls.Add(_newGroupBox);
            Controls.Add(_addGroupButton);
            Controls.Add(_removeGroupCombo);
            Controls.Add(_removeGroupButton);
            Controls.Add(closeButton);

            Load += (_, __) => InitializeData();
            FormClosed += (_, __) =>
            {
                if (_hasChanges)
                {
                    _settingsStore.Save(_settings);
                    ExplorerContextMenuManager.InstallOrUpdate(_settings);
                    DialogResult = DialogResult.OK;
                }

                DisposeWatcher();
            };
        }

        private void InitializeData()
        {
            _executables = _processManager.DiscoverExecutables(_baseDir);
            EnsureAccountStates();
            RefreshRemoveGroupCombo();
            RefreshSections();
            InitializeWatcher();
        }

        private void EnsureAccountStates()
        {
            bool added = false;
            foreach (var exe in _executables)
            {
                if (!_settings.AccountStates.ContainsKey(exe.Name))
                {
                    _settings.AccountStates[exe.Name] = new AccountState();
                    added = true;
                }
            }

            if (added)
            {
                _hasChanges = true;
            }
        }

        private void RefreshRemoveGroupCombo()
        {
            _removeGroupCombo.Items.Clear();
            foreach (var group in _settings.AccountGroups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(group.Name))
                {
                    _removeGroupCombo.Items.Add(group.Name);
                }
            }

            if (_removeGroupCombo.Items.Count > 0)
            {
                _removeGroupCombo.SelectedIndex = 0;
            }
        }

        private void RefreshSections()
        {
            string filter = _searchBox.Text.Trim();
            _groupList.BeginUpdate();
            _groupList.Items.Clear();
            foreach (var item in BuildGroupListItems(filter))
            {
                _groupList.Items.Add(item);
            }
            _groupList.EndUpdate();
        }

        private List<GroupDefinition> BuildGroupDefinitions()
        {
            var sections = new List<GroupDefinition>
            {
                new GroupDefinition(GroupUngrouped, GroupKind.Ungrouped),
                new GroupDefinition(GroupFrozen, GroupKind.Frozen),
                new GroupDefinition(GroupCrashed, GroupKind.Crashed)
            };

            foreach (var group in _settings.AccountGroups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(group.Name))
                {
                    sections.Add(new GroupDefinition(group.Name, GroupKind.Custom));
                }
            }

            return sections;
        }

        private void AddGroup()
        {
            string name = (_newGroupBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (IsReservedGroupName(name))
            {
                MessageBox.Show("\u042d\u0442\u043e \u0438\u043c\u044f \u0437\u0430\u043d\u044f\u0442\u043e \u0441\u043b\u0443\u0436\u0435\u0431\u043d\u044b\u043c \u0440\u0430\u0437\u0434\u0435\u043b\u043e\u043c.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_settings.AccountGroups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _settings.AccountGroups.Add(new AccountGroup { Name = name });
            _newGroupBox.Text = string.Empty;
            _hasChanges = true;
            RefreshRemoveGroupCombo();
            RefreshSections();
        }

        private void RemoveGroup()
        {
            if (_removeGroupCombo.SelectedItem is not string groupName || string.IsNullOrWhiteSpace(groupName))
            {
                return;
            }

            var confirm = MessageBox.Show(
                "\u0423\u0434\u0430\u043b\u0438\u0442\u044c \u0433\u0440\u0443\u043f\u043f\u0443 \u0438 \u0441\u043d\u044f\u0442\u044c \u0441 \u043d\u0435\u0435 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u044b?",
                "Telegram Manager",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            _settings.AccountGroups.RemoveAll(g => string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
            foreach (var state in _settings.AccountStates.Values)
            {
                if (string.Equals(state.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    state.GroupName = null;
                }
            }

            _hasChanges = true;
            RefreshRemoveGroupCombo();
            RefreshSections();
        }

        private void OnGroupOpen(GroupKind kind, string name)
        {
            if (kind == GroupKind.Frozen || kind == GroupKind.Crashed)
            {
                return;
            }

            var accounts = GetAccountsForGroup(kind, name);
            var executables = _executables
                .Where(e => accounts.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            _processManager.StartExecutables(executables, _settings.Scale);
        }

        private void OnGroupClose(GroupKind kind, string name)
        {
            if (kind == GroupKind.Frozen || kind == GroupKind.Crashed)
            {
                return;
            }

            var accounts = GetAccountsForGroup(kind, name);
            var directories = _executables
                .Where(e => accounts.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .Select(e => e.Directory)
                .ToList();

            _processManager.CloseTelegramForDirectories(directories, _baseDir);
        }

        private void OnAssignToGroup(GroupKind kind, string name, IEnumerable<string> accounts)
        {
            foreach (var account in accounts)
            {
                var state = GetAccountState(account);
                switch (kind)
                {
                    case GroupKind.Frozen:
                        state.Status = AccountStatus.Frozen;
                        state.GroupName = null;
                        break;
                    case GroupKind.Crashed:
                        state.Status = AccountStatus.Crashed;
                        state.GroupName = null;
                        break;
                    case GroupKind.Ungrouped:
                        state.Status = AccountStatus.Active;
                        state.GroupName = null;
                        break;
                    case GroupKind.Custom:
                        state.Status = AccountStatus.Active;
                        state.GroupName = name;
                        break;
                }
            }

            _hasChanges = true;
            RefreshSections();
        }

        private IEnumerable<GroupListItem> BuildGroupListItems(string filter)
        {
            var sections = BuildGroupDefinitions();
            foreach (var section in sections)
            {
                yield return GroupListItem.Header(section.Name, section.Kind);

                foreach (var exe in _executables.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!MatchesFilter(exe.Name, filter))
                    {
                        continue;
                    }

                    if (BelongsToGroup(exe.Name, section.Kind, section.Name))
                    {
                        yield return GroupListItem.Account(section.Name, section.Kind, exe.Name);
                    }
                }
            }
        }

        private bool BelongsToGroup(string accountName, GroupKind kind, string groupName)
        {
            var state = GetAccountState(accountName);
            return kind switch
            {
                GroupKind.Frozen => state.Status == AccountStatus.Frozen,
                GroupKind.Crashed => state.Status == AccountStatus.Crashed,
                GroupKind.Ungrouped => state.Status == AccountStatus.Active && string.IsNullOrWhiteSpace(state.GroupName),
                GroupKind.Custom => state.Status == AccountStatus.Active &&
                                    string.Equals(state.GroupName, groupName, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private void GroupListOnDrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _groupList.Items.Count)
            {
                return;
            }

            var item = (GroupListItem)_groupList.Items[e.Index];
            e.DrawBackground();

            var bounds = e.Bounds;
            var textColor = SystemColors.ControlText;
            if (item.IsHeader)
            {
                using var headerFont = new Font(e.Font, FontStyle.Bold);
                var headerRect = new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
                using var headerBrush = new SolidBrush(Color.FromArgb(242, 242, 242));
                e.Graphics.FillRectangle(headerBrush, headerRect);
                TextRenderer.DrawText(e.Graphics, item.Text, headerFont, headerRect, textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            else
            {
                var indentRect = new Rectangle(bounds.Left + 18, bounds.Top, bounds.Width - 18, bounds.Height);
                TextRenderer.DrawText(e.Graphics, item.Text, e.Font, indentRect, textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            e.DrawFocusRectangle();
        }

        private void GroupListOnMouseDown(object? sender, MouseEventArgs e)
        {
            int index = _groupList.IndexFromPoint(e.Location);
            if (index < 0)
            {
                return;
            }

            var item = (GroupListItem)_groupList.Items[index];
            if (item.IsHeader)
            {
                _groupList.ClearSelected();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            var names = _groupList.SelectedItems
                .Cast<GroupListItem>()
                .Where(i => !i.IsHeader)
                .Select(i => i.AccountName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();

            if (names.Length > 0)
            {
                _groupList.DoDragDrop(new DataObject("AccountNames", names), DragDropEffects.Copy);
            }
        }

        private void GroupListOnMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            int index = _groupList.IndexFromPoint(e.Location);
            if (index < 0)
            {
                return;
            }

            var item = (GroupListItem)_groupList.Items[index];
            if (item.IsHeader)
            {
                var menu = new ContextMenuStrip();
                var openItem = new ToolStripMenuItem("\u041e\u0442\u043a\u0440\u044b\u0442\u044c");
                var closeItem = new ToolStripMenuItem("\u0417\u0430\u043a\u0440\u044b\u0442\u044c");
                bool enabled = item.Kind != GroupKind.Frozen && item.Kind != GroupKind.Crashed;
                openItem.Enabled = enabled;
                closeItem.Enabled = enabled;
                openItem.Click += (_, __) => OnGroupOpen(item.Kind, item.GroupName);
                closeItem.Click += (_, __) => OnGroupClose(item.Kind, item.GroupName);
                menu.Items.Add(openItem);
                menu.Items.Add(closeItem);
                menu.Show(_groupList, e.Location);
                return;
            }

            if (!_groupList.SelectedIndices.Contains(index))
            {
                _groupList.ClearSelected();
                _groupList.SelectedIndex = index;
            }

            var names = _groupList.SelectedItems
                .Cast<GroupListItem>()
                .Where(i => !i.IsHeader)
                .Select(i => i.AccountName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();
            if (names.Length == 0)
            {
                return;
            }

            var assignMenu = new ContextMenuStrip();
            foreach (var action in BuildGroupActions())
            {
                var menuItem = new ToolStripMenuItem(action.Name);
                menuItem.Click += (_, __) => OnAssignToGroup(action.Kind, action.Name, names);
                assignMenu.Items.Add(menuItem);
            }

            assignMenu.Show(_groupList, e.Location);
        }

        private void GroupListOnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent("AccountNames"))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void GroupListOnDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent("AccountNames"))
            {
                return;
            }

            var names = e.Data.GetData("AccountNames") as string[];
            if (names == null || names.Length == 0)
            {
                return;
            }

            var point = _groupList.PointToClient(new Point(e.X, e.Y));
            int index = _groupList.IndexFromPoint(point);
            if (index < 0)
            {
                return;
            }

            int headerIndex = FindHeaderIndex(index);
            if (headerIndex < 0)
            {
                return;
            }

            var header = (GroupListItem)_groupList.Items[headerIndex];
            OnAssignToGroup(header.Kind, header.GroupName, names);
        }

        private int FindHeaderIndex(int startIndex)
        {
            for (int i = startIndex; i >= 0; i--)
            {
                var item = (GroupListItem)_groupList.Items[i];
                if (item.IsHeader)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool MatchesFilter(string accountName, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            return accountName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private HashSet<string> GetAccountsForGroup(GroupKind kind, string name)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var exe in _executables)
            {
                var state = GetAccountState(exe.Name);
                if (kind == GroupKind.Frozen && state.Status == AccountStatus.Frozen)
                {
                    result.Add(exe.Name);
                }
                else if (kind == GroupKind.Crashed && state.Status == AccountStatus.Crashed)
                {
                    result.Add(exe.Name);
                }
                else if (kind == GroupKind.Ungrouped && state.Status == AccountStatus.Active && string.IsNullOrWhiteSpace(state.GroupName))
                {
                    result.Add(exe.Name);
                }
                else if (kind == GroupKind.Custom && state.Status == AccountStatus.Active &&
                         string.Equals(state.GroupName, name, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(exe.Name);
                }
            }

            return result;
        }

        private AccountState GetAccountState(string accountName)
        {
            if (_settings.AccountStates.TryGetValue(accountName, out var state) && state != null)
            {
                return state;
            }

            state = new AccountState();
            _settings.AccountStates[accountName] = state;
            return state;
        }

        private static bool IsReservedGroupName(string name)
        {
            return string.Equals(name, GroupUngrouped, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, GroupFrozen, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, GroupCrashed, StringComparison.OrdinalIgnoreCase);
        }

        private List<GroupAction> BuildGroupActions()
        {
            var actions = new List<GroupAction>
            {
                new GroupAction(GroupUngrouped, GroupKind.Ungrouped),
                new GroupAction(GroupFrozen, GroupKind.Frozen),
                new GroupAction(GroupCrashed, GroupKind.Crashed)
            };

            foreach (var group in _settings.AccountGroups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(group.Name))
                {
                    actions.Add(new GroupAction(group.Name, GroupKind.Custom));
                }
            }

            return actions;
        }

        private void InitializeWatcher()
        {
            if (string.IsNullOrWhiteSpace(_baseDir) || !System.IO.Directory.Exists(_baseDir))
            {
                return;
            }

            _rescanTimer = new System.Windows.Forms.Timer
            {
                Interval = 200
            };
            _rescanTimer.Tick += (_, __) => HandleRescanTimer();

            _baseDirWatcher = new FileSystemWatcher(_baseDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };
            _baseDirWatcher.Created += OnBaseDirChanged;
            _baseDirWatcher.Renamed += OnBaseDirChanged;
            _baseDirWatcher.EnableRaisingEvents = true;
        }

        private void DisposeWatcher()
        {
            if (_baseDirWatcher != null)
            {
                _baseDirWatcher.EnableRaisingEvents = false;
                _baseDirWatcher.Created -= OnBaseDirChanged;
                _baseDirWatcher.Renamed -= OnBaseDirChanged;
                _baseDirWatcher.Dispose();
                _baseDirWatcher = null;
            }

            if (_rescanTimer != null)
            {
                _rescanTimer.Stop();
                _rescanTimer.Dispose();
                _rescanTimer = null;
            }
        }

        private void OnBaseDirChanged(object? sender, FileSystemEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.FullPath) || !System.IO.Directory.Exists(e.FullPath))
            {
                return;
            }

            ScheduleRescan();
        }

        private void ScheduleRescan()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ScheduleRescan));
                return;
            }

            lock (_rescanLock)
            {
                _rescanTimer?.Stop();
                _rescanTimer?.Start();
            }
        }

        private void HandleRescanTimer()
        {
            _rescanTimer?.Stop();
            RefreshFromDisk();
        }

        private void RefreshFromDisk()
        {
            if (string.IsNullOrWhiteSpace(_baseDir) || !System.IO.Directory.Exists(_baseDir))
            {
                return;
            }

            _executables = _processManager.DiscoverExecutables(_baseDir);
            EnsureAccountStates();
            RefreshRemoveGroupCombo();
            RefreshSections();
        }

        private enum GroupKind
        {
            Ungrouped,
            Frozen,
            Crashed,
            Custom
        }

        private readonly struct GroupDefinition
        {
            public GroupDefinition(string name, GroupKind kind)
            {
                Name = name;
                Kind = kind;
            }

            public string Name { get; }
            public GroupKind Kind { get; }
        }

        private sealed class GroupListItem
        {
            private GroupListItem(string text, string groupName, GroupKind kind, string? accountName, bool isHeader)
            {
                Text = text;
                GroupName = groupName;
                Kind = kind;
                AccountName = accountName;
                IsHeader = isHeader;
            }

            public static GroupListItem Header(string name, GroupKind kind)
            {
                return new GroupListItem(name, name, kind, null, true);
            }

            public static GroupListItem Account(string groupName, GroupKind kind, string accountName)
            {
                return new GroupListItem(accountName, groupName, kind, accountName, false);
            }

            public string Text { get; }
            public string GroupName { get; }
            public GroupKind Kind { get; }
            public string? AccountName { get; }
            public bool IsHeader { get; }
        }

        private sealed class GroupAction
        {
            public GroupAction(string name, GroupKind kind)
            {
                Name = name;
                Kind = kind;
            }

            public string Name { get; }
            public GroupKind Kind { get; }
        }
    }
}
