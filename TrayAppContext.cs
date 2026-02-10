using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Reflection;

namespace TelegramTrayLauncher
{
    internal class TrayAppContext : ApplicationContext
    {
        private const string GroupUngroupedLabel = "Без группы";

        private string _baseDir;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _openAllMenu;
        private readonly ToolStripMenuItem _openSingleMenu;
        private readonly ToolStripMenuItem _openGroupAccountMenu;
        private readonly ToolStripMenuItem _closeSingleMenu;
        private readonly ToolStripMenuItem _closeAllMenu;
        private readonly ToolStripMenuItem _openGroupMenuItem;
        private readonly ToolStripMenuItem _closeGroupMenuItem;
        private readonly ToolStripMenuItem _settingsMenu;
        private readonly ToolStripMenuItem _accountsMenuItem;
        private readonly ToolStripMenuItem _scaleMenuItem;
        private readonly ToolStripMenuItem _changeBaseDirMenuItem;
        private readonly ToolStripMenuItem _templatesMenuItem;
        private readonly ToolStripMenuItem _templatesToggleItem;
        private readonly ToolStripMenuItem _checkTelegramUpdateMenuItem;
        private readonly TelegramProcessManager _processManager;
        private readonly OverlayManager _overlayManager;
        private readonly SettingsStore _settingsStore = new SettingsStore();
        private readonly TemplateHotkeyManager _templateHotkeyManager;
        private readonly TelegramUpdateManager _updateManager;
        private readonly AppUpdateManager _appUpdateManager;
        private readonly ToolStripMenuItem _aboutMenuItem;
        private SettingsStore.Settings _settings;
        private readonly bool _useConsole;
        private readonly SynchronizationContext _uiContext;
        private System.Threading.Timer? _accountRefreshTimer;
        private int _accountRefreshInProgress;
        private FileSystemWatcher? _baseDirWatcher;
        private System.Windows.Forms.Timer? _baseDirRescanTimer;
        private readonly object _baseDirRescanLock = new object();
        private int _baseDirRescanInProgress;
        private int _baseDirRescanPending;
        private static readonly PropertyInfo? UpScrollButtonProperty = typeof(ToolStripDropDownMenu).GetProperty("UpScrollButton", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo? DownScrollButtonProperty = typeof(ToolStripDropDownMenu).GetProperty("DownScrollButton", BindingFlags.Instance | BindingFlags.NonPublic);

        public TrayAppContext(bool useConsole, string baseDir)
        {
            _useConsole = useConsole;
            _baseDir = baseDir;

            _processManager = new TelegramProcessManager(Log);
            _overlayManager = new OverlayManager(Log);
            _settings = _settingsStore.Load();
            ExplorerContextMenuManager.InstallOrUpdate(_settings, Log);
            _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _templateHotkeyManager = new TemplateHotkeyManager(Log, _uiContext);
            _updateManager = new TelegramUpdateManager(_baseDir, Log, _uiContext);
            _appUpdateManager = new AppUpdateManager(Log, _uiContext, ExitForUpdate);

            _menu = new ContextMenuStrip();

            _openAllMenu = new ToolStripMenuItem("Открыть все аккаунты");
            _openAllMenu.Click += (_, __) => OpenAllAccounts();

            _openSingleMenu = new ToolStripMenuItem("Открыть аккаунт");
            _openSingleMenu.DropDownOpening += OpenSingleMenuOnDropDownOpening;

            _openGroupAccountMenu = new ToolStripMenuItem("Открыть аккаунт группы");
            _openGroupAccountMenu.DropDownOpening += OpenGroupAccountMenuOnDropDownOpening;

            _closeSingleMenu = new ToolStripMenuItem("Закрыть выбранный аккаунт");
            _closeSingleMenu.Click += (_, __) => OpenAccountCloseDialog();

            _closeAllMenu = new ToolStripMenuItem("Закрыть все аккаунты");
            _closeAllMenu.Click += (_, __) => QueueCloseAllTelegram();

            _openGroupMenuItem = new ToolStripMenuItem("Открыть группу");
            _openGroupMenuItem.DropDownOpening += (_, __) => PopulateGroupMenu(_openGroupMenuItem, StartGroupFromMenu);

            _closeGroupMenuItem = new ToolStripMenuItem("Закрыть группу");
            _closeGroupMenuItem.DropDownOpening += (_, __) => PopulateGroupMenu(_closeGroupMenuItem, CloseGroupFromMenu);

            _settingsMenu = new ToolStripMenuItem("Параметры запуска");
            _scaleMenuItem = new ToolStripMenuItem("Масштаб интерфейса");
            _scaleMenuItem.Click += (_, __) => PromptScale();
            _settingsMenu.DropDownItems.Add(_scaleMenuItem);
            _changeBaseDirMenuItem = new ToolStripMenuItem("Сменить папку аккаунтов");
            _changeBaseDirMenuItem.Click += (_, __) => PromptChangeBaseDirectory();
            _settingsMenu.DropDownItems.Add(_changeBaseDirMenuItem);

            _accountsMenuItem = new ToolStripMenuItem("Управление аккаунтами");
            _accountsMenuItem.Click += (_, __) => OpenAccountManager();

            _templatesMenuItem = new ToolStripMenuItem("Шаблоны");
            _templatesMenuItem.Click += (_, __) => OpenTemplatesWindow();

            _templatesToggleItem = new ToolStripMenuItem();
            _templatesToggleItem.Click += (_, __) => ToggleTemplates();
            UpdateTemplatesToggleTitle();
            _templateHotkeyManager.Configure(_settings.Templates, _settings.TemplatesEnabled);

            _checkTelegramUpdateMenuItem = new ToolStripMenuItem("Проверить обновления Telegram");
            _checkTelegramUpdateMenuItem.Click += (_, __) => _updateManager.Start(userInitiated: true);

            _aboutMenuItem = new ToolStripMenuItem("О программе");
            _aboutMenuItem.Click += (_, __) => ShowAboutDialog();

            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (_, __) => ExitApplication();

            _menu.Items.Add(_openAllMenu);
            _menu.Items.Add(_closeAllMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_openSingleMenu);
            _menu.Items.Add(_openGroupAccountMenu);
            _menu.Items.Add(_closeSingleMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_openGroupMenuItem);
            _menu.Items.Add(_closeGroupMenuItem);
            _menu.Items.Add(_accountsMenuItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_settingsMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_templatesMenuItem);
            _menu.Items.Add(_templatesToggleItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_checkTelegramUpdateMenuItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_aboutMenuItem);
            _menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateTrayIcon(),
                Visible = true,
                Text = "Telegram launcher",
                ContextMenuStrip = _menu
            };

            _notifyIcon.DoubleClick += (_, __) =>
            {
                _notifyIcon.ShowBalloonTip(
                    2000,
                    "Telegram launcher",
                    "Программа запущена и отслеживает Telegram.exe в подпапках.",
                    ToolTipIcon.Info);
            };

            Log($"Tray icon created. Base directory: {_baseDir}");
            ShowAboutOnFirstLaunchOrUpdate();
            StartAccountRefreshPolling();
            _updateManager.Start();
            _appUpdateManager.Start();
        }

