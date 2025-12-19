using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TemplateSetting
    {
        public string Text { get; set; } = string.Empty;
        public Keys Key { get; set; } = Keys.None;
        public bool IsDefault { get; set; }

        public TemplateSetting Clone()
        {
            return new TemplateSetting
            {
                Text = Text,
                Key = Key,
                IsDefault = IsDefault
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

    internal static class TemplateDefaults
    {
        public const string DefaultText = "\u041f\u0440\u0438\u0432\u0435\u0442, \u0441\u043a\u0438\u043d\u044c \u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 {\u043a\u043e\u043c\u043f\u0430\u043d\u0438\u044f}, \u043d\u0435 \u043c\u043e\u0433\u0443 \u043d\u0430\u0439\u0442\u0438";
    }
}
