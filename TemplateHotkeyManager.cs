using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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

        private LowLevelKeyboardProc? _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private string? _pendingTemplate;
        private bool _enabled;

        public TemplateHotkeyManager(Action<string> log, SynchronizationContext uiContext)
        {
            _log = log;
            _uiContext = uiContext;
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
                string? templateToSend = null;
                bool removeKeyStroke = false;
                bool suppressKey = false;

                lock (_lock)
                {
                    if (!_enabled || _templates.Count == 0)
                    {
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
                    else
                    {
                        var matched = _templates.FirstOrDefault(t => t.Key == key);
                        if (matched != null)
                        {
                            _pendingTemplate = matched.Text;
                            _log($"РЁР°Р±Р»РѕРЅ \"{matched}\" РїРѕРґРіРѕС‚РѕРІР»РµРЅ. РќР°Р¶РјРёС‚Рµ Tab РґР»СЏ РІСЃС‚Р°РІРєРё.");
                            // РЅРµ РіР»СѓС€РёРј РёСЃС…РѕРґРЅСѓСЋ РєР»Р°РІРёС€Сѓ, С‡С‚РѕР±С‹ РµС‘ РјРѕР¶РЅРѕ Р±С‹Р»Рѕ РёСЃРїРѕР»СЊР·РѕРІР°С‚СЊ РєР°Рє РѕР±С‹С‡РЅРѕ;
                            // РїСЂРё РІСЃС‚Р°РІРєРµ С€Р°Р±Р»РѕРЅР° СѓРґР°Р»РёРј РІРІРµРґРµРЅРЅС‹Р№ СЃРёРјРІРѕР».
                        }
                    }
                }

                if (shouldSend && !string.IsNullOrEmpty(templateToSend))
                {
                    SendTemplate(templateToSend, removeKeyStroke);
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

        private void SendTemplate(string text, bool removeKeyStroke)
        {
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

                        SendKeys.SendWait(EscapeSendKeys(text));
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
