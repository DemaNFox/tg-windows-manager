using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TemplateHotkeyManager : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int DefaultVariantCount = 200;
        private const int MaxVariantLength = 120;
        private const string CompanyPlaceholder = "{\u043a\u043e\u043c\u043f\u0430\u043d\u0438\u044f}";

        private readonly Action<string> _log;
        private readonly SynchronizationContext _uiContext;
        private readonly List<TemplateSetting> _templates = new List<TemplateSetting>();
        private readonly object _lock = new object();
        private readonly List<string> _defaultVariants;
        private readonly Random _random = new Random();

        private LowLevelKeyboardProc? _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private TemplateSetting? _pendingTemplate;
        private bool _enabled;
        private bool _awaitingDefaultReplacement;
        private int _lastTemplateLength;
        private string? _lastDefaultVariant;

        public TemplateHotkeyManager(Action<string> log, SynchronizationContext uiContext)
        {
            _log = log;
            _uiContext = uiContext;
            _defaultVariants = BuildDefaultVariants(DefaultVariantCount);
        }

        public void Configure(IEnumerable<TemplateSetting> templates, bool enabled)
        {
            lock (_lock)
            {
                _templates.Clear();
                _templates.AddRange(templates ?? Enumerable.Empty<TemplateSetting>());
                _enabled = enabled;
                EnsureHookState();
            }
        }

        public void SetEnabled(bool enabled)
        {
            lock (_lock)
            {
                _enabled = enabled;
                EnsureHookState();
            }
        }

        public void Dispose()
        {
            ReleaseHook();
        }

        private void EnsureHookState()
        {
            bool shouldHook = _enabled && _templates.Count > 0;
            if (shouldHook && _hookId == IntPtr.Zero)
            {
                _proc ??= HookCallback;
                _hookId = SetHook(_proc);
                _log("Template hotkeys enabled.");
            }
            else if (!shouldHook && _hookId != IntPtr.Zero)
            {
                ReleaseHook();
                _log("Template hotkeys disabled.");
            }
        }

        private void ReleaseHook()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }

            _pendingTemplate = null;
            _awaitingDefaultReplacement = false;
            _lastTemplateLength = 0;
            _lastDefaultVariant = null;
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule? curModule = curProcess.MainModule;
            IntPtr handle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

            var hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, handle, 0);
            if (hook == IntPtr.Zero)
            {
                _log("Не удалось установить глобальный хук клавиатуры для шаблонов.");
            }

            return hook;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;

                bool shouldSend = false;
                TemplateSetting? templateToSend = null;
                bool removeKeyStroke = false;
                bool suppressKey = false;
                bool replaceDefault = false;

                lock (_lock)
                {
                    if (!_enabled || _templates.Count == 0)
                    {
                        _awaitingDefaultReplacement = false;
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    if (_pendingTemplate != null && key == Keys.Tab)
                    {
                        templateToSend = _pendingTemplate;
                        _pendingTemplate = null;
                        shouldSend = true;
                        removeKeyStroke = true;
                        // подавляем Tab, чтобы не было переключения фокуса
                        suppressKey = true;
                    }
                    else if (_awaitingDefaultReplacement && key == Keys.Tab)
                    {
                        replaceDefault = true;
                        suppressKey = true;
                    }
                    else
                    {
                        _awaitingDefaultReplacement = false;

                        var matched = _templates.FirstOrDefault(t => t.Key == key);
                        if (matched != null)
                        {
                            _pendingTemplate = matched;
                            _log($"Шаблон \"{matched}\" подготовлен. Нажмите Tab для вставки.");
                            // не глушим исходную клавишу, чтобы её можно было использовать как обычно;
                            // при вставке шаблона удалим введенный символ.
                        }
                    }
                }

                if (shouldSend && templateToSend != null)
                {
                    SendTemplate(templateToSend, removeKeyStroke);
                    if (suppressKey)
                    {
                        return (IntPtr)1;
                    }
                }
                else if (replaceDefault)
                {
                    ReplaceDefaultTemplate();
                    if (suppressKey)
                    {
                        return (IntPtr)1;
                    }
                }
                else if (suppressKey)
                {
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void SendTemplate(TemplateSetting template, bool removeKeyStroke)
        {
            string textToSend = ResolveTemplateText(template);
            if (string.IsNullOrWhiteSpace(textToSend))
            {
                return;
            }

            try
            {
                _uiContext.Post(_ =>
                {
                    try
                    {
                        if (removeKeyStroke)
                        {
                            SendKeys.SendWait("{BACKSPACE}");
                        }

                        SendKeys.SendWait(EscapeSendKeys(textToSend));
                        TrackLastTemplate(template, textToSend);
                    }
                    catch (Exception ex)
                    {
                        _log("Ошибка отправки шаблона: " + ex.Message);
                    }
                }, null);
            }
            catch (Exception ex)
            {
                _log("Ошибка планирования отправки шаблона: " + ex.Message);
            }
        }

        private string ResolveTemplateText(TemplateSetting template)
        {
            if (template.IsDefault || string.Equals(template.Text, TemplateDefaults.DefaultText, StringComparison.OrdinalIgnoreCase))
            {
                return ChooseDefaultVariant();
            }

            return template.Text;
        }

        private void TrackLastTemplate(TemplateSetting template, string sentText)
        {
            if (template.IsDefault || string.Equals(template.Text, TemplateDefaults.DefaultText, StringComparison.OrdinalIgnoreCase))
            {
                _lastTemplateLength = sentText.Length;
                _awaitingDefaultReplacement = true;
                _lastDefaultVariant = sentText;
            }
            else
            {
                _awaitingDefaultReplacement = false;
                _lastTemplateLength = 0;
                _lastDefaultVariant = null;
            }
        }

        private void ReplaceDefaultTemplate()
        {
            string newVariant = ChooseDefaultVariant(_lastDefaultVariant);
            if (string.IsNullOrWhiteSpace(newVariant))
            {
                return;
            }

            try
            {
                _uiContext.Post(_ =>
                {
                    try
                    {
                        if (_lastTemplateLength > 0)
                        {
                            var backspaces = new StringBuilder(_lastTemplateLength * 4);
                            for (int i = 0; i < _lastTemplateLength; i++)
                            {
                                backspaces.Append("{BACKSPACE}");
                            }

                            SendKeys.SendWait(backspaces.ToString());
                        }

                        SendKeys.SendWait(EscapeSendKeys(newVariant));
                        _lastTemplateLength = newVariant.Length;
                        _lastDefaultVariant = newVariant;
                        _awaitingDefaultReplacement = true;
                    }
                    catch (Exception ex)
                    {
                        _log("Ошибка замены шаблона: " + ex.Message);
                        _awaitingDefaultReplacement = false;
                    }
                }, null);
            }
            catch (Exception ex)
            {
                _log("Ошибка планирования замены шаблона: " + ex.Message);
                _awaitingDefaultReplacement = false;
            }
        }

        private string ChooseDefaultVariant(string? exclude = null)
        {
            if (_defaultVariants.Count == 0)
            {
                return TemplateDefaults.DefaultText;
            }

            string candidate;
            int attempts = 0;
            do
            {
                candidate = _defaultVariants[_random.Next(_defaultVariants.Count)];
                attempts++;
            } while (!string.IsNullOrWhiteSpace(exclude) &&
                     string.Equals(candidate, exclude, StringComparison.Ordinal) &&
                     attempts < 10);

            return candidate;
        }

        private List<string> BuildDefaultVariants(int count = DefaultVariantCount, bool filterNearDuplicates = true)
        {
            var greetings = new[]
            {
                "\u041f\u0440\u0438\u0432\u0435\u0442",
            };

            var verbs = new[]
            {
                "\u0441\u043a\u0438\u043d\u044c",
                "\u043f\u0440\u0438\u0448\u043b\u0438",
                "\u043e\u0442\u043f\u0440\u0430\u0432\u044c",
                "\u043f\u0435\u0440\u0435\u0448\u043b\u0438",
                "\u0437\u0430\u043a\u0438\u043d\u044c",
                "\u043c\u043e\u0436\u0435\u0448\u044c \u0441\u043a\u0438\u043d\u0443\u0442\u044c",
                "\u043c\u043e\u0436\u0435\u0448\u044c \u043f\u0440\u0438\u0441\u043b\u0430\u0442\u044c",
                "\u043f\u0440\u0438\u0448\u043b\u0435\u0448\u044c",
                "\u043e\u0442\u043f\u0440\u0430\u0432\u0438\u0448\u044c",
            };

            var objects = new[]
            {
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u043f\u043e \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u0444\u0438\u0440\u043c\u044b " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u043e\u0440\u0433\u0430\u043d\u0438\u0437\u0430\u0446\u0438\u0438 " + CompanyPlaceholder,
                "\u0434\u0430\u043d\u043d\u044b\u0435 \u043f\u043e \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u0438\u043d\u0444\u043e\u0440\u043c\u0430\u0446\u0438\u044e \u043f\u043e \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u043f\u0440\u043e\u0444\u0438\u043b\u044c \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u043a\u043e\u043d\u0442\u0440\u0430\u0433\u0435\u043d\u0442\u0430 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0443 \u044e\u0440\u043b\u0438\u0446\u0430 " + CompanyPlaceholder,
            };

            var objectsNeedFeminine = new[]
            {
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0430 \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0430 \u043f\u043e \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0430 \u0444\u0438\u0440\u043c\u044b " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0430 \u043e\u0440\u0433\u0430\u043d\u0438\u0437\u0430\u0446\u0438\u0438 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0430 \u043a\u043e\u043d\u0442\u0440\u0430\u0433\u0435\u043d\u0442\u0430 " + CompanyPlaceholder,
                "\u043a\u0430\u0440\u0442\u043e\u0447\u043a\u0430 \u044e\u0440\u043b\u0438\u0446\u0430 " + CompanyPlaceholder,
                "\u0438\u043d\u0444\u043e\u0440\u043c\u0430\u0446\u0438\u044f \u043f\u043e \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
            };

            var objectsNeedPlural = new[]
            {
                "\u0434\u0430\u043d\u043d\u044b\u0435 \u043f\u043e \u043a\u043e\u043c\u043f\u0430\u043d\u0438\u0438 " + CompanyPlaceholder,
            };

            var reasons = new[]
            {
                "\u043d\u0435 \u043c\u043e\u0433\u0443 \u043d\u0430\u0439\u0442\u0438",
                "\u043d\u0435 \u043d\u0430\u0448\u0435\u043b",
                "\u043d\u0435 \u043d\u0430\u0445\u043e\u0436\u0443",
                "\u043d\u0435 \u043f\u043e\u043b\u0443\u0447\u0430\u0435\u0442\u0441\u044f \u043d\u0430\u0439\u0442\u0438",
                "\u043d\u0435 \u0432\u0438\u0436\u0443",
                "\u043f\u043e\u0442\u0435\u0440\u044f\u043b",
                "\u043d\u0435\u0442 \u043f\u043e\u0434 \u0440\u0443\u043a\u043e\u0439",
                "\u043d\u0435 \u043f\u043e\u0434 \u0440\u0443\u043a\u043e\u0439",
                "\u043d\u0435 \u0432\u0438\u0436\u0443 \u0432 \u043f\u0435\u0440\u0435\u043f\u0438\u0441\u043a\u0435",
                "\u043d\u0435 \u043d\u0430\u0448\u0435\u043b \u0443 \u0441\u0435\u0431\u044f",
                "\u043d\u0435 \u043c\u043e\u0433\u0443 \u0431\u044b\u0441\u0442\u0440\u043e \u043d\u0430\u0439\u0442\u0438",
            };

            var reasonsShort = new[]
            {
                "\u043d\u0435 \u043c\u043e\u0433\u0443 \u043d\u0430\u0439\u0442\u0438",
                "\u043d\u0435 \u0432\u0438\u0436\u0443",
                "\u043d\u0435 \u043d\u0430\u0445\u043e\u0436\u0443",
                "\u043d\u0435\u0442 \u043f\u043e\u0434 \u0440\u0443\u043a\u043e\u0439",
                "\u043d\u0435 \u043f\u043e\u0434 \u0440\u0443\u043a\u043e\u0439",
                "\u043d\u0435 \u043c\u043e\u0433\u0443 \u0431\u044b\u0441\u0442\u0440\u043e \u043d\u0430\u0439\u0442\u0438",
            };

            var templates = new List<TemplateSpec>
            {
                new TemplateSpec("{greeting}, {verb} {obj}, {reason}", true, ObjectKind.Accusative, ReasonKind.Full, false),
                new TemplateSpec("{greeting}, {verb} {obj} \u2014 {reason}", true, ObjectKind.Accusative, ReasonKind.Full, false),
                new TemplateSpec("{greeting}, {verb} {obj}. {reason_cap}", true, ObjectKind.Accusative, ReasonKind.Full, true),
                new TemplateSpec("{greeting}, {verb} {obj}? {reason_cap}", true, ObjectKind.Accusative, ReasonKind.Full, true),
                new TemplateSpec("{greeting} {verb} {obj}, {reason}", true, ObjectKind.Accusative, ReasonKind.Full, false),
                new TemplateSpec("{greeting} {verb} {obj} \u2014 {reason}", true, ObjectKind.Accusative, ReasonKind.Full, false),
                new TemplateSpec("{greeting} {verb} {obj}. {reason_cap}", true, ObjectKind.Accusative, ReasonKind.Full, true),
                new TemplateSpec("{greeting}, {obj} {verb}, {reason}", true, ObjectKind.Accusative, ReasonKind.Full, false),
                new TemplateSpec("{greeting}, {obj} {verb}. {reason_cap}", true, ObjectKind.Accusative, ReasonKind.Full, true),
                new TemplateSpec("{greeting}, {obj} {verb}? {reason_cap}", true, ObjectKind.Accusative, ReasonKind.Full, true),
                new TemplateSpec("{greeting}, \u043d\u0443\u0436\u043d\u0430 {obj_need}, {reason}", false, ObjectKind.NeedFeminine, ReasonKind.Full, false),
                new TemplateSpec("{greeting}, \u043d\u0443\u0436\u043d\u0430 {obj_need}. {reason_cap}", false, ObjectKind.NeedFeminine, ReasonKind.Full, true),
                new TemplateSpec("{greeting}, \u043d\u0443\u0436\u043d\u044b {obj_need_plural}, {reason}", false, ObjectKind.NeedPlural, ReasonKind.Full, false),
                new TemplateSpec("{greeting}, {verb} {obj}, {reason}", true, ObjectKind.Accusative, ReasonKind.Short, false),
                new TemplateSpec("{greeting} {verb} {obj}. {reason_cap}", true, ObjectKind.Accusative, ReasonKind.Short, true),
            };

            int theoreticalTotal = 0;
            foreach (var template in templates)
            {
                int verbCount = template.UseVerb ? verbs.Length : 1;
                int objectCount = template.ObjectKind switch
                {
                    ObjectKind.Accusative => objects.Length,
                    ObjectKind.NeedFeminine => objectsNeedFeminine.Length,
                    ObjectKind.NeedPlural => objectsNeedPlural.Length,
                    _ => 0
                };
                int reasonCount = template.ReasonKind == ReasonKind.Short ? reasonsShort.Length : reasons.Length;
                theoreticalTotal += greetings.Length * verbCount * objectCount * reasonCount;
            }

            var variants = new List<string>(Math.Min(count * 3, 10000));
            var seenExact = new HashSet<string>(StringComparer.Ordinal);
            var seenNear = new HashSet<string>(StringComparer.Ordinal);

            foreach (var template in templates)
            {
                var verbSource = template.UseVerb ? verbs : new[] { string.Empty };
                var objectSource = template.ObjectKind switch
                {
                    ObjectKind.Accusative => objects,
                    ObjectKind.NeedFeminine => objectsNeedFeminine,
                    ObjectKind.NeedPlural => objectsNeedPlural,
                    _ => Array.Empty<string>()
                };
                var reasonSource = template.ReasonKind == ReasonKind.Short ? reasonsShort : reasons;

                foreach (var greet in greetings)
                {
                    foreach (var verb in verbSource)
                    {
                        foreach (var obj in objectSource)
                        {
                            foreach (var reason in reasonSource)
                            {
                                string text = ApplyTemplate(template, greet, verb, obj, reason);
                                if (!IsValidVariant(text))
                                {
                                    continue;
                                }

                                if (filterNearDuplicates)
                                {
                                    var nearKey = NormalizeForDedup(text, removePunctuation: true);
                                    if (!seenNear.Add(nearKey))
                                    {
                                        continue;
                                    }
                                }

                                if (!seenExact.Add(text))
                                {
                                    continue;
                                }

                                variants.Add(text);
                            }
                        }
                    }
                }
            }

            if (variants.Count == 0)
            {
                variants.Add(TemplateDefaults.DefaultText);
            }

            Shuffle(variants);

            if (variants.Count > count)
            {
                variants = variants.Take(count).ToList();
            }

            _log($"Шаблоны: теоретически {theoreticalTotal}, с учетом фильтров получаем {variants.Count}.");

            return variants;
        }

                private static string ApplyTemplate(TemplateSpec template, string greeting, string verb, string obj, string reason)
        {
            var reasonText = template.UseReasonCap ? CapitalizeFirst(reason) : reason;

            string text = template.Pattern
                .Replace("{greeting}", greeting)
                .Replace("{verb}", verb)
                .Replace("{obj}", obj)
                .Replace("{obj_need}", obj)
                .Replace("{obj_need_plural}", obj)
                .Replace("{reason}", reasonText)
                .Replace("{reason_cap}", CapitalizeFirst(reason));

            return NormalizeText(text);
        }

        private static string NormalizeText(string text)
        {
            text = Regex.Replace(text, @"\s+", " ").Trim();
            text = Regex.Replace(text, @"\s+([,?.])", "$1");
            text = Regex.Replace(text, "\\s*\u2014\\s*", " \u2014 ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private static string NormalizeForDedup(string text, bool removePunctuation)
        {
            string normalized = text.ToLowerInvariant();
            if (removePunctuation)
            {
                normalized = Regex.Replace(normalized, @"[^\p{L}\p{Nd}\s]", "");
            }

            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static bool IsValidVariant(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (!text.Contains(CompanyPlaceholder, StringComparison.Ordinal))
            {
                return false;
            }

            if (text.Length > MaxVariantLength)
            {
                return false;
            }

            if (text.Contains("..", StringComparison.Ordinal) ||
                text.Contains("!!", StringComparison.Ordinal) ||
                text.Contains("??", StringComparison.Ordinal))
            {
                return false;
            }

            var lower = text.ToLowerInvariant();
            if (lower.Contains("\u0434\u043e\u0431\u0440\u044b\u0439", StringComparison.Ordinal) ||
                lower.Contains("\u0437\u0434\u0440\u0430\u0432\u0441\u0442\u0432\u0443\u0439\u0442\u0435", StringComparison.Ordinal) ||
                lower.Contains("\u043f\u0440\u0438\u0432\u0435\u0442\u0441\u0442\u0432\u0443\u044e", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static string CapitalizeFirst(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (text.Length == 1)
            {
                return text.ToUpperInvariant();
            }

            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        private void Shuffle(IList<string> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }

        private sealed class TemplateSpec
        {
            public TemplateSpec(string pattern, bool useVerb, ObjectKind objectKind, ReasonKind reasonKind, bool useReasonCap)
            {
                Pattern = pattern;
                UseVerb = useVerb;
                ObjectKind = objectKind;
                ReasonKind = reasonKind;
                UseReasonCap = useReasonCap;
            }

            public string Pattern { get; }
            public bool UseVerb { get; }
            public ObjectKind ObjectKind { get; }
            public ReasonKind ReasonKind { get; }
            public bool UseReasonCap { get; }
        }

        private enum ObjectKind
        {
            Accusative,
            NeedFeminine,
            NeedPlural
        }

        private enum ReasonKind
        {
            Full,
            Short
        }
        private static string EscapeSendKeys(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length * 2);
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '{':
                        builder.Append("{{}");
                        break;
                    case '}':
                        builder.Append("{}}");
                        break;
                    case '+':
                        builder.Append("{+}");
                        break;
                    case '^':
                        builder.Append("{^}");
                        break;
                    case '%':
                        builder.Append("{%}");
                        break;
                    case '~':
                        builder.Append("{~}");
                        break;
                    case '(':
                        builder.Append("{(}");
                        break;
                    case ')':
                        builder.Append("{)}");
                        break;
                    default:
                        builder.Append(ch);
                        break;
                }
            }

            return builder.ToString();
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