        private void Log(string message)
        {
            if (_useConsole)
            {
                ConsoleHelper.Log(message);
            }
            else
            {
                Debug.WriteLine(message);
            }
        }

        private void OpenAccountManager()
        {
            using var form = new AccountManagerForm(_baseDir, _processManager, _settingsStore, _settings);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _settings = _settingsStore.Load();
                ExplorerContextMenuManager.InstallOrUpdate(_settings, Log);
            }
        }

        private bool IsAccountActive(string accountName)
        {
            if (_settings.AccountStates != null &&
                _settings.AccountStates.TryGetValue(accountName, out var state))
            {
                return state == null || state.Status == AccountStatus.Active;
            }

            return true;
        }

        private void PopulateGroupMenu(ToolStripMenuItem menuItem, Action<string> action)
        {
            menuItem.DropDownItems.Clear();
            _settings = _settingsStore.Load();

            var ungrouped = new ToolStripMenuItem(GroupUngroupedLabel);
            ungrouped.Click += (_, __) => action(GroupUngroupedLabel);
            menuItem.DropDownItems.Add(ungrouped);

            var groups = GetSortedGroupNames();
            if (groups.Count > 0)
            {
                menuItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var name in groups)
                {
                    var item = new ToolStripMenuItem(name);
                    item.Click += (_, __) => action(name);
                    menuItem.DropDownItems.Add(item);
                }
            }
        }

        private void StartGroupFromMenu(string groupName)
        {
            var executables = GetExecutablesForGroup(groupName);
            _processManager.StartExecutables(executables, NormalizeScale(_settings.Scale));
        }

        private void CloseGroupFromMenu(string groupName)
        {
            var executables = GetExecutablesForGroup(groupName);
            var directories = executables.Select(e => e.Directory).ToList();
            _processManager.CloseTelegramForDirectories(directories, _baseDir);
        }

        private List<TelegramProcessManager.TelegramExecutable> GetExecutablesForGroup(string groupName)
        {
            _settings = _settingsStore.Load();
            bool ungrouped = string.Equals(groupName, GroupUngroupedLabel, StringComparison.OrdinalIgnoreCase);
            var executables = _processManager.DiscoverExecutables(_baseDir);
            var result = new List<TelegramProcessManager.TelegramExecutable>();

            foreach (var exe in executables)
            {
                if (!_settings.AccountStates.TryGetValue(exe.Name, out var state) || state == null)
                {
                    state = new AccountState();
                    _settings.AccountStates[exe.Name] = state;
                }

                if (state.Status != AccountStatus.Active)
                {
                    continue;
                }

                if (ungrouped)
                {
                    if (string.IsNullOrWhiteSpace(state.GroupName))
                    {
                        result.Add(exe);
                    }
                }
                else if (string.Equals(state.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(exe);
                }
            }

            return result;
        }

        private void OpenAccountCloseDialog()
        {
            var processes = _processManager.GetTrackedTelegramProcesses(_baseDir);
            if (processes.Count == 0)
            {
                MessageBox.Show("Нет открытых аккаунтов.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var entries = BuildAccountEntries(processes);
            using var dialog = new AccountSelectorForm(entries, _overlayManager, pid => _processManager.CloseSingleTelegram(pid, _baseDir));
            dialog.ShowDialog();
        }

        private List<AccountEntry> BuildAccountEntries(List<Process> processes)
        {
            var result = new List<AccountEntry>();
            for (int i = 0; i < processes.Count; i++)
            {
                var process = processes[i];
                string exeDir = string.Empty;
                string labelFolder = "неизвестно";
                try
                {
                    var fileName = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        exeDir = Path.GetDirectoryName(fileName) ?? string.Empty;
                        labelFolder = Path.GetFileName(exeDir);
                    }
                }
                catch
                {
                    // ignore inability to read
                }

                var displayIndex = i + 1;
                var label = $"Аккаунт {displayIndex} — {labelFolder}";
                result.Add(new AccountEntry(displayIndex.ToString(), label, process.Id));
            }

            return result;
        }

        private void OpenSingleMenuOnDropDownOpening(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem menuItem)
            {
                return;
            }

            menuItem.DropDownItems.Clear();
            _settings = _settingsStore.Load();
            var runningDirs = GetRunningTelegramDirectories();
            var executables = _processManager.DiscoverExecutables(_baseDir)
                .Where(exe => IsAccountActive(exe.Name))
                .ToList();
            if (executables.Count == 0)
            {
                menuItem.DropDownItems.Add(new ToolStripMenuItem("Нет доступных аккаунтов") { Enabled = false });
                return;
            }

            var scale = NormalizeScale(_settings.Scale);
            int preferredWidth = 280;
            foreach (var exe in executables)
            {
                var groupLabel = GetAccountGroupLabel(exe.Name);
                bool isRunning = runningDirs.Contains(exe.Directory);
                var item = new AccountMenuItem(exe.Name, groupLabel, isRunning);
                string path = exe.ExePath;
                string workDir = exe.Directory;
                item.Click += (_, __) => _processManager.StartSingle(path, workDir, scale);
                menuItem.DropDownItems.Add(item);

                string combined = exe.Name + " - " + groupLabel;
                int lineWidth = TextRenderer.MeasureText(combined, menuItem.Font).Width + 56;
                if (lineWidth > preferredWidth)
                {
                    preferredWidth = lineWidth;
                }
            }

            menuItem.DropDown.AutoSize = true;
            menuItem.DropDown.MinimumSize = new Size(Math.Min(700, preferredWidth), 0);
            menuItem.DropDown.MouseWheel -= OpenSingleDropDownOnMouseWheel;
            menuItem.DropDown.MouseWheel += OpenSingleDropDownOnMouseWheel;
            menuItem.DropDownItems.Add(new ToolStripSeparator());
            menuItem.DropDownItems.Add(new ToolStripMenuItem("О — открыт, З — закрыт") { Enabled = false });
        }

        private void OpenGroupAccountMenuOnDropDownOpening(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem menuItem)
            {
                return;
            }

            menuItem.DropDownItems.Clear();
            _settings = _settingsStore.Load();

            var ungrouped = new ToolStripMenuItem(GroupUngroupedLabel);
            ungrouped.DropDownOpening += (_, __) => PopulateAccountsForGroup(ungrouped, GroupUngroupedLabel);
            menuItem.DropDownItems.Add(ungrouped);

            var groups = GetSortedGroupNames();
            if (groups.Count > 0)
            {
                menuItem.DropDownItems.Add(new ToolStripSeparator());
                foreach (var name in groups)
                {
                    var item = new ToolStripMenuItem(name);
                    item.DropDownOpening += (_, __) => PopulateAccountsForGroup(item, name);
                    menuItem.DropDownItems.Add(item);
                }
            }
        }

        private void PopulateAccountsForGroup(ToolStripMenuItem groupMenuItem, string groupName)
        {
            groupMenuItem.DropDownItems.Clear();
            _settings = _settingsStore.Load();
            var runningDirs = GetRunningTelegramDirectories();
            var executables = GetExecutablesForGroup(groupName);
            if (executables.Count == 0)
            {
                groupMenuItem.DropDownItems.Add(new ToolStripMenuItem("Нет доступных аккаунтов") { Enabled = false });
                return;
            }

            var scale = NormalizeScale(_settings.Scale);
            foreach (var exe in executables)
            {
                bool isRunning = runningDirs.Contains(exe.Directory);
                var item = new AccountMenuItem(exe.Name, GetAccountGroupLabel(exe.Name), isRunning);
                string path = exe.ExePath;
                string workDir = exe.Directory;
                item.Click += (_, __) => _processManager.StartSingle(path, workDir, scale);
                groupMenuItem.DropDownItems.Add(item);
            }

            groupMenuItem.DropDownItems.Add(new ToolStripSeparator());
            groupMenuItem.DropDownItems.Add(new ToolStripMenuItem("О — открыт, З — закрыт") { Enabled = false });
        }

        private void OpenSingleDropDownOnMouseWheel(object? sender, MouseEventArgs e)
        {
            if (sender is not ToolStripDropDownMenu dropDownMenu || e.Delta == 0)
            {
                return;
            }

            var buttonProperty = e.Delta > 0 ? UpScrollButtonProperty : DownScrollButtonProperty;
            if (buttonProperty?.GetValue(dropDownMenu) is not ToolStripItem scrollButton)
            {
                return;
            }

            if (!scrollButton.Available || !scrollButton.Enabled)
            {
                return;
            }

            int steps = Math.Max(1, Math.Abs(e.Delta) / SystemInformation.MouseWheelScrollDelta);
            for (int i = 0; i < steps; i++)
            {
                if (!scrollButton.Available || !scrollButton.Enabled)
                {
                    break;
                }

                scrollButton.PerformClick();
            }
        }

        private string GetAccountGroupLabel(string accountName)
        {
            if (!_settings.AccountStates.TryGetValue(accountName, out var state) || state == null)
            {
                return GroupUngroupedLabel;
            }

            return string.IsNullOrWhiteSpace(state.GroupName) ? GroupUngroupedLabel : state.GroupName;
        }

        private List<string> GetSortedGroupNames()
        {
            return _settings.AccountGroups
                .Select(g => g.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void OpenAllAccounts()
        {
            var executables = _processManager.DiscoverExecutables(_baseDir)
                .Where(exe => IsAccountActive(exe.Name))
                .ToList();
            _processManager.StartExecutables(executables, NormalizeScale(_settings.Scale));
        }

        private void CloseAllTelegram()
        {
            _processManager.CloseAllTelegram(_baseDir);
        }

        private void QueueCloseAllTelegram()
        {
            Log("[DBG] QueueCloseAllTelegram queued.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(180);
                _uiContext.Post(async _ => await CloseAllTelegramAsync(), null);
            });
        }

        private async Task CloseAllTelegramAsync()
        {
            if (!_closeAllMenu.Enabled)
            {
                Log("[DBG] CloseAllTelegramAsync skipped: already running.");
                return;
            }

            _closeAllMenu.Enabled = false;
            try
            {
                Log("[DBG] CloseAllTelegramAsync started.");
                await Task.Run(() => _processManager.CloseAllTelegram(_baseDir));
                Log("[DBG] CloseAllTelegramAsync finished.");
            }
            catch (Exception ex)
            {
                Log("Ошибка закрытия всех аккаунтов: " + ex.Message);
            }
            finally
            {
                _closeAllMenu.Enabled = true;
                Log("[DBG] CloseAllTelegramAsync UI state restored.");
            }
        }

        private void ExitApplication()
        {
            Log("ExitApplication called.");
            try
            {
                DisposeAccountRefreshPolling();
                DisposeBaseDirWatcher();
                _overlayManager.HideOverlays();
                _templateHotkeyManager.Dispose();
            }
            catch (Exception ex)
            {
                Log("Ошибка при выходе: " + ex.Message);
            }
            finally
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                Log("Application exiting.");
                Application.Exit();
            }
        }

        private void ExitForUpdate()
        {
            Log("ExitForUpdate called.");
            try
            {
                DisposeAccountRefreshPolling();
                DisposeBaseDirWatcher();
                _overlayManager.HideOverlays();
                _templateHotkeyManager.Dispose();
            }
            catch (Exception ex)
            {
                Log("Ошибка при выходе для обновления: " + ex.Message);
            }
            finally
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                Application.Exit();
                Task.Run(() =>
                {
                    Thread.Sleep(1500);
                    Environment.Exit(0);
                });
            }
        }

        private void PromptScale()
        {
            using var form = new Form
            {
                Width = 300,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                Text = "Масштаб интерфейса",
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                TopMost = true
            };

            var label = new Label
            {
                Text = "Введите масштаб (-scale):",
                Left = 10,
                Top = 15,
                Width = 260
            };

            var input = new TextBox
            {
                Left = 10,
                Top = 40,
                Width = 260,
                Text = _settings.Scale ?? string.Empty
            };

            var ok = new Button { Text = "OK", Left = 110, Width = 75, Top = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 195, Width = 75, Top = 75, DialogResult = DialogResult.Cancel };

            form.Controls.Add(label);
            form.Controls.Add(input);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            if (form.ShowDialog() == DialogResult.OK)
            {
                _settings.Scale = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
                _settingsStore.Save(_settings);
            }
        }

        private void PromptChangeBaseDirectory()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the base folder that contains account subfolders (tdata).",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            var result = dialog.ShowDialog();
            if (result != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return;
            }

            string selected = dialog.SelectedPath;
            if (!Directory.Exists(selected))
            {
                MessageBox.Show("Selected folder does not exist.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!BaseDirectoryResolver.TryPersistWorkdir(selected, Log))
            {
                MessageBox.Show("Failed to save the base folder.", "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _baseDir = selected;
            _updateManager.UpdateBaseDir(_baseDir);
            QueueAccountRefresh();
            Log("Base directory updated: " + _baseDir);
        }

        private void StartAccountRefreshPolling()
        {
            if (_accountRefreshTimer != null)
            {
                return;
            }

            _accountRefreshTimer = new System.Threading.Timer(
                _ => QueueAccountRefresh(),
                null,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3));
            Log("[DBG] Account refresh polling started.");
        }

        private void DisposeAccountRefreshPolling()
        {
            var timer = Interlocked.Exchange(ref _accountRefreshTimer, null);
            if (timer != null)
            {
                timer.Dispose();
            }
        }

        private void QueueAccountRefresh()
        {
            if (Interlocked.CompareExchange(ref _accountRefreshInProgress, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    RefreshKnownAccountsFromDisk();
                }
                finally
                {
                    Interlocked.Exchange(ref _accountRefreshInProgress, 0);
                }
            });
        }

        private string? NormalizeScale(string? scale)
        {
            var trimmed = scale?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        private void OpenTemplatesWindow()
        {
            using var form = new TemplateListForm(_settings.Templates);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _settings.Templates = form.Templates;
                _settingsStore.Save(_settings);
                _templateHotkeyManager.Configure(_settings.Templates, _settings.TemplatesEnabled);
            }
        }

        private void ToggleTemplates()
        {
            _settings.TemplatesEnabled = !_settings.TemplatesEnabled;
            _templateHotkeyManager.SetEnabled(_settings.TemplatesEnabled);
            UpdateTemplatesToggleTitle();
            _settingsStore.Save(_settings);
        }

        private void UpdateTemplatesToggleTitle()
        {
            _templatesToggleItem.Text = _settings.TemplatesEnabled
                ? "Отключить шаблоны"
                : "Включить шаблоны";
        }

        private static string GetAppVersion()
        {
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            var infoAttr = entry?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
            var infoVersion = infoAttr?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                return NormalizeVersion(infoVersion);
            }

            var nameVersion = entry?.GetName().Version?.ToString();
            return string.IsNullOrWhiteSpace(nameVersion) ? "unknown" : NormalizeVersion(nameVersion);
        }

        private static string NormalizeVersion(string value)
        {
            var text = value.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1);
            }

            int plusIndex = text.IndexOf('+');
            if (plusIndex >= 0)
            {
                text = text.Substring(0, plusIndex);
            }

            int dashIndex = text.IndexOf('-');
            if (dashIndex >= 0)
            {
                text = text.Substring(0, dashIndex);
            }

            return text.Trim();
        }

        private void ShowAboutDialog()
        {
            string version = GetAppVersion();
            string message =
                "Telegram Manager" + Environment.NewLine +
                "\u0412\u0435\u0440\u0441\u0438\u044f: " + version + Environment.NewLine +
                Environment.NewLine +
                "\u041f\u043e\u0441\u043b\u0435\u0434\u043d\u0438\u0435 \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f:" + Environment.NewLine +
                "\u2022 \u0414\u043e\u0431\u0430\u0432\u043b\u0435\u043d\u043e \u043c\u0435\u043d\u044e \u00ab\u041e\u0442\u043a\u0440\u044b\u0442\u044c \u0430\u043a\u043a\u0430\u0443\u043d\u0442 \u0433\u0440\u0443\u043f\u043f\u044b\u00bb (\u0433\u0440\u0443\u043f\u043f\u044b \u2192 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u044b)." + Environment.NewLine +
                "\u2022 \u0421\u0442\u0430\u0431\u0438\u043b\u044c\u043d\u043e\u0441\u0442\u044c \u0442\u0440\u0435\u044f: \u043e\u043f\u0435\u0440\u0430\u0446\u0438\u044f \u00ab\u0417\u0430\u043a\u0440\u044b\u0442\u044c \u0432\u0441\u0435 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u044b\u00bb \u0432\u044b\u043d\u0435\u0441\u0435\u043d\u0430 \u0432 \u043e\u0447\u0435\u0440\u0435\u0434\u044c \u0431\u0435\u0437 \u0431\u043b\u043e\u043a\u0438\u0440\u043e\u0432\u043a\u0438 UI." + Environment.NewLine +
                "\u2022 \u0410\u0432\u0442\u043e\u043f\u043e\u0434\u0445\u0432\u0430\u0442 \u043d\u043e\u0432\u044b\u0445 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u043e\u0432 \u043f\u0435\u0440\u0435\u0432\u0435\u0434\u0435\u043d \u043d\u0430 \u0431\u0435\u0437\u043e\u043f\u0430\u0441\u043d\u044b\u0439 polling (\u0431\u0435\u0437 FileSystemWatcher)." + Environment.NewLine +
                "\u2022 \u041f\u0440\u0438 \u0432\u044b\u0445\u043e\u0434\u0435 \u0438\u0437 \u043c\u0435\u043d\u0435\u0434\u0436\u0435\u0440\u0430 Telegram-\u0430\u043a\u043a\u0430\u0443\u043d\u0442\u044b \u0431\u043e\u043b\u044c\u0448\u0435 \u043d\u0435 \u0437\u0430\u043a\u0440\u044b\u0432\u0430\u044e\u0442\u0441\u044f." + Environment.NewLine +
                "\u2022 \u041f\u0440\u043e\u0446\u0435\u0441\u0441 \u0440\u0435\u043b\u0438\u0437\u0430: \u043f\u0435\u0440\u0435\u0434 push \u043e\u0431\u043d\u043e\u0432\u043b\u044f\u0435\u043c About \u043f\u043e \u0432\u0441\u0435\u043c \u0438\u0437\u043c\u0435\u043d\u0435\u043d\u0438\u044f\u043c \u0440\u0435\u043b\u0438\u0437\u0430." + Environment.NewLine +
                Environment.NewLine +
                "Credential: DemaNFox";

            MessageBox.Show(message, "\u041e \u043f\u0440\u043e\u0433\u0440\u0430\u043c\u043c\u0435", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowAboutOnFirstLaunchOrUpdate()
        {
            string currentVersion = GetAppVersion();
            string? lastSeenVersion = _settings.LastSeenAboutVersion;
            if (string.Equals(lastSeenVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ShowAboutDialog();
            _settings.LastSeenAboutVersion = currentVersion;
            _settingsStore.Save(_settings);
        }

        private void InitializeBaseDirWatcher()
        {
            if (string.IsNullOrWhiteSpace(_baseDir) || !Directory.Exists(_baseDir))
            {
                return;
            }

            _baseDirRescanTimer = new System.Windows.Forms.Timer
            {
                Interval = 200
            };
            _baseDirRescanTimer.Tick += (_, __) => HandleBaseDirRescan();

            _baseDirWatcher = new FileSystemWatcher(_baseDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };
            _baseDirWatcher.Created += OnBaseDirChanged;
            _baseDirWatcher.Renamed += OnBaseDirChanged;
            _baseDirWatcher.EnableRaisingEvents = true;
        }

        private void UpdateBaseDirWatcher(string newBaseDir)
        {
            DisposeBaseDirWatcher();
            _baseDir = newBaseDir ?? string.Empty;
            InitializeBaseDirWatcher();
        }

        private void DisposeBaseDirWatcher()
        {
            if (_baseDirWatcher != null)
            {
                _baseDirWatcher.EnableRaisingEvents = false;
                _baseDirWatcher.Created -= OnBaseDirChanged;
                _baseDirWatcher.Renamed -= OnBaseDirChanged;
                _baseDirWatcher.Dispose();
                _baseDirWatcher = null;
            }

            if (_baseDirRescanTimer != null)
            {
                _baseDirRescanTimer.Stop();
                _baseDirRescanTimer.Dispose();
                _baseDirRescanTimer = null;
            }
        }

        private void OnBaseDirChanged(object? sender, FileSystemEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.FullPath) || !Directory.Exists(e.FullPath))
            {
                return;
            }

            ScheduleBaseDirRescan();
        }

        private void ScheduleBaseDirRescan()
        {
            if (SynchronizationContext.Current != _uiContext)
            {
                _uiContext.Post(_ => ScheduleBaseDirRescan(), null);
                return;
            }

            lock (_baseDirRescanLock)
            {
                Interlocked.Exchange(ref _baseDirRescanPending, 1);
                _baseDirRescanTimer?.Stop();
                _baseDirRescanTimer?.Start();
            }
        }

        private void HandleBaseDirRescan()
        {
            _baseDirRescanTimer?.Stop();
            Interlocked.Exchange(ref _baseDirRescanPending, 0);
            if (Interlocked.CompareExchange(ref _baseDirRescanInProgress, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    RefreshKnownAccountsFromDisk();
                }
                finally
                {
                    Interlocked.Exchange(ref _baseDirRescanInProgress, 0);
                    if (Interlocked.Exchange(ref _baseDirRescanPending, 0) == 1)
                    {
                        _uiContext.Post(_ => ScheduleBaseDirRescan(), null);
                    }
                }
            });
        }

        private void RefreshKnownAccountsFromDisk()
        {
            if (string.IsNullOrWhiteSpace(_baseDir) || !Directory.Exists(_baseDir))
            {
                return;
            }

            var executables = _processManager.DiscoverExecutables(_baseDir);
            var settings = _settingsStore.Load();
            bool added = false;
            foreach (var exe in executables)
            {
                if (!settings.AccountStates.ContainsKey(exe.Name))
                {
                    settings.AccountStates[exe.Name] = new AccountState();
                    added = true;
                }
            }

            if (added)
            {
                _settingsStore.Save(settings);
                _uiContext.Post(_ => _settings = settings, null);
                Log("New account folders detected, settings updated.");
            }
        }

        private sealed class AccountMenuItem : ToolStripMenuItem
        {
            private readonly string _groupLabel;
            private readonly bool _isRunning;

            public AccountMenuItem(string accountName, string groupLabel, bool isRunning) : base(accountName)
            {
                _groupLabel = groupLabel;
                _isRunning = isRunning;
            }

            private string SuffixText => string.IsNullOrWhiteSpace(_groupLabel) ? string.Empty : " - " + _groupLabel;

            protected override void OnPaint(PaintEventArgs e)
            {
                var renderer = ToolStripManager.Renderer;
                renderer.DrawMenuItemBackground(new ToolStripItemRenderEventArgs(e.Graphics, this));

                var textBounds = ContentRectangle;
                if (textBounds.Width <= 0 || textBounds.Height <= 0)
                {
                    return;
                }

                const TextFormatFlags mainFlags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                var suffix = SuffixText;
                var statusText = _isRunning ? "\u041e" : "\u0417";
                var primaryColor = Enabled
                    ? (Selected ? SystemColors.HighlightText : ForeColor)
                    : SystemColors.GrayText;
                var secondaryColor = Enabled
                    ? (Selected ? Color.FromArgb(220, 220, 220) : SystemColors.GrayText)
                    : SystemColors.GrayText;
                var statusColor = _isRunning
                    ? (Selected ? SystemColors.HighlightText : Color.ForestGreen)
                    : (Selected ? SystemColors.HighlightText : Color.DarkRed);

                int statusWidth = TextRenderer.MeasureText(e.Graphics, statusText, Font, Size.Empty, mainFlags).Width + 12;
                statusWidth = Math.Max(20, statusWidth);
                var statusBounds = new Rectangle(
                    textBounds.Right - statusWidth,
                    textBounds.Top,
                    statusWidth,
                    textBounds.Height);

                int availableWidth = Math.Max(0, textBounds.Width - statusWidth);
                var contentBounds = new Rectangle(textBounds.Left, textBounds.Top, availableWidth, textBounds.Height);

                if (string.IsNullOrEmpty(suffix))
                {
                    TextRenderer.DrawText(e.Graphics, Text, Font, contentBounds, primaryColor, mainFlags | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(e.Graphics, statusText, Font, statusBounds, statusColor, mainFlags | TextFormatFlags.Right);
                    return;
                }

                int desiredSuffixWidth = TextRenderer.MeasureText(e.Graphics, suffix, Font, Size.Empty, mainFlags).Width;
                int maxSuffixWidth = Math.Max(0, (int)(contentBounds.Width * 0.45));
                int minSuffixWidth = Math.Max(0, Math.Min(80, contentBounds.Width / 2));
                int suffixWidth = Math.Min(desiredSuffixWidth, maxSuffixWidth);
                suffixWidth = Math.Max(suffixWidth, minSuffixWidth);
                var accountBounds = new Rectangle(contentBounds.Left, contentBounds.Top, Math.Max(0, contentBounds.Width - suffixWidth), contentBounds.Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, accountBounds, primaryColor, mainFlags | TextFormatFlags.EndEllipsis);

                int suffixX = accountBounds.Right;
                var suffixBounds = new Rectangle(suffixX, contentBounds.Top, Math.Max(0, contentBounds.Right - suffixX), contentBounds.Height);
                TextRenderer.DrawText(e.Graphics, suffix, Font, suffixBounds, secondaryColor, mainFlags | TextFormatFlags.EndEllipsis | TextFormatFlags.Right);
                TextRenderer.DrawText(e.Graphics, statusText, Font, statusBounds, statusColor, mainFlags | TextFormatFlags.Right);
            }
        }

        private HashSet<string> GetRunningTelegramDirectories()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processes = _processManager.GetTrackedTelegramProcesses(_baseDir);
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        result.Add(dir);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return result;
        }
    }

    internal sealed record AccountEntry(string Number, string Label, int Pid);
}

