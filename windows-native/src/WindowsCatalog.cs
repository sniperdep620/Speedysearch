using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Speedysearch.Windows
{
    public static class WindowsCatalog
    {
        private sealed class CatalogSpec
        {
            public readonly string Name;
            public readonly string Target;
            public readonly string Category;
            public readonly string[] Tags;

            public CatalogSpec(string name, string target, string category, params string[] tags)
            {
                Name = name;
                Target = target;
                Category = category;
                Tags = tags;
            }
        }

        private static readonly CatalogSpec[] Settings =
        {
            new CatalogSpec("Settings", "ms-settings:", "System", "preferences", "control panel", "general settings"),
            new CatalogSpec("System", "ms-settings:about", "System", "about", "device specifications", "computer information"),
            new CatalogSpec("Display", "ms-settings:display", "System", "monitor", "screen", "resolution", "brightness"),
            new CatalogSpec("Sound", "ms-settings:sound", "System", "audio", "speakers", "microphone", "volume"),
            new CatalogSpec("Notifications", "ms-settings:notifications", "System", "alerts", "do not disturb"),
            new CatalogSpec("Power & Battery", "ms-settings:powersleep", "System", "sleep", "battery saver", "energy"),
            new CatalogSpec("Storage", "ms-settings:storagesense", "System", "disk", "storage sense", "cleanup"),
            new CatalogSpec("Clipboard", "ms-settings:clipboard", "System", "copy", "paste", "history"),
            new CatalogSpec("Nearby Sharing", "ms-settings:crossdevice", "System", "share", "devices"),
            new CatalogSpec("Multitasking", "ms-settings:multitasking", "System", "snap windows", "desktops"),
            new CatalogSpec("Bluetooth & Devices", "ms-settings:bluetooth", "Connections", "bluetooth", "paired devices"),
            new CatalogSpec("Printers & Scanners", "ms-settings:printers", "Connections", "printer", "scanner"),
            new CatalogSpec("Mouse", "ms-settings:mousetouchpad", "Input", "mouse settings", "pointer", "scroll"),
            new CatalogSpec("Touchpad", "ms-settings:devices-touchpad", "Input", "trackpad", "gestures"),
            new CatalogSpec("Typing", "ms-settings:typing", "Input", "keyboard", "spelling", "text suggestions"),
            new CatalogSpec("Network & Internet", "ms-settings:network-status", "Connections", "network", "internet", "wifi", "ethernet"),
            new CatalogSpec("Wi-Fi", "ms-settings:network-wifi", "Connections", "wireless", "network"),
            new CatalogSpec("Ethernet", "ms-settings:network-ethernet", "Connections", "wired network", "lan"),
            new CatalogSpec("VPN", "ms-settings:network-vpn", "Connections", "virtual private network"),
            new CatalogSpec("Airplane Mode", "ms-settings:network-airplanemode", "Connections", "flight mode", "wireless"),
            new CatalogSpec("Mobile Hotspot", "ms-settings:network-mobilehotspot", "Connections", "tethering", "share internet"),
            new CatalogSpec("Personalization", "ms-settings:personalization", "Desktop", "appearance", "theme", "colors"),
            new CatalogSpec("Background", "ms-settings:personalization-background", "Desktop", "wallpaper", "desktop background"),
            new CatalogSpec("Colors", "ms-settings:colors", "Desktop", "accent color", "dark mode", "light mode"),
            new CatalogSpec("Themes", "ms-settings:themes", "Desktop", "theme", "appearance"),
            new CatalogSpec("Lock Screen", "ms-settings:lockscreen", "Desktop", "screen saver", "login background"),
            new CatalogSpec("Taskbar", "ms-settings:taskbar", "Desktop", "system tray", "taskbar settings"),
            new CatalogSpec("Apps", "ms-settings:appsfeatures", "Applications", "installed apps", "programs", "uninstall"),
            new CatalogSpec("Default Apps", "ms-settings:defaultapps", "Applications", "file associations", "browser"),
            new CatalogSpec("Startup Apps", "ms-settings:startupapps", "Applications", "login apps", "autostart"),
            new CatalogSpec("Optional Features", "ms-settings:optionalfeatures", "Applications", "windows features"),
            new CatalogSpec("Accounts", "ms-settings:yourinfo", "Accounts", "user", "profile", "microsoft account"),
            new CatalogSpec("Sign-in Options", "ms-settings:signinoptions", "Accounts", "password", "pin", "windows hello"),
            new CatalogSpec("Family", "ms-settings:family-group", "Accounts", "family safety", "child account"),
            new CatalogSpec("Other Users", "ms-settings:otherusers", "Accounts", "add user", "local account"),
            new CatalogSpec("Windows Backup", "ms-settings:backup", "Accounts", "sync", "backup"),
            new CatalogSpec("Date & Time", "ms-settings:dateandtime", "System", "clock", "time zone", "automatic time"),
            new CatalogSpec("Language & Region", "ms-settings:regionlanguage", "System", "locale", "keyboard language", "country"),
            new CatalogSpec("Gaming", "ms-settings:gaming-gamebar", "Gaming", "game bar", "game mode", "captures"),
            new CatalogSpec("Accessibility", "ms-settings:easeofaccess", "Accessibility", "ease of access", "assistive technology"),
            new CatalogSpec("Text Size", "ms-settings:easeofaccess-display", "Accessibility", "font size", "scaling"),
            new CatalogSpec("Magnifier", "ms-settings:easeofaccess-magnifier", "Accessibility", "zoom", "low vision"),
            new CatalogSpec("Narrator", "ms-settings:easeofaccess-narrator", "Accessibility", "screen reader"),
            new CatalogSpec("Privacy & Security", "ms-settings:privacy", "Privacy", "permissions", "security"),
            new CatalogSpec("Windows Security", "windowsdefender:", "Privacy", "defender", "antivirus", "firewall"),
            new CatalogSpec("Location Privacy", "ms-settings:privacy-location", "Privacy", "gps", "location permissions"),
            new CatalogSpec("Camera Privacy", "ms-settings:privacy-webcam", "Privacy", "webcam", "camera permissions"),
            new CatalogSpec("Microphone Privacy", "ms-settings:privacy-microphone", "Privacy", "microphone permissions"),
            new CatalogSpec("Windows Update", "ms-settings:windowsupdate", "System", "updates", "update history", "restart"),
            new CatalogSpec("Recovery", "ms-settings:recovery", "System", "reset pc", "advanced startup"),
            new CatalogSpec("For Developers", "ms-settings:developers", "System", "developer mode", "device portal")
        };

        private static readonly CatalogSpec[] Commands =
        {
            new CatalogSpec("File Explorer", "explorer.exe", "Windows Tools", "files", "folders", "file manager", "explorer"),
            new CatalogSpec("Windows Terminal", "wt.exe", "Windows Tools", "terminal", "console", "shell", "command line"),
            new CatalogSpec("PowerShell", "powershell.exe", "Windows Tools", "terminal", "shell", "command line"),
            new CatalogSpec("Command Prompt", "cmd.exe", "Windows Tools", "cmd", "console", "dos", "terminal"),
            new CatalogSpec("Task Manager", "taskmgr.exe", "Windows Tools", "process manager", "performance", "startup"),
            new CatalogSpec("Control Panel", "control.exe", "Windows Tools", "classic settings", "system settings"),
            new CatalogSpec("Run", "explorer.exe shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}", "Windows Tools", "run dialog", "execute"),
            new CatalogSpec("Calculator", "calc.exe", "Accessories", "math", "calculate"),
            new CatalogSpec("Notepad", "notepad.exe", "Accessories", "text editor", "notes"),
            new CatalogSpec("Paint", "mspaint.exe", "Accessories", "drawing", "image editor"),
            new CatalogSpec("Snipping Tool", "snippingtool.exe", "Accessories", "screenshot", "screen capture", "snip"),
            new CatalogSpec("Character Map", "charmap.exe", "Accessories", "symbols", "unicode", "special characters"),
            new CatalogSpec("Registry Editor", "regedit.exe", "Administration", "registry", "regedit"),
            new CatalogSpec("Services", "services.msc", "Administration", "windows services", "service manager"),
            new CatalogSpec("Event Viewer", "eventvwr.msc", "Administration", "logs", "windows logs", "events"),
            new CatalogSpec("Device Manager", "devmgmt.msc", "Administration", "hardware", "drivers", "devices"),
            new CatalogSpec("Disk Management", "diskmgmt.msc", "Administration", "partitions", "volumes", "drives"),
            new CatalogSpec("Computer Management", "compmgmt.msc", "Administration", "management console", "admin tools"),
            new CatalogSpec("System Information", "msinfo32.exe", "Administration", "hardware information", "system details"),
            new CatalogSpec("Resource Monitor", "resmon.exe", "Administration", "cpu", "memory", "disk", "network monitor")
        };

        public static IList<string> ApplicationRoots()
        {
            List<string> paths = new List<string>();
            AddIfDirectory(paths, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
            AddIfDirectory(paths, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddIfDirectory(paths, Path.Combine(appData, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned"));
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<IndexEntry> Build(CancellationToken cancellationToken)
        {
            List<IndexEntry> entries = new List<IndexEntry>();
            entries.AddRange(BuildApplications(ApplicationRoots(), cancellationToken));
            entries.AddRange(Settings.Select(delegate(CatalogSpec spec)
            {
                return FromSpec(spec, EntryType.Setting);
            }));
            entries.AddRange(Commands.Select(delegate(CatalogSpec spec)
            {
                return FromSpec(spec, EntryType.Command);
            }));
            return DeduplicateNames(entries);
        }

        public static List<IndexEntry> BuildApplications(
            IEnumerable<string> roots,
            CancellationToken cancellationToken)
        {
            List<IndexEntry> entries = new List<IndexEntry>();
            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }
                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    try
                    {
                        foreach (string child in Directory.EnumerateDirectories(directory))
                        {
                            pending.Push(child);
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }

                    try
                    {
                        foreach (string path in Directory.EnumerateFiles(directory))
                        {
                            string extension = Path.GetExtension(path);
                            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                                && !extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
                                && !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)
                                && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            IndexEntry entry = CreateApplicationEntry(path, root);
                            if (entry != null)
                            {
                                entries.Add(entry);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            return entries;
        }

        public static IndexEntry CreateApplicationEntry(string path, string root)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (String.IsNullOrWhiteSpace(name)
                || name.Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            EntryMetadata metadata = new EntryMetadata
            {
                Category = CategoryFromPath(path, root),
                IsDirectory = false
            };
            string parentName = Path.GetFileName(Path.GetDirectoryName(path));
            AddTag(metadata.Tags, parentName, name);
            foreach (string alias in CommonAliases(name))
            {
                AddTag(metadata.Tags, alias, name);
            }
            IndexEntry entry = new IndexEntry
            {
                Id = TrigramIndex.StableId(EntryType.App, path),
                EntryType = EntryType.App,
                Name = name,
                Path = Paths.Normalize(path),
                IconPath = path,
                Metadata = metadata
            };
            entry.Trigrams = TrigramIndex.SearchTrigrams(entry);
            return entry;
        }

        private static IndexEntry FromSpec(CatalogSpec spec, EntryType type)
        {
            EntryMetadata metadata = new EntryMetadata { Category = spec.Category };
            foreach (string tag in spec.Tags)
            {
                AddTag(metadata.Tags, tag, spec.Name);
            }
            foreach (string alias in CommonAliases(spec.Name))
            {
                AddTag(metadata.Tags, alias, spec.Name);
            }
            IndexEntry entry = new IndexEntry
            {
                Id = TrigramIndex.StableId(type, spec.Target),
                EntryType = type,
                Name = spec.Name,
                Path = spec.Target,
                Metadata = metadata
            };
            entry.Trigrams = TrigramIndex.SearchTrigrams(entry);
            return entry;
        }

        private static IEnumerable<string> CommonAliases(string name)
        {
            string[] words = name.Split(new[] { ' ', '-', '_', '&' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
            {
                yield return String.Concat(words.Select(delegate(string word) { return word.Substring(0, 1); }));
            }
            string lower = name.ToLowerInvariant();
            if (lower.IndexOf("visual studio code", StringComparison.Ordinal) >= 0)
            {
                yield return "vs code";
                yield return "vscode";
                yield return "code editor";
            }
            if (lower.IndexOf("file explorer", StringComparison.Ordinal) >= 0)
            {
                yield return "files";
                yield return "file manager";
            }
            if (lower.IndexOf("system information", StringComparison.Ordinal) >= 0)
            {
                yield return "msinfo";
            }
        }

        private static void AddTag(List<string> tags, string tag, string name)
        {
            if (!String.IsNullOrWhiteSpace(tag)
                && !tag.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }

        private static string CategoryFromPath(string path, string root)
        {
            try
            {
                string relative = path.Substring(root.Length).TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string first = relative.Split(Path.DirectorySeparatorChar)[0];
                return first == Path.GetFileName(path) ? "Applications" : first;
            }
            catch
            {
                return "Applications";
            }
        }

        private static List<IndexEntry> DeduplicateNames(IEnumerable<IndexEntry> values)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<IndexEntry> result = new List<IndexEntry>();
            foreach (IndexEntry entry in values
                .OrderBy(delegate(IndexEntry item) { return (int)item.EntryType; })
                .ThenBy(delegate(IndexEntry item) { return item.Name; }, StringComparer.OrdinalIgnoreCase))
            {
                string key = ((int)entry.EntryType).ToString() + "\0" + entry.Name;
                if (seen.Add(key))
                {
                    result.Add(entry);
                }
            }
            return result;
        }

        private static void AddIfDirectory(List<string> paths, string path)
        {
            if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                paths.Add(Paths.Normalize(path));
            }
        }
    }
}
