using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TemplateSetting
    {
        public string Text { get; set; } = string.Empty;
        public Keys Key { get; set; } = Keys.None;

        public TemplateSetting Clone()
        {
            return new TemplateSetting
            {
                Text = Text,
                Key = Key
            };
        }

        public override string ToString()
        {
            return $"{Key}: {GetPreview()}";
        }

        private string GetPreview()
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                return "(пусто)";
            }

            var normalized = Text.Replace("\r", " ").Replace("\n", " ");
            return normalized.Length <= 40 ? normalized : normalized.Substring(0, 40) + "...";
        }
    }
}
