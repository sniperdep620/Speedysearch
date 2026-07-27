using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Speedysearch.Windows.Tests
{
    internal static class TestRunner
    {
        private static int _passed;
        private static int _failed;

        [STAThread]
        private static int Main()
        {
            WindowsShell.InitializeProcess();
            Run("trigram generation is stable and supports containment", TestTrigrams);
            Run("bounded Levenshtein handles typos and limits", TestLevenshtein);
            Run("filesystem discovery indexes metadata and honors exclusions", TestFileDiscovery);
            Run("Windows catalog discovers shortcuts, settings, and commands", TestWindowsCatalog);
            Run("exact and typo queries find the intended result", TestSearchQueries);
            Run("filters isolate apps, files, and settings", TestSearchFilters);
            Run("concurrent queries are safe", TestConcurrentQueries);
            Run("frecency promotes a selected result", TestFrecency);
            Run("binary persistence round-trips and rebuilds derived indexes", TestPersistence);
            Run("corrupt and incompatible caches fail closed", TestCorruptCache);
            Run("LightGBM text models parse and evaluate", TestLightGbm);
            Run("ranking features are normalized and context-aware", TestFeatureExtraction);
            Run("clickstream logging writes valid local JSONL", TestClickstream);
            Run("configuration loads, sanitizes, expands, and saves", TestConfiguration);
            Run("global hotkey parsing validates real combinations", TestHotkeyParsing);
            Run("filesystem watcher reports create and remove events", TestWatcher);
            Run("named-pipe daemon handles queries and malformed JSON", TestNamedPipe);
            Run("native WinForms window constructs and owns a Win32 handle", TestNativeWindow);
            Run("100,000-entry full query stays within latency budget", TestLargeIndexPerformance);

            Console.WriteLine();
            Console.WriteLine(
                "Result: {0} passed, {1} failed, {2} total.",
                _passed,
                _failed,
                _passed + _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                test();
                stopwatch.Stop();
                _passed++;
                Console.WriteLine("PASS  {0} ({1:0.0} ms)", name, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception error)
            {
                stopwatch.Stop();
                _failed++;
                Console.WriteLine("FAIL  {0} ({1:0.0} ms)", name, stopwatch.Elapsed.TotalMilliseconds);
                Console.WriteLine("      " + error);
            }
        }

        private static void TestTrigrams()
        {
            AssertSequenceEqual(
                TrigramIndex.Generate("Résumé"),
                TrigramIndex.Generate("résumé"),
                "case normalization changed trigram hashes");
            float overlap = TrigramIndex.OverlapRatio(
                TrigramIndex.Generate("document"),
                TrigramIndex.Generate("important_document"));
            Assert(overlap > 0.7f, "embedded names should have strong trigram overlap");
            AssertEqual(
                TrigramIndex.StableId(EntryType.File, @"C:\Data\Report.txt"),
                TrigramIndex.StableId(EntryType.File, @"c:\data\report.txt"),
                "Windows IDs must be case-insensitive");
        }

        private static void TestLevenshtein()
        {
            AssertEqual(1, Levenshtein.Bounded("document", "dcument", 2).Value, "single deletion");
            AssertEqual(2, Levenshtein.Bounded("résumé", "resume", 2).Value, "Unicode substitutions");
            Assert(!Levenshtein.Bounded("document", "xyz", 2).HasValue, "unrelated term was accepted");
        }

        private static void TestFileDiscovery()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            {
                Directory.CreateDirectory(Path.Combine(directory.Path, "docs"));
                File.WriteAllText(Path.Combine(directory.Path, "docs", "report.txt"), "hello");
                Directory.CreateDirectory(Path.Combine(directory.Path, "node_modules"));
                File.WriteAllText(Path.Combine(directory.Path, "node_modules", "hidden.js"), "x");
                List<IndexEntry> entries = SearchIndex.DiscoverFiles(
                    new[] { directory.Path },
                    new[] { "node_modules" },
                    CancellationToken.None).ToList();
                Assert(entries.Any(delegate(IndexEntry item) { return item.Name == "report.txt"; }), "file missing");
                Assert(entries.Any(delegate(IndexEntry item)
                {
                    return item.Name == "docs" && item.Metadata.IsDirectory;
                }), "folder missing");
                Assert(!entries.Any(delegate(IndexEntry item) { return item.Name == "hidden.js"; }), "excluded file indexed");
                IndexEntry report = entries.First(delegate(IndexEntry item) { return item.Name == "report.txt"; });
                AssertEqual("txt", report.Metadata.FileType, "file extension metadata");
                AssertEqual(5L, report.Metadata.FileSize, "file size metadata");
            }
        }

        private static void TestWindowsCatalog()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            {
                string shortcut = Path.Combine(directory.Path, "Visual Studio Code.lnk");
                File.WriteAllBytes(shortcut, new byte[0]);
                List<IndexEntry> apps = WindowsCatalog.BuildApplications(
                    new[] { directory.Path },
                    CancellationToken.None);
                AssertEqual(1, apps.Count, "shortcut count");
                AssertEqual("Visual Studio Code", apps[0].Name, "shortcut display name");
                Assert(apps[0].Metadata.Tags.Contains("vscode"), "common app alias missing");

                List<IndexEntry> catalog = WindowsCatalog.Build(CancellationToken.None);
                Assert(catalog.Any(delegate(IndexEntry item)
                {
                    return item.EntryType == EntryType.Setting && item.Name == "Windows Update";
                }), "Windows setting missing");
                Assert(catalog.Any(delegate(IndexEntry item)
                {
                    return item.EntryType == EntryType.Command && item.Name == "Task Manager";
                }), "Windows command missing");
            }
        }

        private static void TestSearchQueries()
        {
            using (SearchIndex index = new SearchIndex())
            {
                index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = new List<IndexEntry> { Entry(1, "document", EntryType.File, @"C:\document") },
                    LastIndexed = UnixTime.NowSeconds()
                });
                AssertEqual(1UL, index.FilterCandidates("document", SearchFilter.All, 100)[0].Id, "exact query");
                AssertEqual(1UL, index.FilterCandidates("dcument", SearchFilter.All, 100)[0].Id, "typo query");
                AssertEqual(1UL, index.FilterCandidates("   ", SearchFilter.All, 100)[0].Id, "empty query");
            }
        }

        private static void TestSearchFilters()
        {
            using (SearchIndex index = new SearchIndex())
            {
                IndexEntry app = Entry(1, "Shared Name", EntryType.App, @"C:\app.lnk");
                IndexEntry file = Entry(2, "Shared Name", EntryType.File, @"C:\Shared Name");
                IndexEntry setting = Entry(3, "Shared Name", EntryType.Setting, "ms-settings:");
                IndexEntry command = Entry(4, "Shared Name", EntryType.Command, "control.exe");
                index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = new List<IndexEntry> { app, file, setting, command },
                    LastIndexed = UnixTime.NowSeconds()
                });
                List<IndexEntry> apps = index.FilterCandidates("shared name", SearchFilter.Apps, 100);
                List<IndexEntry> files = index.FilterCandidates("shared name", SearchFilter.Files, 100);
                List<IndexEntry> settings = index.FilterCandidates("shared name", SearchFilter.Settings, 100);
                AssertEqual(1, apps.Count, "app filter count");
                Assert(apps.All(delegate(IndexEntry item) { return item.EntryType == EntryType.App; }), "app filter leaked");
                AssertEqual(1, files.Count, "file filter count");
                Assert(files.All(delegate(IndexEntry item) { return item.EntryType == EntryType.File; }), "file filter leaked");
                AssertEqual(2, settings.Count, "settings filter count");
                Assert(settings.Any(delegate(IndexEntry item) { return item.EntryType == EntryType.Setting; }), "setting result missing");
                Assert(settings.Any(delegate(IndexEntry item) { return item.EntryType == EntryType.Command; }), "command result missing");
            }
        }

        private static void TestConcurrentQueries()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            using (SearchEngine engine = CreateEngine(directory))
            {
                engine.Index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = new List<IndexEntry> { Entry(1, "document", EntryType.File, Path.Combine(directory.Path, "document")) },
                    LastIndexed = UnixTime.NowSeconds()
                });
                Task<QueryResponse>[] tasks = Enumerable.Range(0, 32).Select(delegate(int unused)
                {
                    return Task.Factory.StartNew(delegate { return engine.Query("dcument", SearchFilter.All); });
                }).ToArray();
                Task.WaitAll(tasks);
                Assert(tasks.All(delegate(Task<QueryResponse> task)
                {
                    return task.Result.Results.Count > 0 && task.Result.Results[0].Entry.Id == 1;
                }), "a concurrent query returned the wrong result");
            }
        }

        private static void TestFrecency()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            using (SearchEngine engine = CreateEngine(directory))
            {
                IndexEntry alpha = Entry(1, "alpha document", EntryType.File, Path.Combine(directory.Path, "alpha"));
                IndexEntry beta = Entry(2, "beta document", EntryType.File, Path.Combine(directory.Path, "beta"));
                engine.Index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = new List<IndexEntry> { alpha, beta },
                    LastIndexed = UnixTime.NowSeconds()
                });
                for (int count = 0; count < 5; count++)
                {
                    Assert(engine.RecordSelection("document", 2, 2), "selection was not recorded");
                }
                QueryResponse result = engine.Query("document", SearchFilter.All);
                AssertEqual(2UL, result.Results[0].Entry.Id, "frecency did not promote selected item");
            }
        }

        private static void TestPersistence()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            using (SearchIndex index = new SearchIndex())
            {
                string path = Path.Combine(directory.Path, "index.bin");
                index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = new List<IndexEntry> { Entry(7, "Firefox", EntryType.App, @"C:\Firefox.lnk") },
                    LastIndexed = 10
                });
                IndexStore.Save(index, path);
                IndexSnapshot loaded = IndexStore.Load(path);
                using (SearchIndex restored = new SearchIndex())
                {
                    restored.ReplaceSnapshot(loaded);
                    AssertEqual(
                        7UL,
                        restored.FilterCandidates("firefox", SearchFilter.Apps, 100)[0].Id,
                        "derived index was not rebuilt");
                    AssertEqual(10L, restored.LastIndexed, "index timestamp");
                }
            }
        }

        private static void TestCorruptCache()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            {
                string path = Path.Combine(directory.Path, "bad.bin");
                File.WriteAllText(path, "not an index");
                IndexSnapshot snapshot;
                Assert(!IndexStore.TryLoad(path, out snapshot), "corrupt cache was accepted");
                Assert(snapshot == null, "failed cache load returned a snapshot");
            }
        }

        private static void TestLightGbm()
        {
            string modelText = "tree\nfeature_names="
                + String.Join(" ", LightGbmModel.FeatureNames)
                + "\nTree=0\nnum_leaves=2\nsplit_feature=0\nthreshold=0.5"
                + "\ndecision_type=2\nleft_child=-1\nright_child=-2\nleaf_value=-2 2\n";
            LightGbmModel model = LightGbmModel.Parse(modelText);
            RankerFeatures features = new RankerFeatures();
            Assert(model.Predict(features) < 0.5f, "left leaf prediction");
            features.IsExactPrefix = true;
            Assert(model.Predict(features) > 0.5f, "right leaf prediction");
            Expect<InvalidDataException>(delegate
            {
                LightGbmModel.Parse("feature_names=wrong\nTree=0\nleaf_value=1\n");
            });
        }

        private static void TestFeatureExtraction()
        {
            IndexEntry item = Entry(9, "report.pdf", EntryType.File, @"C:\Work\report.pdf");
            item.AccessCount = 42;
            item.LastAccessed = 1699999000;
            item.Metadata.FileType = "pdf";
            AppConfig config = AppConfig.CreateDefault();
            FeatureExtractor extractor = new FeatureExtractor(new[] { item }, config);
            RankerFeatures features = extractor.Extract(item, new QueryContext
            {
                ActiveApplication = "Microsoft Edge - PDF",
                WorkingDirectory = @"C:\Work",
                Timestamp = 1700000000,
                HourOfDay = 12,
                DayOfWeek = 3
            }, "report");
            foreach (float value in features.AsModelInput())
            {
                Assert(value >= 0.0f && value <= 1.0f, "feature was not normalized: " + value);
            }
            Assert(features.IsExactPrefix, "exact prefix feature");
            Assert(features.ActiveAppMatch, "active app feature");
            Assert(features.SameDirectory, "working directory feature");
        }

        private static void TestClickstream()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            {
                string path = Path.Combine(directory.Path, "clickstream.jsonl");
                using (ClickstreamLogger logger = new ClickstreamLogger(path))
                {
                    logger.Log(
                        "test",
                        123,
                        "Test",
                        1,
                        new QueryContext { Timestamp = 1700000000 },
                        new[] { new SearchResult
                        {
                            Entry = Entry(123, "Test", EntryType.App, @"C:\Test.lnk"),
                            Features = new RankerFeatures(),
                            Score = 1.0f
                        } });
                    AssertEqual(1, logger.ClickCount, "click count");
                }
                string line = File.ReadAllText(path).Trim();
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> value = serializer.Deserialize<Dictionary<string, object>>(line);
                AssertEqual("123", Convert.ToString(value["selected_id"]), "selected id JSON");
                IList candidates = (IList)value["candidates"];
                Dictionary<string, object> candidate = (Dictionary<string, object>)candidates[0];
                Assert(
                    candidate["features"] is Dictionary<string, object>,
                    "clickstream features must remain compatible with train_ranker.py");
            }
        }

        private static void TestConfiguration()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            {
                string configPath = Path.Combine(directory.Path, "config.json");
                AppConfig config = AppConfig.CreateDefault();
                config.WatchPaths = new List<string> { directory.Path, Path.Combine(directory.Path, "child") };
                Directory.CreateDirectory(Path.Combine(directory.Path, "child"));
                config.MaxStage1Candidates = 1;
                config.Opacity = 5.0;
                config.Save(configPath);
                AppConfig loaded = AppConfig.Load(configPath);
                AssertEqual(10, loaded.MaxStage1Candidates, "candidate minimum");
                AssertEqual(1.0, loaded.Opacity, "opacity maximum");
                AssertEqual(1, loaded.ExpandedWatchPaths().Count, "nested watch roots not deduplicated");
            }
        }

        private static void TestHotkeyParsing()
        {
            HotkeyDefinition definition;
            Assert(GlobalHotkey.TryParse("Ctrl + Alt + K", out definition), "valid shortcut rejected");
            AssertEqual("Ctrl+Alt+K", definition.Normalized, "shortcut normalization");
            Assert(!GlobalHotkey.TryParse("K", out definition), "bare key accepted");
            Assert(!GlobalHotkey.TryParse("Shift+K", out definition), "shift-only shortcut accepted");
            Assert(!GlobalHotkey.TryParse("Ctrl+Shift", out definition), "modifier-only shortcut accepted");
        }

        private static void TestWatcher()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            using (ManualResetEvent changed = new ManualResetEvent(false))
            {
                List<PathChange> observed = new List<PathChange>();
                using (WatcherService watcher = new WatcherService(
                    new[] { directory.Path },
                    new string[0],
                    new string[0],
                    80,
                    delegate(IEnumerable<PathChange> changes)
                    {
                        lock (observed)
                        {
                            observed.AddRange(changes);
                        }
                        changed.Set();
                    },
                    delegate { },
                    delegate { }))
                {
                    string file = Path.Combine(directory.Path, "notes.txt");
                    File.WriteAllText(file, "hello");
                    Assert(changed.WaitOne(5000), "create event timed out");
                    changed.Reset();
                    File.Delete(file);
                    Assert(changed.WaitOne(5000), "remove event timed out");
                    Thread.Sleep(150);
                    lock (observed)
                    {
                        Assert(observed.Any(delegate(PathChange item)
                        {
                            return item.Kind == PathChangeKind.Removed
                                && item.Path.Equals(file, StringComparison.OrdinalIgnoreCase);
                        }), "remove event missing");
                    }
                }
            }
        }

        private static void TestNamedPipe()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            using (SearchEngine engine = CreateEngine(directory))
            {
                engine.Index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = new List<IndexEntry>
                    {
                        Entry(1, "document", EntryType.File, Path.Combine(directory.Path, "document"))
                    },
                    LastIndexed = UnixTime.NowSeconds()
                });
                string pipeName = "speedysearch-test-" + Guid.NewGuid().ToString("N");
                using (NamedPipeSearchServer server = new NamedPipeSearchServer(engine, pipeName))
                {
                    server.Start();
                    using (NamedPipeClientStream client = new NamedPipeClientStream(
                        ".",
                        pipeName,
                        PipeDirection.InOut))
                    {
                        client.Connect(5000);
                        using (StreamReader reader = new StreamReader(client, Encoding.UTF8, false, 4096, true))
                        using (StreamWriter writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true))
                        {
                            writer.AutoFlush = true;
                            writer.WriteLine("{\"query\":\"document\"}");
                            string response = reader.ReadLine();
                            Assert(response.IndexOf("\"document\"", StringComparison.Ordinal) >= 0, "pipe result missing");
                            Assert(response.IndexOf("\"entry_type\":\"file\"", StringComparison.Ordinal) >= 0, "entry type missing");
                            Assert(response.IndexOf("\"frecency_score\"", StringComparison.Ordinal) >= 0, "frecency missing");
                            Assert(response.IndexOf("\"is_dir\"", StringComparison.Ordinal) >= 0, "directory metadata missing");
                            writer.WriteLine("{\"action\":\"ping\"}");
                            string ping = reader.ReadLine();
                            Assert(ping.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0, "pipe ping failed");
                            writer.WriteLine("{\"action\":\"open\",\"id\":\"999\"}");
                            string stale = reader.ReadLine();
                            Assert(stale.IndexOf("\"error\"", StringComparison.Ordinal) >= 0, "stale action was accepted");
                            writer.WriteLine("not-json");
                            string error = reader.ReadLine();
                            Assert(error.IndexOf("\"error\"", StringComparison.Ordinal) >= 0, "malformed JSON error missing");
                        }
                    }
                }
            }
        }

        private static void TestNativeWindow()
        {
            using (TemporaryDirectory directory = new TemporaryDirectory())
            {
                AppConfig config = TestConfig(directory);
                config.GlobalHotkey = "Ctrl+Alt+F12";
                string cache = Path.Combine(directory.Path, "window-index.bin");
                using (SearchIndex seed = new SearchIndex())
                {
                    seed.ReplaceSnapshot(new IndexSnapshot
                    {
                        Entries = new List<IndexEntry>
                        {
                            Entry(1, "document", EntryType.File, Path.Combine(directory.Path, "document"))
                        },
                        LastIndexed = UnixTime.NowSeconds()
                    });
                    IndexStore.Save(seed, cache);
                }
                using (SearchEngine engine = new SearchEngine(
                    config,
                    cache,
                    Path.Combine(directory.Path, "click.jsonl")))
                {
                    engine.WarmStart();
                    Application.EnableVisualStyles();
                    using (MainForm form = new MainForm(engine, config, true))
                    {
                        IntPtr handle = form.Handle;
                        Assert(handle != IntPtr.Zero, "native window handle was not created");
                        Assert(form.Controls.Count > 0, "window has no controls");
                        AssertEqual(FormBorderStyle.None, form.FormBorderStyle, "launcher should be frameless");
                        AssertEqual(680, form.ClientSize.Width, "launcher width");
                        AssertEqual(620, form.ClientSize.Height, "launcher height");
                    }
                }
            }
        }

        private static void TestLargeIndexPerformance()
        {
            const int count = 100000;
            List<IndexEntry> entries = new List<IndexEntry>(count);
            for (int index = 0; index < count; index++)
            {
                string name = index == count - 1
                    ? "document"
                    : "sample_file_" + index.ToString("D6") + ".txt";
                entries.Add(Entry((ulong)(index + 1), name, EntryType.File, @"C:\Data\" + name));
            }
            using (TemporaryDirectory directory = new TemporaryDirectory())
            using (SearchEngine engine = CreateEngine(directory))
            {
                engine.Index.ReplaceSnapshot(new IndexSnapshot
                {
                    Entries = entries,
                    LastIndexed = UnixTime.NowSeconds()
                });
                for (int warm = 0; warm < 4; warm++)
                {
                    engine.Query("document", SearchFilter.All);
                }
                List<double> measurements = new List<double>();
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    QueryResponse response = engine.Query("document", SearchFilter.All);
                    AssertEqual("document", response.Results[0].Entry.Name, "large-index result");
                    measurements.Add(response.LatencyMilliseconds);
                }
                measurements.Sort();
                double p95 = measurements[(int)Math.Ceiling(measurements.Count * 0.95) - 1];
                Console.Write("      p50={0:0.###} ms p95={1:0.###} ms; ", measurements[10], p95);
                Assert(p95 < 100.0, "p95 query latency exceeded 100 ms: " + p95);
            }
        }

        private static SearchEngine CreateEngine(TemporaryDirectory directory)
        {
            AppConfig config = TestConfig(directory);
            return new SearchEngine(
                config,
                Path.Combine(directory.Path, "index.bin"),
                Path.Combine(directory.Path, "clickstream.jsonl"));
        }

        private static AppConfig TestConfig(TemporaryDirectory directory)
        {
            AppConfig config = AppConfig.CreateDefault();
            config.WatchPaths = new List<string> { directory.Path };
            config.ExcludePatterns = new List<string>();
            config.ModelEnabled = false;
            config.EnableClickstream = false;
            config.MaxStage1Candidates = 100;
            return config;
        }

        private static IndexEntry Entry(ulong id, string name, EntryType type, string path)
        {
            IndexEntry entry = new IndexEntry
            {
                Id = id,
                Name = name,
                EntryType = type,
                Path = path,
                Metadata = new EntryMetadata()
            };
            entry.Trigrams = TrigramIndex.SearchTrigrams(entry);
            return entry;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new Exception(String.Format(
                    "{0}: expected {1}, got {2}",
                    message,
                    expected,
                    actual));
            }
        }

        private static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
        {
            if (!expected.SequenceEqual(actual))
            {
                throw new Exception(message);
            }
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new Exception("Expected " + typeof(T).Name + " was not thrown.");
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; private set; }

        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "speedysearch-win-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(Path, true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
