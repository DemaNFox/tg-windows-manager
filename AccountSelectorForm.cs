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

            Text = "\u0412\u044b\u0431\u043e\u0440 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u0430";
            Width = 360;
            Height = 280;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = false;

            var instructionLabel = new Label
            {
                Left = 10,
                Top = 10,
                Width = 330,
                Height = 34,
                Text = "Выберите окно из списка или нажмите на нужное окно (цифру)"
            };

            _list = new ListBox
            {
                Left = 10,
                Top = 50,
                Width = 330,
                Height = 150
            };

            foreach (var entry in entries)
            {
                _list.Items.Add(entry.Label);
            }

            _list.DoubleClick += (_, __) => CloseSelected();

            _closeButton = new Button
            {
                Text = "\u0417\u0430\u043a\u0440\u044b\u0442\u044c \u0432\u044b\u0431\u0440\u0430\u043d\u043d\u044b\u0439",
                Left = 140,
                Width = 120,
                Top = 210,
                DialogResult = DialogResult.None
            };
            _closeButton.Click += (_, __) => CloseSelected();

            _cancelButton = new Button
            {
                Text = "\u041e\u0442\u043c\u0435\u043d\u0430",
                Left = 270,
                Width = 70,
                Top = 210,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(instructionLabel);
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

