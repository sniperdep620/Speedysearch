using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Speedysearch.Windows
{
    public sealed class AppConfig
    {
        public List<string> WatchPaths { get; set; }
        public List<string> ExcludePatterns { get; set; }
        public int MaxStage1Candidates { get; set; }
        public int BatchUpdateIntervalMs { get; set; }
        public bool ModelEnabled { get; set; }
        public string ModelPath { get; set; }
        public bool EnableClickstream { get; set; }
        public bool UseTimeOfDay { get; set; }
        public bool UseActiveApp { get; set; }
        public bool UseWorkingDirectory { get; set; }
        public bool UseFileTypePopularity { get; set; }
        public string GlobalHotkey { get; set; }
        public double Opacity { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool RunAtStartup { get; set; }

        public AppConfig()
        {
            WatchPaths = new List<string>();
            ExcludePatterns = new List<string>();
        }

        public static AppConfig CreateDefault()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AppConfig config = new AppConfig();
            config.WatchPaths.Add(profile);
            config.ExcludePatterns.AddRange(new[]
            {
                ".git", ".cache", "__pycache__", "node_modules", "target",
                "bin", "obj", "AppData", "$Recycle.Bin", "System Volume Information"
            });
            config.MaxStage1Candidates = 100;
            config.BatchUpdateIntervalMs = 350;
            config.ModelEnabled = true;
            config.ModelPath = Path.Combine(Paths.DataDirectory, "ranker.txt");
            config.EnableClickstream = true;
            config.UseTimeOfDay = true;
            config.UseActiveApp = true;
            config.UseWorkingDirectory = true;
            config.UseFileTypePopularity = true;
            config.GlobalHotkey = "Ctrl+Space";
            config.Opacity = 0.97;
            config.AlwaysOnTop = true;
            config.RunAtStartup = false;
            return config;
        }

        public static AppConfig Load()
        {
            return Load(Paths.ConfigPath);
        }

        public static AppConfig Load(string path)
        {
            AppConfig defaults = CreateDefault();
            if (!File.Exists(path))
            {
                return defaults;
            }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                AppConfig loaded = serializer.Deserialize<AppConfig>(File.ReadAllText(path));
                if (loaded == null)
                {
                    return defaults;
                }
                loaded.Sanitize(defaults);
                return loaded;
            }
            catch
            {
                return defaults;
            }
        }

        public void Save()
        {
            Save(Paths.ConfigPath);
        }

        public void Save(string path)
        {
            AppConfig defaults = CreateDefault();
            Sanitize(defaults);
            string parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            File.WriteAllText(path, serializer.Serialize(this));
        }

        public List<string> ExpandedWatchPaths()
        {
            List<string> paths = new List<string>();
            foreach (string raw in WatchPaths)
            {
                string expanded = Paths.Expand(raw);
                if (Directory.Exists(expanded))
                {
                    paths.Add(Paths.Normalize(expanded));
                }
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return paths.Where(delegate(string candidate)
            {
                return !paths.Any(delegate(string other)
                {
                    return !String.Equals(other, candidate, StringComparison.OrdinalIgnoreCase)
                        && Paths.IsWithin(candidate, other);
                });
            }).ToList();
        }

        private void Sanitize(AppConfig defaults)
        {
            if (WatchPaths == null || WatchPaths.Count == 0)
            {
                WatchPaths = defaults.WatchPaths;
            }
            if (ExcludePatterns == null)
            {
                ExcludePatterns = defaults.ExcludePatterns;
            }
            if (MaxStage1Candidates <= 0)
            {
                MaxStage1Candidates = defaults.MaxStage1Candidates;
            }
            if (BatchUpdateIntervalMs <= 0)
            {
                BatchUpdateIntervalMs = defaults.BatchUpdateIntervalMs;
            }
            MaxStage1Candidates = Math.Max(10, Math.Min(1000, MaxStage1Candidates));
            BatchUpdateIntervalMs = Math.Max(50, Math.Min(5000, BatchUpdateIntervalMs));
            if (String.IsNullOrWhiteSpace(ModelPath))
            {
                ModelPath = defaults.ModelPath;
            }
            if (String.IsNullOrWhiteSpace(GlobalHotkey))
            {
                GlobalHotkey = defaults.GlobalHotkey;
            }
            if (Opacity <= 0.0)
            {
                Opacity = defaults.Opacity;
            }
            Opacity = Math.Max(0.55, Math.Min(1.0, Opacity));
        }
    }

    public static class Paths
    {
        public static readonly string RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Speedysearch");
        public static readonly string CacheDirectory = Path.Combine(RootDirectory, "Cache");
        public static readonly string DataDirectory = Path.Combine(RootDirectory, "Data");
        public static readonly string ConfigPath = Path.Combine(RootDirectory, "config.json");
        public static readonly string IndexPath = Path.Combine(CacheDirectory, "index-win-v1.bin");
        public static readonly string ClickstreamPath = Path.Combine(DataDirectory, "clickstream.jsonl");
        public const string PipeName = "speedysearch";

        public static string Expand(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (raw == "~")
            {
                return profile;
            }
            if (raw.StartsWith("~/", StringComparison.Ordinal)
                || raw.StartsWith("~\\", StringComparison.Ordinal))
            {
                return Path.Combine(profile, raw.Substring(2));
            }
            return Environment.ExpandEnvironmentVariables(raw);
        }

        public static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static bool IsWithin(string candidate, string root)
        {
            string normalizedCandidate = Normalize(candidate);
            string normalizedRoot = Normalize(root);
            if (String.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
