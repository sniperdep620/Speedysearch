using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Speedysearch.Windows
{
    public sealed class FeatureExtractor
    {
        private readonly Dictionary<string, float> _fileTypeStats;
        private readonly AppConfig _config;

        public FeatureExtractor(IEnumerable<IndexEntry> entries, AppConfig config)
        {
            _config = config;
            _fileTypeStats = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            List<IndexEntry> values = entries.ToList();
            long totalFileOpens = values
                .Where(delegate(IndexEntry entry) { return entry.EntryType == EntryType.File; })
                .Sum(delegate(IndexEntry entry) { return (long)entry.AccessCount; });
            if (totalFileOpens > 0)
            {
                foreach (IndexEntry entry in values.Where(delegate(IndexEntry item)
                {
                    return item.EntryType == EntryType.File && item.AccessCount > 0;
                }))
                {
                    string kind = EntryFileType(entry);
                    if (String.IsNullOrEmpty(kind))
                    {
                        continue;
                    }
                    float current;
                    _fileTypeStats.TryGetValue(kind, out current);
                    _fileTypeStats[kind] = current + (float)entry.AccessCount / totalFileOpens;
                }
            }
        }

        public RankerFeatures Extract(IndexEntry entry, QueryContext context, string query)
        {
            string normalizedQuery = (query ?? String.Empty).Trim().ToLowerInvariant();
            List<string> terms = TrigramIndex.SearchTerms(entry)
                .Select(delegate(string term) { return term.ToLowerInvariant(); })
                .ToList();
            long ageSeconds = entry.LastAccessed == 0
                ? 30L * 86400L
                : Math.Max(0, context.Timestamp - entry.LastAccessed);
            float ageHours = ageSeconds / 3600.0f;
            float rawFrecency = entry.AccessCount == 0
                ? Math.Max(0.0f, entry.FrecencyScore)
                : entry.AccessCount * (float)Math.Pow(0.8, ageSeconds / 86400.0);
            int distance = terms.Count == 0
                ? Levenshtein.Full(normalizedQuery, entry.Name.ToLowerInvariant(), 999)
                : terms.Min(delegate(string term)
                {
                    return Levenshtein.Full(normalizedQuery, term, 999);
                });
            int[] queryTrigrams = TrigramIndex.Generate(normalizedQuery);
            string fileType = EntryFileType(entry);
            float typePopularity;
            _fileTypeStats.TryGetValue(fileType ?? String.Empty, out typePopularity);

            return new RankerFeatures
            {
                Frecency = Clamp01(Squash(rawFrecency, 10.0f)),
                RecencyHours = Clamp01(ageHours / 720.0f),
                AccessCount = Clamp01((float)(Math.Log(entry.AccessCount + 1.0) / Math.Log(1001.0))),
                IsExactPrefix = normalizedQuery.Length > 0 && terms.Any(delegate(string term)
                {
                    return term.StartsWith(normalizedQuery, StringComparison.Ordinal);
                }),
                IsTrigramMatch = queryTrigrams.Length > 0
                    && TrigramIndex.OverlapRatio(queryTrigrams, entry.Trigrams) >= 0.6f,
                IsLevenshteinMatch = distance <= 2,
                EditDistance = Clamp01(distance / 999.0f),
                TimeHourBucket = _config.UseTimeOfDay ? Clamp01(context.HourOfDay / 23.0f) : 0.0f,
                DayOfWeek = _config.UseTimeOfDay ? Clamp01(context.DayOfWeek / 6.0f) : 0.0f,
                ActiveAppMatch = _config.UseActiveApp
                    && ActiveApplicationMatches(context.ActiveApplication, fileType, entry),
                SameDirectory = _config.UseWorkingDirectory
                    && SameDirectory(context.WorkingDirectory, entry.Path),
                FileTypePopularity = _config.UseFileTypePopularity ? Clamp01(typePopularity) : 0.0f
            };
        }

        private static float Squash(float value, float midpoint)
        {
            if (Single.IsNaN(value) || Single.IsInfinity(value) || value <= 0.0f)
            {
                return 0.0f;
            }
            return value / (value + midpoint);
        }

        internal static float Clamp01(float value)
        {
            return Math.Max(0.0f, Math.Min(1.0f, value));
        }

        private static string EntryFileType(IndexEntry entry)
        {
            if (entry.Metadata != null && !String.IsNullOrWhiteSpace(entry.Metadata.FileType))
            {
                return entry.Metadata.FileType.TrimStart('.').ToLowerInvariant();
            }
            return Path.GetExtension(entry.Path ?? String.Empty).TrimStart('.').ToLowerInvariant();
        }

        private static bool SameDirectory(string workingDirectory, string itemPath)
        {
            if (String.IsNullOrWhiteSpace(workingDirectory) || String.IsNullOrWhiteSpace(itemPath))
            {
                return false;
            }
            try
            {
                if (String.Equals(
                    Paths.Normalize(workingDirectory),
                    Paths.Normalize(itemPath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string parent = Path.GetDirectoryName(itemPath);
                return !String.IsNullOrEmpty(parent)
                    && String.Equals(
                        Paths.Normalize(workingDirectory),
                        Paths.Normalize(parent),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool ActiveApplicationMatches(string active, string fileType, IndexEntry entry)
        {
            if (String.IsNullOrWhiteSpace(active))
            {
                return false;
            }
            string app = active.ToLowerInvariant();
            if (entry.EntryType == EntryType.App || entry.EntryType == EntryType.Command)
            {
                string name = entry.Name.ToLowerInvariant();
                return app.IndexOf(name, StringComparison.Ordinal) >= 0
                    || name.IndexOf(app, StringComparison.Ordinal) >= 0;
            }
            if (String.IsNullOrEmpty(fileType))
            {
                return false;
            }
            string[][] apps =
            {
                new[] { "code", "vscode", "codium", "visual studio", "idea", "vim", "emacs" },
                new[] { "word", "writer", "wordpad" },
                new[] { "excel", "calc" },
                new[] { "acrobat", "pdf", "edge" },
                new[] { "paint", "photos", "gimp", "inkscape", "krita" }
            };
            string[][] types =
            {
                new[] { "rs", "py", "js", "ts", "tsx", "jsx", "c", "cpp", "h", "go", "java", "toml", "yaml", "yml", "json" },
                new[] { "odt", "doc", "docx", "rtf" },
                new[] { "ods", "xls", "xlsx", "csv" },
                new[] { "pdf", "djvu", "epub" },
                new[] { "png", "jpg", "jpeg", "gif", "svg", "webp", "bmp" }
            };
            for (int group = 0; group < apps.Length; group++)
            {
                if (apps[group].Any(delegate(string candidate)
                    {
                        return app.IndexOf(candidate, StringComparison.Ordinal) >= 0;
                    })
                    && types[group].Contains(fileType, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public sealed class LightGbmModel
    {
        public static readonly string[] FeatureNames =
        {
            "is_exact_prefix",
            "is_trigram_match",
            "is_levenshtein_match",
            "frecency",
            "recency_hours",
            "access_count",
            "edit_distance",
            "time_hour_bucket",
            "day_of_week",
            "active_app_match",
            "same_directory",
            "file_type_popularity"
        };

        private readonly List<Tree> _trees;
        private readonly bool _averageOutput;
        private readonly float _sigmoid;

        private LightGbmModel(List<Tree> trees, bool averageOutput, float sigmoid)
        {
            _trees = trees;
            _averageOutput = averageOutput;
            _sigmoid = sigmoid;
        }

        public static LightGbmModel Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Ranker model was not found.", path);
            }
            return Parse(File.ReadAllText(path));
        }

        public static LightGbmModel Parse(string contents)
        {
            string[] modelFeatureNames = (string[])FeatureNames.Clone();
            bool averageOutput = false;
            float sigmoid = 1.0f;
            foreach (string sourceLine in SplitLines(contents))
            {
                string line = sourceLine.Trim();
                if (line.StartsWith("feature_names=", StringComparison.Ordinal))
                {
                    modelFeatureNames = SplitValues(line.Substring("feature_names=".Length));
                }
                else if (line == "average_output")
                {
                    averageOutput = true;
                }
                else if (line.StartsWith("sigmoid:", StringComparison.Ordinal))
                {
                    Single.TryParse(
                        line.Substring("sigmoid:".Length).Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out sigmoid);
                    if (sigmoid == 0.0f)
                    {
                        sigmoid = 1.0f;
                    }
                }
            }
            if (!modelFeatureNames.SequenceEqual(FeatureNames))
            {
                throw new InvalidDataException("Ranker feature order does not match this build.");
            }

            List<Tree> trees = new List<Tree>();
            string normalized = contents.Replace("\r\n", "\n");
            string[] blocks = normalized.Split(new[] { "\nTree=" }, StringSplitOptions.None);
            for (int index = 1; index < blocks.Length; index++)
            {
                trees.Add(Tree.Parse(blocks[index]));
            }
            if (trees.Count == 0)
            {
                throw new InvalidDataException("Ranker model contains no trees.");
            }
            return new LightGbmModel(trees, averageOutput, sigmoid);
        }

        public float Predict(RankerFeatures features)
        {
            float[] input = features.AsModelInput();
            float raw = _trees.Sum(delegate(Tree tree) { return tree.Predict(input); });
            if (_averageOutput && _trees.Count > 0)
            {
                raw /= _trees.Count;
            }
            return FeatureExtractor.Clamp01(
                (float)(1.0 / (1.0 + Math.Exp(-_sigmoid * raw))));
        }

        private static IEnumerable<string> SplitLines(string contents)
        {
            return contents.Replace("\r\n", "\n").Split('\n');
        }

        private static string[] SplitValues(string value)
        {
            return value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private sealed class Tree
        {
            private int[] _splitFeature;
            private float[] _threshold;
            private int[] _leftChild;
            private int[] _rightChild;
            private byte[] _decisionType;
            private float[] _leafValue;

            public static Tree Parse(string block)
            {
                Dictionary<string, string> values = new Dictionary<string, string>();
                foreach (string line in SplitLines(block))
                {
                    int equals = line.IndexOf('=');
                    if (equals > 0)
                    {
                        values[line.Substring(0, equals)] = line.Substring(equals + 1);
                    }
                }
                Tree tree = new Tree();
                tree._splitFeature = ParseInts(Get(values, "split_feature"));
                tree._threshold = ParseFloats(Get(values, "threshold"));
                tree._leftChild = ParseInts(Get(values, "left_child"));
                tree._rightChild = ParseInts(Get(values, "right_child"));
                tree._decisionType = ParseBytes(Get(values, "decision_type"));
                tree._leafValue = ParseFloats(Get(values, "leaf_value"));
                if (tree._leafValue.Length == 0)
                {
                    throw new InvalidDataException("Ranker tree has no leaves.");
                }
                if (tree._splitFeature.Length > 0
                    && (tree._threshold.Length != tree._splitFeature.Length
                        || tree._leftChild.Length != tree._splitFeature.Length
                        || tree._rightChild.Length != tree._splitFeature.Length))
                {
                    throw new InvalidDataException("Ranker tree arrays have inconsistent lengths.");
                }
                return tree;
            }

            public float Predict(float[] input)
            {
                if (_splitFeature.Length == 0)
                {
                    return _leafValue[0];
                }
                int node = 0;
                while (node >= 0)
                {
                    if (node >= _splitFeature.Length)
                    {
                        return 0.0f;
                    }
                    int feature = _splitFeature[node];
                    float value = feature >= 0 && feature < input.Length
                        ? input[feature]
                        : Single.NaN;
                    byte decision = node < _decisionType.Length ? _decisionType[node] : (byte)0;
                    bool goLeft;
                    if (Single.IsNaN(value))
                    {
                        goLeft = (decision & 2) != 0;
                    }
                    else if ((decision & 1) != 0)
                    {
                        goLeft = Math.Abs(value - _threshold[node]) < Single.Epsilon;
                    }
                    else
                    {
                        goLeft = value <= _threshold[node];
                    }
                    node = goLeft ? _leftChild[node] : _rightChild[node];
                }
                int leaf = -node - 1;
                return leaf >= 0 && leaf < _leafValue.Length ? _leafValue[leaf] : 0.0f;
            }

            private static string Get(Dictionary<string, string> values, string key)
            {
                string value;
                return values.TryGetValue(key, out value) ? value : String.Empty;
            }

            private static int[] ParseInts(string value)
            {
                return SplitValues(value)
                    .Select(delegate(string item)
                    {
                        return Int32.Parse(item, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    }).ToArray();
            }

            private static byte[] ParseBytes(string value)
            {
                return SplitValues(value)
                    .Select(delegate(string item)
                    {
                        return Byte.Parse(item, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    }).ToArray();
            }

            private static float[] ParseFloats(string value)
            {
                return SplitValues(value)
                    .Select(delegate(string item)
                    {
                        return Single.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture);
                    }).ToArray();
            }
        }
    }
}
