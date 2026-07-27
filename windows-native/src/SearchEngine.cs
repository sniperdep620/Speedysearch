using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Speedysearch.Windows
{
    public sealed class SearchEngine : IDisposable
    {
        private readonly object _rankingSync;
        private readonly object _saveSync;
        private readonly string _indexPath;
        private readonly string _clickstreamPath;
        private readonly AppConfig _config;
        private FeatureExtractor _featureExtractor;
        private LightGbmModel _model;
        private ClickstreamLogger _clickstream;
        private bool _disposed;

        public SearchIndex Index { get; private set; }
        public bool LoadedCachedFiles { get; private set; }
        public bool ModelLoaded
        {
            get
            {
                lock (_rankingSync)
                {
                    return _model != null;
                }
            }
        }
        public event EventHandler IndexChanged;

        public SearchEngine(AppConfig config)
            : this(config, Paths.IndexPath, Paths.ClickstreamPath)
        {
        }

        public SearchEngine(AppConfig config, string indexPath, string clickstreamPath)
        {
            _config = config;
            _indexPath = indexPath;
            _clickstreamPath = clickstreamPath;
            _rankingSync = new object();
            _saveSync = new object();
            Index = new SearchIndex();
            _featureExtractor = new FeatureExtractor(new IndexEntry[0], config);
            if (_config.ModelEnabled)
            {
                try { _model = LightGbmModel.Load(Paths.Expand(_config.ModelPath)); }
                catch { _model = null; }
            }
            if (_config.EnableClickstream)
            {
                try { _clickstream = new ClickstreamLogger(clickstreamPath); }
                catch { _clickstream = null; }
            }
        }

        public void WarmStart()
        {
            ThrowIfDisposed();
            IndexSnapshot snapshot;
            if (IndexStore.TryLoad(_indexPath, out snapshot))
            {
                Index.ReplaceSnapshot(snapshot);
                LoadedCachedFiles = snapshot.Entries.Any(delegate(IndexEntry entry)
                {
                    return entry.EntryType == EntryType.File;
                });
            }
            RebuildCatalog(CancellationToken.None);
            RefreshFeatures();
        }

        public void InitializeFully(CancellationToken cancellationToken)
        {
            WarmStart();
            if (!LoadedCachedFiles)
            {
                ReindexFiles(cancellationToken);
            }
            else
            {
                SaveIndex();
            }
        }

        public void ReindexFiles(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            List<string> roots = _config.ExpandedWatchPaths();
            List<IndexEntry> entries = SearchIndex.DiscoverFiles(
                roots,
                _config.ExcludePatterns,
                cancellationToken).ToList();
            Index.ReplaceFiles(entries);
            LoadedCachedFiles = true;
            RefreshFeatures();
            SaveIndex();
            OnIndexChanged();
        }

        public Task ReindexFilesAsync(CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(
                delegate { ReindexFiles(cancellationToken); },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void RebuildCatalog(CancellationToken cancellationToken)
        {
            List<IndexEntry> catalog = WindowsCatalog.Build(cancellationToken);
            Index.ReplaceCatalog(catalog);
            RefreshFeatures();
            OnIndexChanged();
        }

        public QueryResponse Query(string query, SearchFilter filter)
        {
            ThrowIfDisposed();
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<IndexEntry> candidates = Index.FilterCandidates(
                query,
                filter,
                _config.MaxStage1Candidates);
            QueryContext context = BuildContext();
            FeatureExtractor extractor;
            LightGbmModel model;
            lock (_rankingSync)
            {
                extractor = _featureExtractor;
                model = _model;
            }
            List<SearchResult> ranked = new List<SearchResult>();
            foreach (IndexEntry entry in candidates)
            {
                RankerFeatures features = extractor.Extract(entry, context, query);
                float score = model == null ? FallbackScore(features) : model.Predict(features);
                ranked.Add(new SearchResult
                {
                    Entry = entry,
                    Score = FeatureExtractor.Clamp01(score),
                    Features = features
                });
            }
            ranked.Sort(delegate(SearchResult left, SearchResult right)
            {
                int score = right.Score.CompareTo(left.Score);
                if (score != 0)
                {
                    return score;
                }
                int name = StringComparer.OrdinalIgnoreCase.Compare(left.Entry.Name, right.Entry.Name);
                return name != 0 ? name : left.Entry.Id.CompareTo(right.Entry.Id);
            });
            if (ranked.Count > 10)
            {
                ranked.RemoveRange(10, ranked.Count - 10);
            }
            stopwatch.Stop();
            return new QueryResponse
            {
                Results = ranked,
                LatencyMicroseconds = stopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency,
                IndexStale = UnixTime.NowSeconds() - Index.LastIndexed > 1
            };
        }

        public bool RecordSelection(string query, ulong selectedId, int rankPosition)
        {
            ThrowIfDisposed();
            QueryResponse response = Query(query, SearchFilter.All);
            IndexEntry selected = response.Results
                .Where(delegate(SearchResult result) { return result.Entry.Id == selectedId; })
                .Select(delegate(SearchResult result) { return result.Entry; })
                .FirstOrDefault();
            if (selected == null)
            {
                selected = Index.Find(selectedId);
            }
            if (selected == null)
            {
                return false;
            }
            QueryContext context = BuildContext();
            ClickstreamLogger clickstream;
            lock (_rankingSync)
            {
                clickstream = _clickstream;
            }
            if (clickstream != null)
            {
                try
                {
                    clickstream.Log(
                        query,
                        selectedId,
                        selected.Name,
                        rankPosition,
                        context,
                        response.Results);
                }
                catch { }
            }
            if (!Index.RecordAccess(selectedId, context.Timestamp))
            {
                return false;
            }
            RefreshFeatures();
            SaveIndex();
            return true;
        }

        public void ReloadRankingConfiguration()
        {
            LightGbmModel model = null;
            ClickstreamLogger clickstream = null;
            if (_config.ModelEnabled)
            {
                try { model = LightGbmModel.Load(Paths.Expand(_config.ModelPath)); }
                catch { model = null; }
            }
            if (_config.EnableClickstream)
            {
                try { clickstream = new ClickstreamLogger(_clickstreamPath); }
                catch { clickstream = null; }
            }

            ClickstreamLogger previous;
            lock (_rankingSync)
            {
                previous = _clickstream;
                _model = model;
                _clickstream = clickstream;
            }
            if (previous != null)
            {
                previous.Dispose();
            }
            RefreshFeatures();
        }

        public void ApplyFileChanges(IEnumerable<PathChange> changes)
        {
            List<PathChange> values = changes.ToList();
            if (values.Count == 0)
            {
                return;
            }
            Index.ApplyPathChanges(values, _config.ExpandedWatchPaths());
            RefreshFeatures();
            SaveIndex();
            OnIndexChanged();
        }

        public WatcherService CreateWatcher()
        {
            return new WatcherService(
                _config.ExpandedWatchPaths(),
                WindowsCatalog.ApplicationRoots(),
                _config.ExcludePatterns,
                _config.BatchUpdateIntervalMs,
                ApplyFileChanges,
                delegate
                {
                    RebuildCatalog(CancellationToken.None);
                    SaveIndex();
                },
                delegate
                {
                    ReindexFiles(CancellationToken.None);
                });
        }

        public void SaveIndex()
        {
            lock (_saveSync)
            {
                IndexStore.Save(Index, _indexPath);
            }
        }

        private void RefreshFeatures()
        {
            FeatureExtractor replacement = new FeatureExtractor(Index.Snapshot().Entries, _config);
            lock (_rankingSync)
            {
                _featureExtractor = replacement;
            }
        }

        private static float FallbackScore(RankerFeatures features)
        {
            float matchQuality = features.IsExactPrefix
                ? 1.0f
                : (features.IsLevenshteinMatch
                    ? 0.9f
                    : (features.IsTrigramMatch ? 0.7f : 0.0f));
            return matchQuality * 0.85f + features.Frecency * 0.15f;
        }

        private static QueryContext BuildContext()
        {
            DateTime now = DateTime.Now;
            return new QueryContext
            {
                ActiveApplication = WindowsShell.ActiveWindowTitle(),
                WorkingDirectory = Environment.CurrentDirectory,
                Timestamp = UnixTime.NowSeconds(),
                HourOfDay = now.Hour,
                DayOfWeek = ((int)now.DayOfWeek + 6) % 7
            };
        }

        private void OnIndexChanged()
        {
            EventHandler handler = IndexChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("SearchEngine");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ClickstreamLogger clickstream;
            lock (_rankingSync)
            {
                clickstream = _clickstream;
                _clickstream = null;
            }
            if (clickstream != null)
            {
                clickstream.Dispose();
            }
            Index.Dispose();
        }
    }
}
