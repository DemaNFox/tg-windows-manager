using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TemplateListForm : Form
    {
        private readonly List<TemplateSetting> _templates;
        private readonly ListBox _list;
        private readonly Button _addButton;
        private readonly Button _editButton;
        private readonly Button _deleteButton;
        private readonly Button _closeButton;

        public List<TemplateSetting> Templates => _templates.Select(t => t.Clone()).ToList();

        public TemplateListForm(IEnumerable<TemplateSetting> templates)
        {
            _templates = templates?.Select(t => t.Clone()).ToList() ?? new List<TemplateSetting>();

            Text = "Шаблоны";
            Width = 460;
            Height = 360;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            _list = new ListBox
            {
                Left = 10,
                Top = 10,
                Width = 420,
                Height = 250
            };
            RefreshList();

            _addButton = new Button
            {
                Text = "Добавить",
                Left = 10,
                Width = 90,
                Top = 270,
                DialogResult = DialogResult.None
            };
            _addButton.Click += AddButtonOnClick;

            _editButton = new Button
            {
                Text = "Изменить",
                Left = 210,
                Width = 90,
                Top = 270,
                DialogResult = DialogResult.None
            };
            _editButton.Click += EditButtonOnClick;

            _deleteButton = new Button
            {
                Text = "Удалить",
                Left = 110,
                Width = 90,
                Top = 270,
                DialogResult = DialogResult.None
            };
            _deleteButton.Click += DeleteButtonOnClick;

            _closeButton = new Button
            {
                Text = "Закрыть",
                Left = 340,
                Width = 90,
                Top = 270,
                DialogResult = DialogResult.OK
            };

            Controls.Add(_list);
            Controls.Add(_addButton);
            Controls.Add(_editButton);
            Controls.Add(_deleteButton);
            Controls.Add(_closeButton);

            AcceptButton = _closeButton;
            CancelButton = _closeButton;
        }

        private void AddButtonOnClick(object? sender, EventArgs e)
        {
            using var form = new TemplateEditForm();
            if (form.ShowDialog() == DialogResult.OK && form.Result != null)
            {
                // Replace existing template for the same key
                var existingIndex = _templates.FindIndex(t => t.Key == form.Result.Key);
                if (existingIndex >= 0)
                {
                    if (MessageBox.Show(
                            $"Кнопка {form.Result.Key} уже назначена. Заменить шаблон?",
                            "Шаблоны",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        return;
                    }

                    _templates.RemoveAt(existingIndex);
                }

                _templates.Add(form.Result);
                RefreshList();
            }
        }

        private void EditButtonOnClick(object? sender, EventArgs e)
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _templates.Count)
            {
                return;
            }

            var current = _templates[index];
            using var form = new TemplateEditForm(current, current.IsDefault);
            if (form.ShowDialog() == DialogResult.OK && form.Result != null)
            {
                var updated = form.Result;

                var conflict = _templates
                    .Select((t, idx) => (t, idx))
                    .FirstOrDefault(p => p.idx != index && p.t.Key == updated.Key);

                if (conflict.t != null)
                {
                    if (MessageBox.Show(
                            $"Кнопка {updated.Key} уже назначена. Заменить существующий шаблон?",
                            "Шаблоны",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question) != DialogResult.Yes)
                    {
                        return;
                    }

                    _templates.RemoveAt(conflict.idx);
                    if (conflict.idx < index)
                    {
                        index--;
                    }
                }

                _templates[index] = updated;
                RefreshList();
            }
        }

        private void DeleteButtonOnClick(object? sender, EventArgs e)
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _templates.Count)
            {
                return;
            }

            if (_templates[index].IsDefault)
            {
                MessageBox.Show("Базовый шаблон нельзя удалить.", "Шаблоны", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _templates.RemoveAt(index);
            RefreshList();
        }

        private void RefreshList()
        {
            _list.Items.Clear();
            foreach (var template in _templates
                         .OrderByDescending(t => t.IsDefault)
                         .ThenBy(t => t.Key.ToString()))
            {
                _list.Items.Add(template.ToString());
            }
        }
    }
}
