using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TemplateHotkeyManager : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

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
            _defaultVariants = BuildDefaultVariants();
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
                _log("РќРµ СѓРґР°Р»РѕСЃСЊ СѓСЃС‚Р°РЅРѕРІРёС‚СЊ РіР»РѕР±Р°Р»СЊРЅС‹Р№ С…СѓРє РєР»Р°РІРёР°С‚СѓСЂС‹ РґР»СЏ С€Р°Р±Р»РѕРЅРѕРІ.");
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
                        // РїРѕРґР°РІР»СЏРµРј Tab, С‡С‚РѕР±С‹ РЅРµ Р±С‹Р»Рѕ РїРµСЂРµРєР»СЋС‡РµРЅРёСЏ С„РѕРєСѓСЃР°
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
                            _log($"РЁР°Р±Р»РѕРЅ \"{matched}\" РїРѕРґРіРѕС‚РѕРІР»РµРЅ. РќР°Р¶РјРёС‚Рµ Tab РґР»СЏ РІСЃС‚Р°РІРєРё.");
                            // РЅРµ РіР»СѓС€РёРј РёСЃС…РѕРґРЅСѓСЋ РєР»Р°РІРёС€Сѓ, С‡С‚РѕР±С‹ РµС‘ РјРѕР¶РЅРѕ Р±С‹Р»Рѕ РёСЃРїРѕР»СЊР·РѕРІР°С‚СЊ РєР°Рє РѕР±С‹С‡РЅРѕ;
                            // РїСЂРё РІСЃС‚Р°РІРєРµ С€Р°Р±Р»РѕРЅР° СѓРґР°Р»РёРј РІРІРµРґРµРЅРЅС‹Р№ СЃРёРјРІРѕР».
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
                        _log("РћС€РёР±РєР° РѕС‚РїСЂР°РІРєРё С€Р°Р±Р»РѕРЅР°: " + ex.Message);
                    }
                }, null);
            }
            catch (Exception ex)
            {
                _log("РћС€РёР±РєР° РїР»Р°РЅРёСЂРѕРІР°РЅРёСЏ РѕС‚РїСЂР°РІРєРё С€Р°Р±Р»РѕРЅР°: " + ex.Message);
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
                        _log("РћС€РёР±РєР° Р·Р°РјРµРЅС‹ С€Р°Р±Р»РѕРЅР°: " + ex.Message);
                        _awaitingDefaultReplacement = false;
                    }
                }, null);
            }
            catch (Exception ex)
            {
                _log("РћС€РёР±РєР° РїР»Р°РЅРёСЂРѕРІР°РЅРёСЏ Р·Р°РјРµРЅС‹ С€Р°Р±Р»РѕРЅР°: " + ex.Message);
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

        private static List<string> BuildDefaultVariants()
        {
            var greetings = new[]
            {
                "Привет",
                "Привет!",
                "Привет,",
                "Приветик",
                "Добрый день",
                "Добрый!",
                "Хей",
                "Йо",
                "Доброе утро",
                "Добрый вечер",
                "Приветствую",
                "Хай",
                "Доброго дня",
                "Доброго",
                "Привет, пожалуйста",
                "Привет, напомни",
                "Приветики",
                "Хэй",
                "Приветствую,",
                "Привет-привет"
            };

            var requests = new[]
            {
                "скинь пожалуйста карточку компании",
                "кинь, пожалуйста, карточку компании",
                "передай карточку компании",
                "поделись карточкой компании",
                "можешь отправить карточку компании",
                "сбрось карточку компании",
                "скинь карточку компании",
                "сможешь прислать карточку компании",
                "дай, пожалуйста, карточку компании",
                "отправь карточку компании",
                "подкинь карточку компании",
                "скинь карточку по компании",
                "пришли карточку компании",
                "подели карточку компании",
                "ссылку на карточку компании пришли",
                "скинь файл карточки компании",
                "кинь карточку компании сюда",
                "поделись карточкой по компании",
                "отправь, пожалуйста, карточку компании",
                "сможешь кинуть карточку компании"
            };

            var placeholders = new[]
            {
                "(компания)",
                "по (компания)",
                "для (компания)",
                "о (компания)",
                "про (компания)"
            };

            var endings = new[]
            {
                "не могу найти",
                "не нашел",
                "не удалось найти",
                "что-то не вижу её",
                "у себя не нахожу",
                "пропала у меня",
                "не попадается под руку",
                "потерял её",
                "в переписке не вижу",
                "куда-то делась",
                "в поиске не появляется",
                "никак не найду",
                "затерял её",
                "не вижу у себя",
                "поиск не находит",
                "в папке не нашел",
                "не получается найти",
                "не находится",
                "не вижу в чатах",
                "не получается обнаружить"
            };

            var variants = new List<string>(220)
            {
                TemplateDefaults.DefaultText
            };
            foreach (var greet in greetings)
            {
                foreach (var req in requests)
                {
                    foreach (var placeholder in placeholders)
                    {
                        foreach (var ending in endings)
                        {
                            variants.Add($"{greet} {req} {placeholder}, {ending}");
                            if (variants.Count >= 200)
                            {
                                return variants;
                            }
                        }
                    }
                }
            }

            if (variants.Count == 0)
            {
                variants.Add(TemplateDefaults.DefaultText);
            }

            return variants;
        }

        private static string EscapeSendKeys(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("{", "{{}")
                .Replace("}", "{}}")
                .Replace("+", "{+}")
                .Replace("^", "{^}")
                .Replace("%", "{%}")
                .Replace("~", "{~}")
                .Replace("(", "{(}")
                .Replace(")", "{)}");
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
