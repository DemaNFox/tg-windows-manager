using System;
using System.Drawing;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TemplateEditForm : Form
    {
        private readonly TextBox _textInput;
        private readonly TextBox _keyInput;
        private readonly bool _textReadOnly;
        private readonly bool _isDefault;
        private Keys _capturedKey = Keys.None;

        public TemplateSetting? Result { get; private set; }

        public TemplateEditForm()
            : this(null)
        {
        }

        public TemplateEditForm(TemplateSetting? template, bool textReadOnly = false)
        {
            _textReadOnly = textReadOnly;
            _isDefault = template?.IsDefault ?? false;
            Text = "Новый шаблон";
            Width = 420;
            Height = 320;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            var textLabel = new Label
            {
                Text = "Текст шаблона:",
                Left = 10,
                Top = 10,
                Width = 380
            };

            _textInput = new TextBox
            {
                Left = 10,
                Top = 30,
                Width = 380,
                Height = 160,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = _textReadOnly,
                BackColor = _textReadOnly ? Color.WhiteSmoke : Color.White
            };

            var keyLabel = new Label
            {
                Text = "Назначьте кнопку (нажмите клавишу):",
                Left = 10,
                Top = 200,
                Width = 380
            };

            _keyInput = new TextBox
            {
                Left = 10,
                Top = 220,
                Width = 160,
                ReadOnly = true,
                BackColor = Color.White
            };

            _keyInput.KeyDown += KeyInputOnKeyDown;
            _keyInput.GotFocus += (_, __) => _keyInput.SelectAll();
            _keyInput.TabStop = false;

            var saveButton = new Button
            {
                Text = "Сохранить",
                Left = 220,
                Width = 80,
                Top = 250,
                DialogResult = DialogResult.None
            };
            saveButton.Click += SaveButtonOnClick;

            var cancelButton = new Button
            {
                Text = "Отмена",
                Left = 310,
                Width = 80,
                Top = 250,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(textLabel);
            Controls.Add(_textInput);
            Controls.Add(keyLabel);
            Controls.Add(_keyInput);
            Controls.Add(saveButton);
            Controls.Add(cancelButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;

            if (template != null)
            {
                Text = "Изменить шаблон";
                _capturedKey = template.Key;
                _textInput.Text = template.Text;
                if (_capturedKey != Keys.None)
                {
                    _keyInput.Text = _capturedKey.ToString();
                }
            }
        }

        private void KeyInputOnKeyDown(object? sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            _capturedKey = e.KeyCode;
            _keyInput.Text = _capturedKey.ToString();
        }

        private void SaveButtonOnClick(object? sender, EventArgs e)
        {
            string text = _textInput.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("Введите текст шаблона.", "Шаблоны", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_capturedKey == Keys.None)
            {
                MessageBox.Show("Нажмите кнопку, которую хотите использовать для шаблона.", "Шаблоны", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_capturedKey == Keys.Tab)
            {
                MessageBox.Show("Tab используется для вставки шаблона. Выберите другую кнопку.", "Шаблоны", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Result = new TemplateSetting
            {
                Text = text,
                Key = _capturedKey,
                IsDefault = _isDefault
            };

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
