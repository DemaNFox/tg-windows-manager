using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class AccountSelectorForm : Form
    {
        private readonly List<AccountEntry> _entries;
        private readonly OverlayManager _overlayManager;
        private readonly Action<int> _onCloseAccount;
        private readonly ListBox _list;
        private readonly Button _closeButton;
        private readonly Button _cancelButton;

        public AccountSelectorForm(
            List<AccountEntry> entries,
            OverlayManager overlayManager,
            Action<int> onCloseAccount)
        {
            _entries = entries;
            _overlayManager = overlayManager;
            _onCloseAccount = onCloseAccount;

            Text = "Выбор аккаунта";
            Width = 360;
            Height = 280;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = false;

            _list = new ListBox
            {
                Left = 10,
                Top = 10,
                Width = 330,
                Height = 190
            };

            foreach (var entry in entries)
            {
                _list.Items.Add(entry.Label);
            }

            _list.DoubleClick += (_, __) => CloseSelected();

            _closeButton = new Button
            {
                Text = "Закрыть выбранный",
                Left = 140,
                Width = 120,
                Top = 210,
                DialogResult = DialogResult.None
            };
            _closeButton.Click += (_, __) => CloseSelected();

            _cancelButton = new Button
            {
                Text = "Отмена",
                Left = 270,
                Width = 70,
                Top = 210,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(_list);
            Controls.Add(_closeButton);
            Controls.Add(_cancelButton);

            _overlayManager.ShowForEntries(entries, pid => CloseByPid(pid));
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _overlayManager.HideOverlays();
        }

        private void CloseByPid(int pid)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int>(CloseByPid), pid);
                return;
            }

            _onCloseAccount(pid);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CloseSelected()
        {
            var index = _list.SelectedIndex;
            if (index < 0 || index >= _entries.Count)
            {
                return;
            }

            var pid = _entries[index].Pid;
            _onCloseAccount(pid);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
