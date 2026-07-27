using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Speedysearch.Windows
{
    public static class WindowsShell
    {
        private const int SW_SHOWNORMAL = 1;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int size);

        public static void InitializeProcess()
        {
            try { SetProcessDPIAware(); }
            catch { }
            try { SetCurrentProcessExplicitAppUserModelID("Speedysearch.Windows.Native"); }
            catch { }
        }

        public static void EnableImmersiveDarkMode(IntPtr window)
        {
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int));
            }
            catch { }
        }

        public static string ActiveWindowTitle()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }
            StringBuilder text = new StringBuilder(512);
            int length = GetWindowText(window, text, text.Capacity);
            return length > 0 ? text.ToString(0, length) : null;
        }

        public static void Open(IndexEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException("entry");
            }
            string target = entry.Path;
            if (String.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("The selected result has no launch target.");
            }
            if (entry.EntryType == EntryType.Command)
            {
                StartCommand(target);
                return;
            }
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process.Start(start);
        }

        public static void ShowInFolder(IndexEntry entry)
        {
            if (entry == null || entry.EntryType != EntryType.File)
            {
                return;
            }
            string arguments = entry.Metadata.IsDirectory
                ? Quote(entry.Path)
                : "/select," + Quote(entry.Path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
        }

        public static void ConfigureStartup(bool enabled)
        {
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled)
                {
                    run.SetValue(
                        "Speedysearch",
                        Quote(Application.ExecutablePath) + " --hidden",
                        RegistryValueKind.String);
                }
                else
                {
                    run.DeleteValue("Speedysearch", false);
                }
            }
        }

        private static void StartCommand(string target)
        {
            string executable = target;
            string arguments = String.Empty;
            int separator = target.IndexOf(' ');
            if (separator > 0)
            {
                executable = target.Substring(0, separator);
                arguments = target.Substring(separator + 1);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    public sealed class GlobalHotkey : IDisposable
    {
        private const uint ModifierAlt = 0x0001;
        private const uint ModifierControl = 0x0002;
        private const uint ModifierShift = 0x0004;
        private const uint ModifierWindows = 0x0008;
        private const uint ModifierNoRepeat = 0x4000;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        private readonly IntPtr _window;
        private readonly int _id;
        private bool _registered;

        public GlobalHotkey(IntPtr window, int id)
        {
            _window = window;
            _id = id;
        }

        public bool Register(string shortcut, out string normalized)
        {
            HotkeyDefinition definition;
            if (!TryParse(shortcut, out definition))
            {
                normalized = null;
                return false;
            }
            Unregister();
            _registered = RegisterHotKey(
                _window,
                _id,
                definition.Modifiers | ModifierNoRepeat,
                (uint)definition.Key);
            normalized = definition.Normalized;
            return _registered;
        }

        public void Unregister()
        {
            if (_registered)
            {
                UnregisterHotKey(_window, _id);
                _registered = false;
            }
        }

        public static bool TryParse(string shortcut, out HotkeyDefinition definition)
        {
            definition = null;
            if (String.IsNullOrWhiteSpace(shortcut))
            {
                return false;
            }
            uint modifiers = 0;
            Keys key = Keys.None;
            bool hasRealModifier = false;
            foreach (string raw in shortcut.Split('+'))
            {
                string part = raw.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierControl;
                    hasRealModifier = true;
                }
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierAlt;
                    hasRealModifier = true;
                }
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierShift;
                }
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("Super", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModifierWindows;
                    hasRealModifier = true;
                }
                else
                {
                    Keys parsed;
                    if (!Enum.TryParse<Keys>(part, true, out parsed)
                        || parsed == Keys.None
                        || IsModifierKey(parsed)
                        || key != Keys.None)
                    {
                        return false;
                    }
                    key = parsed;
                }
            }
            if (!hasRealModifier || key == Keys.None)
            {
                return false;
            }
            definition = new HotkeyDefinition(modifiers, key, Normalize(modifiers, key));
            return true;
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey
                || key == Keys.Menu
                || key == Keys.ShiftKey
                || key == Keys.LWin
                || key == Keys.RWin;
        }

        private static string Normalize(uint modifiers, Keys key)
        {
            StringBuilder value = new StringBuilder();
            if ((modifiers & ModifierControl) != 0) value.Append("Ctrl+");
            if ((modifiers & ModifierAlt) != 0) value.Append("Alt+");
            if ((modifiers & ModifierShift) != 0) value.Append("Shift+");
            if ((modifiers & ModifierWindows) != 0) value.Append("Win+");
            value.Append(key.ToString());
            return value.ToString();
        }

        public void Dispose()
        {
            Unregister();
        }
    }

    public sealed class HotkeyDefinition
    {
        public uint Modifiers { get; private set; }
        public Keys Key { get; private set; }
        public string Normalized { get; private set; }

        internal HotkeyDefinition(uint modifiers, Keys key, string normalized)
        {
            Modifiers = modifiers;
            Key = key;
            Normalized = normalized;
        }
    }
}
