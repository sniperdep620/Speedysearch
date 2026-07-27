using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Speedysearch.Windows
{
    public sealed class SearchIndex : IDisposable
    {
        private readonly ReaderWriterLockSlim _lock;
        private List<IndexEntry> _entries;
        private Dictionary<ulong, int> _positions;
        private TrigramIndex _apps;
        private TrigramIndex _files;
        private TrigramIndex _settings;
        private long _lastIndexed;

        public SearchIndex()
        {
            _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _entries = new List<IndexEntry>();
            _positions = new Dictionary<ulong, int>();
            _apps = new TrigramIndex();
            _files = new TrigramIndex();
            _settings = new TrigramIndex();
        }

        public int Count
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _entries.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        public long LastIndexed
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    return _lastIndexed;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        public IndexSnapshot Snapshot()
        {
            _lock.EnterReadLock();
            try
            {
                return new IndexSnapshot
                {
                    Entries = _entries.Select(delegate(IndexEntry item) { return item.Clone(); }).ToList(),
                    LastIndexed = _lastIndexed
                };
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void ReplaceSnapshot(IndexSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            _lock.EnterWriteLock();
            try
            {
                _entries = snapshot.Entries ?? new List<IndexEntry>();
                _lastIndexed = snapshot.LastIndexed;
                RebuildDerivedLocked();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void ReplaceFiles(IEnumerable<IndexEntry> files)
        {
            _lock.EnterWriteLock();
            try
            {
                Dictionary<ulong, IndexEntry> previous = _entries.ToDictionary(
                    delegate(IndexEntry entry) { return entry.Id; },
                    delegate(IndexEntry entry) { return entry; });
                List<IndexEntry> replacement = _entries
                    .Where(delegate(IndexEntry entry) { return entry.EntryType != EntryType.File; })
                    .ToList();
                foreach (IndexEntry entry in files)
                {
                    CarryUsage(previous, entry);
                    replacement.Add(entry);
                }
                _entries = Deduplicate(replacement);
                _lastIndexed = UnixTime.NowSeconds();
                RebuildDerivedLocked();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void ReplaceCatalog(IEnumerable<IndexEntry> catalog)
        {
            _lock.EnterWriteLock();
            try
            {
                Dictionary<ulong, IndexEntry> previous = _entries.ToDictionary(
                    delegate(IndexEntry entry) { return entry.Id; },
                    delegate(IndexEntry entry) { return entry; });
                List<IndexEntry> replacement = _entries.Where(delegate(IndexEntry entry)
                {
                    return entry.EntryType == EntryType.File;
                }).ToList();
                foreach (IndexEntry entry in catalog)
                {
                    CarryUsage(previous, entry);
                    replacement.Add(entry);
                }
                _entries = Deduplicate(replacement);
                _lastIndexed = UnixTime.NowSeconds();
                RebuildDerivedLocked();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void ApplyPathChanges(IEnumerable<PathChange> changes, IList<string> fileRoots)
        {
            _lock.EnterWriteLock();
            try
            {
                foreach (PathChange change in changes)
                {
                    string changedPath = change.Path;
                    _entries.RemoveAll(delegate(IndexEntry entry)
                    {
                        if (String.Equals(entry.Path, changedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        if (entry.EntryType == EntryType.File && IsDescendant(entry.Path, changedPath))
                        {
                            return true;
                        }
                        return false;
                    });

                    if (change.Kind != PathChangeKind.Removed && File.Exists(changedPath))
                    {
                        bool inFileRoot = fileRoots.Any(delegate(string root)
                        {
                            return Paths.IsWithin(changedPath, root);
                        });
                        if (inFileRoot)
                        {
                            IndexEntry entry;
                            if (TryCreateFileEntry(changedPath, out entry))
                            {
                                _entries.Add(entry);
                            }
                        }
                    }
                    else if (change.Kind != PathChangeKind.Removed && Directory.Exists(changedPath))
                    {
                        bool inFileRoot = fileRoots.Any(delegate(string root)
                        {
                            return Paths.IsWithin(changedPath, root);
                        });
                        if (inFileRoot)
                        {
                            foreach (IndexEntry entry in DiscoverFiles(
                                new[] { changedPath },
                                new string[0],
                                CancellationToken.None))
                            {
                                _entries.Add(entry);
                            }
                        }
                    }
                }
                _entries = Deduplicate(_entries);
                _lastIndexed = UnixTime.NowSeconds();
                RebuildDerivedLocked();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public List<IndexEntry> FilterCandidates(
            string query,
            SearchFilter filter,
            int maxCandidates)
        {
            string normalized = (query ?? String.Empty).Trim().ToLowerInvariant();
            _lock.EnterReadLock();
            try
            {
                if (normalized.Length == 0)
                {
                    return _entries
                        .Where(delegate(IndexEntry entry) { return MatchesFilter(entry, filter); })
                        .OrderByDescending(delegate(IndexEntry entry) { return Frecency(entry); })
                        .ThenBy(delegate(IndexEntry entry) { return entry.Name; }, StringComparer.OrdinalIgnoreCase)
                        .Take(10)
                        .Select(delegate(IndexEntry entry) { return entry.Clone(); })
                        .ToList();
                }

                HashSet<ulong> accepted = new HashSet<ulong>();
                Dictionary<ulong, int> fuzzyPool = new Dictionary<ulong, int>();
                QueryIntent intent = ClassifyIntent(normalized);
                if (filter == SearchFilter.Apps)
                {
                    Collect(_apps, normalized, 0.72f, maxCandidates, accepted, fuzzyPool);
                }
                else if (filter == SearchFilter.Files)
                {
                    Collect(_files, normalized, 0.65f, maxCandidates, accepted, fuzzyPool);
                }
                else if (filter == SearchFilter.Settings)
                {
                    Collect(_settings, normalized, 0.72f, maxCandidates, accepted, fuzzyPool);
                }
                else
                {
                    if (intent == QueryIntent.ExactApp)
                    {
                        Collect(_apps, normalized, 0.72f, maxCandidates, accepted, fuzzyPool);
                    }
                    else if (intent == QueryIntent.FileExtension)
                    {
                        string extension = normalized.Substring(normalized.LastIndexOf('.') + 1);
                        Collect(_files, extension, 0.60f, maxCandidates, accepted, fuzzyPool);
                    }
                    else if (intent == QueryIntent.Setting)
                    {
                        Collect(_settings, normalized, 0.68f, maxCandidates, accepted, fuzzyPool);
                        Collect(_apps, normalized, 0.78f, maxCandidates, accepted, fuzzyPool);
                    }
                    else
                    {
                        Collect(_apps, normalized, 0.72f, maxCandidates, accepted, fuzzyPool);
                        Collect(_files, normalized, 0.65f, maxCandidates, accepted, fuzzyPool);
                        Collect(_settings, normalized, 0.72f, maxCandidates, accepted, fuzzyPool);
                    }
                }

                CollectExact(normalized, filter, accepted);
                if (normalized.Length < 15)
                {
                    foreach (KeyValuePair<ulong, int> fuzzy in fuzzyPool
                        .OrderByDescending(delegate(KeyValuePair<ulong, int> item) { return item.Value; })
                        .ThenBy(delegate(KeyValuePair<ulong, int> item) { return item.Key; })
                        .Take(maxCandidates))
                    {
                        int position;
                        if (_positions.TryGetValue(fuzzy.Key, out position)
                            && MatchesFilter(_entries[position], filter)
                            && TrigramIndex.SearchTerms(_entries[position]).Any(delegate(string term)
                            {
                                return Levenshtein.Bounded(normalized, term, 2).HasValue;
                            }))
                        {
                            accepted.Add(fuzzy.Key);
                        }
                    }
                }

                List<IndexEntry> candidates = new List<IndexEntry>();
                foreach (ulong id in accepted)
                {
                    int position;
                    if (_positions.TryGetValue(id, out position)
                        && MatchesFilter(_entries[position], filter))
                    {
                        candidates.Add(_entries[position]);
                    }
                }
                candidates.Sort(delegate(IndexEntry left, IndexEntry right)
                {
                    int quality = MatchQuality(right, normalized).CompareTo(MatchQuality(left, normalized));
                    if (quality != 0)
                    {
                        return quality;
                    }
                    int usage = Frecency(right).CompareTo(Frecency(left));
                    if (usage != 0)
                    {
                        return usage;
                    }
                    int name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                    return name != 0 ? name : left.Id.CompareTo(right.Id);
                });
                return candidates
                    .Take(maxCandidates)
                    .Select(delegate(IndexEntry entry) { return entry.Clone(); })
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public IndexEntry Find(ulong id)
        {
            _lock.EnterReadLock();
            try
            {
                int position;
                return _positions.TryGetValue(id, out position) ? _entries[position].Clone() : null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public bool RecordAccess(ulong id, long timestamp)
        {
            _lock.EnterWriteLock();
            try
            {
                int position;
                if (!_positions.TryGetValue(id, out position))
                {
                    return false;
                }
                IndexEntry entry = _entries[position];
                entry.AccessCount = entry.AccessCount == Int32.MaxValue
                    ? Int32.MaxValue
                    : entry.AccessCount + 1;
                entry.LastAccessed = timestamp;
                entry.FrecencyScore = entry.AccessCount;
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public static IEnumerable<IndexEntry> DiscoverFiles(
            IEnumerable<string> roots,
            IEnumerable<string> excludePatterns,
            CancellationToken cancellationToken)
        {
            HashSet<string> excludes = new HashSet<string>(
                excludePatterns ?? new string[0],
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new Stack<string>();
            foreach (string root in roots)
            {
                if (Directory.Exists(root))
                {
                    pending.Push(Paths.Normalize(root));
                }
            }

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = pending.Pop();
                if (!seen.Add(current) || IsExcluded(current, excludes))
                {
                    continue;
                }
                IndexEntry directoryEntry;
                if (TryCreateFileEntry(current, out directoryEntry))
                {
                    yield return directoryEntry;
                }
                try
                {
                    foreach (string directory in Directory.EnumerateDirectories(current))
                    {
                        if (!IsExcluded(directory, excludes)
                            && !IsReparsePoint(directory))
                        {
                            pending.Push(directory);
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }

                string[] files = new string[0];
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }
                foreach (string file in files)
                {
                    if (IsExcluded(file, excludes))
                    {
                        continue;
                    }
                    IndexEntry fileEntry;
                    if (TryCreateFileEntry(file, out fileEntry))
                    {
                        yield return fileEntry;
                    }
                }
            }
        }

        public static bool TryCreateFileEntry(string path, out IndexEntry entry)
        {
            entry = null;
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                FileInfo file = isDirectory ? null : new FileInfo(path);
                string name = System.IO.Path.GetFileName(path);
                if (String.IsNullOrEmpty(name))
                {
                    name = path;
                }
                EntryMetadata metadata = new EntryMetadata
                {
                    FileType = isDirectory ? null : System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    FileSize = isDirectory ? 0 : file.Length,
                    HasFileSize = !isDirectory,
                    IsDirectory = isDirectory,
                    Category = isDirectory ? "Folder" : "File"
                };
                entry = new IndexEntry
                {
                    Id = TrigramIndex.StableId(EntryType.File, path),
                    EntryType = EntryType.File,
                    Name = name,
                    Path = Paths.Normalize(path),
                    Metadata = metadata
                };
                entry.Trigrams = TrigramIndex.SearchTrigrams(entry);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private void RebuildDerivedLocked()
        {
            _positions = new Dictionary<ulong, int>(_entries.Count);
            _apps = new TrigramIndex();
            _files = new TrigramIndex();
            _settings = new TrigramIndex();
            for (int index = 0; index < _entries.Count; index++)
            {
                IndexEntry entry = _entries[index];
                if (entry.Metadata == null)
                {
                    entry.Metadata = new EntryMetadata();
                }
                if (entry.Trigrams == null || entry.Trigrams.Length == 0)
                {
                    entry.Trigrams = TrigramIndex.SearchTrigrams(entry);
                }
                _positions[entry.Id] = index;
                if (entry.EntryType == EntryType.App)
                {
                    _apps.Insert(entry);
                }
                else if (entry.EntryType == EntryType.File)
                {
                    _files.Insert(entry);
                }
                else
                {
                    _settings.Insert(entry);
                }
            }
        }

        private QueryIntent ClassifyIntent(string query)
        {
            if (_apps != null)
            {
                HashSet<ulong> exact = new HashSet<ulong>();
                _apps.CollectExact(query, exact);
                if (exact.Count > 0)
                {
                    return QueryIntent.ExactApp;
                }
            }
            int dot = query.LastIndexOf('.');
            if (dot >= 0 && dot < query.Length - 1)
            {
                string extension = query.Substring(dot + 1);
                if (_entries.Any(delegate(IndexEntry entry)
                {
                    return entry.EntryType == EntryType.File
                        && String.Equals(entry.Metadata.FileType, extension, StringComparison.OrdinalIgnoreCase);
                }))
                {
                    return QueryIntent.FileExtension;
                }
            }
            string[] settingWords =
            {
                "settings", "setting", "bluetooth", "display", "network", "privacy",
                "update", "sound", "keyboard", "mouse", "power", "account", "apps"
            };
            if (settingWords.Any(delegate(string word)
            {
                return query.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
            }))
            {
                return QueryIntent.Setting;
            }
            return QueryIntent.Mixed;
        }

        private static void Collect(
            TrigramIndex index,
            string query,
            float threshold,
            int limit,
            HashSet<ulong> accepted,
            Dictionary<ulong, int> fuzzyPool)
        {
            foreach (ulong id in index.Search(query, threshold, limit))
            {
                accepted.Add(id);
            }
            index.AddFuzzySeeds(query, fuzzyPool);
        }

        private void CollectExact(string query, SearchFilter filter, HashSet<ulong> accepted)
        {
            if (filter == SearchFilter.All || filter == SearchFilter.Apps)
            {
                _apps.CollectExact(query, accepted);
            }
            if (filter == SearchFilter.All || filter == SearchFilter.Files)
            {
                _files.CollectExact(query, accepted);
            }
            if (filter == SearchFilter.All || filter == SearchFilter.Settings)
            {
                _settings.CollectExact(query, accepted);
            }
        }

        private static int MatchQuality(IndexEntry entry, string query)
        {
            int quality = 0;
            foreach (string term in TrigramIndex.SearchTerms(entry))
            {
                string normalized = term.ToLowerInvariant();
                if (normalized == query)
                {
                    quality = Math.Max(quality, 3);
                }
                else if (normalized.StartsWith(query, StringComparison.Ordinal))
                {
                    quality = Math.Max(quality, 2);
                }
                else if (normalized.IndexOf(query, StringComparison.Ordinal) >= 0)
                {
                    quality = Math.Max(quality, 1);
                }
            }
            return quality;
        }

        private static float Frecency(IndexEntry entry)
        {
            if (entry.AccessCount <= 0)
            {
                return Math.Max(0.0f, entry.FrecencyScore);
            }
            long days = Math.Max(0, UnixTime.NowSeconds() - entry.LastAccessed) / 86400;
            return entry.AccessCount * (float)Math.Pow(0.8, days);
        }

        private static bool MatchesFilter(IndexEntry entry, SearchFilter filter)
        {
            if (filter == SearchFilter.All)
            {
                return true;
            }
            if (filter == SearchFilter.Apps)
            {
                return entry.EntryType == EntryType.App;
            }
            if (filter == SearchFilter.Files)
            {
                return entry.EntryType == EntryType.File;
            }
            return entry.EntryType == EntryType.Setting || entry.EntryType == EntryType.Command;
        }

        private static bool IsExcluded(string path, HashSet<string> patterns)
        {
            string name = System.IO.Path.GetFileName(path);
            return patterns.Contains(name);
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsDescendant(string candidate, string parent)
        {
            try
            {
                return Paths.IsWithin(candidate, parent)
                    && !String.Equals(
                        Paths.Normalize(candidate),
                        Paths.Normalize(parent),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static List<IndexEntry> Deduplicate(IEnumerable<IndexEntry> entries)
        {
            Dictionary<ulong, IndexEntry> byId = new Dictionary<ulong, IndexEntry>();
            foreach (IndexEntry entry in entries)
            {
                byId[entry.Id] = entry;
            }
            return byId.Values.ToList();
        }

        private static void CarryUsage(Dictionary<ulong, IndexEntry> previous, IndexEntry entry)
        {
            IndexEntry old;
            if (previous.TryGetValue(entry.Id, out old))
            {
                entry.AccessCount = old.AccessCount;
                entry.LastAccessed = old.LastAccessed;
                entry.FrecencyScore = old.FrecencyScore;
            }
        }

        public void Dispose()
        {
            _lock.Dispose();
        }

        private enum QueryIntent
        {
            ExactApp,
            FileExtension,
            Setting,
            Mixed
        }
    }

    public enum PathChangeKind
    {
        Created,
        Modified,
        Removed
    }

    public sealed class PathChange
    {
        public PathChangeKind Kind { get; private set; }
        public string Path { get; private set; }

        public PathChange(PathChangeKind kind, string path)
        {
            Kind = kind;
            Path = path;
        }
    }

    public static class Levenshtein
    {
        public static int? Bounded(string first, string second, int maximumDistance)
        {
            string left = (first ?? String.Empty).ToLowerInvariant();
            string right = (second ?? String.Empty).ToLowerInvariant();
            if (Math.Abs(left.Length - right.Length) > maximumDistance)
            {
                return null;
            }
            if (left.Length == 0)
            {
                return right.Length <= maximumDistance ? (int?)right.Length : null;
            }
            if (right.Length == 0)
            {
                return left.Length <= maximumDistance ? (int?)left.Length : null;
            }

            int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
            int[] current = new int[right.Length + 1];
            for (int leftIndex = 0; leftIndex < left.Length; leftIndex++)
            {
                current[0] = leftIndex + 1;
                int rowMinimum = current[0];
                for (int rightIndex = 0; rightIndex < right.Length; rightIndex++)
                {
                    int substitution = previous[rightIndex]
                        + (left[leftIndex] == right[rightIndex] ? 0 : 1);
                    current[rightIndex + 1] = Math.Min(
                        substitution,
                        Math.Min(previous[rightIndex + 1] + 1, current[rightIndex] + 1));
                    rowMinimum = Math.Min(rowMinimum, current[rightIndex + 1]);
                }
                if (rowMinimum > maximumDistance)
                {
                    return null;
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[right.Length] <= maximumDistance
                ? (int?)previous[right.Length]
                : null;
        }

        public static int Full(string first, string second, int cap)
        {
            int? bounded = Bounded(first, second, 2);
            if (bounded.HasValue)
            {
                return bounded.Value;
            }
            string left = first ?? String.Empty;
            string right = second ?? String.Empty;
            int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
            for (int leftIndex = 0; leftIndex < left.Length; leftIndex++)
            {
                int[] current = new int[right.Length + 1];
                current[0] = leftIndex + 1;
                for (int rightIndex = 0; rightIndex < right.Length; rightIndex++)
                {
                    current[rightIndex + 1] = Math.Min(
                        previous[rightIndex] + (left[leftIndex] == right[rightIndex] ? 0 : 1),
                        Math.Min(previous[rightIndex + 1] + 1, current[rightIndex] + 1));
                }
                previous = current;
            }
            return Math.Min(cap, previous[right.Length]);
        }
    }

    public static class UnixTime
    {
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long NowSeconds()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalSeconds;
        }
    }
}
