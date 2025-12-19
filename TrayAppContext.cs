using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal class TrayAppContext : ApplicationContext
    {
        private readonly string _baseDir;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _openAllMenu;
        private readonly ToolStripMenuItem _openSingleMenu;
        private readonly ToolStripMenuItem _closeSingleMenu;
        private readonly ToolStripMenuItem _closeAllMenu;
        private readonly ToolStripMenuItem _settingsMenu;
        private readonly ToolStripMenuItem _scaleMenuItem;
        private readonly ToolStripMenuItem _templatesMenuItem;
        private readonly ToolStripMenuItem _templatesToggleItem;
        private readonly TelegramProcessManager _processManager;
        private readonly OverlayManager _overlayManager;
        private readonly SettingsStore _settingsStore = new SettingsStore();
        private readonly TemplateHotkeyManager _templateHotkeyManager;
        private SettingsStore.Settings _settings;
        private readonly bool _useConsole;

        public TrayAppContext(bool useConsole, string baseDir)
        {
            _useConsole = useConsole;
            _baseDir = baseDir;

            _processManager = new TelegramProcessManager(Log);
            _overlayManager = new OverlayManager(Log);
            _settings = _settingsStore.Load();
            _templateHotkeyManager = new TemplateHotkeyManager(Log, SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());

            _menu = new ContextMenuStrip();

            _openAllMenu = new ToolStripMenuItem("Открыть все аккаунты");
            _openAllMenu.Click += (_, __) => OpenAllAccounts();

            _openSingleMenu = new ToolStripMenuItem("Открыть аккаунт");
            _openSingleMenu.DropDownOpening += OpenSingleMenuOnDropDownOpening;

            _closeSingleMenu = new ToolStripMenuItem("Закрыть выбранный аккаунт");
            _closeSingleMenu.Click += (_, __) => OpenAccountCloseDialog();

            _closeAllMenu = new ToolStripMenuItem("Закрыть все аккаунты");
            _closeAllMenu.Click += (_, __) => CloseAllTelegram();

            _settingsMenu = new ToolStripMenuItem("Параметры запуска");
            _scaleMenuItem = new ToolStripMenuItem("Масштаб интерфейса");
            _scaleMenuItem.Click += (_, __) => PromptScale();
            _settingsMenu.DropDownItems.Add(_scaleMenuItem);

            _templatesMenuItem = new ToolStripMenuItem("Шаблоны");
            _templatesMenuItem.Click += (_, __) => OpenTemplatesWindow();

            _templatesToggleItem = new ToolStripMenuItem();
            _templatesToggleItem.Click += (_, __) => ToggleTemplates();
            UpdateTemplatesToggleTitle();
            _templateHotkeyManager.Configure(_settings.Templates, _settings.TemplatesEnabled);

            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (_, __) => ExitApplication();

            _menu.Items.Add(_openAllMenu);
            _menu.Items.Add(_openSingleMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_closeSingleMenu);
            _menu.Items.Add(_closeAllMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_settingsMenu);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_templatesMenuItem);
            _menu.Items.Add(_templatesToggleItem);
            _menu.Items.Add(new ToolStripSeparator());
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
            var executables = _processManager.DiscoverExecutables(_baseDir);
            if (executables.Count == 0)
            {
                menuItem.DropDownItems.Add(new ToolStripMenuItem("Нет доступных аккаунтов") { Enabled = false });
                return;
            }

            var scale = NormalizeScale(_settings.Scale);
            foreach (var exe in executables)
            {
                var item = new ToolStripMenuItem(exe.Name);
                string path = exe.ExePath;
                string workDir = exe.Directory;
                item.Click += (_, __) => _processManager.StartSingle(path, workDir, scale);
                menuItem.DropDownItems.Add(item);
            }
        }

        private void OpenAllAccounts()
        {
            _processManager.StartAllAvailable(_baseDir, NormalizeScale(_settings.Scale));
        }

        private void CloseAllTelegram()
        {
            _processManager.CloseAllTelegram(_baseDir);
        }

        private void ExitApplication()
        {
            Log("ExitApplication called.");
            try
            {
                _overlayManager.HideOverlays();
                _templateHotkeyManager.Dispose();
                CloseAllTelegram();
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
    }

    internal sealed record AccountEntry(string Number, string Label, int Pid);
}
