using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace Speedysearch.Windows
{
    internal static class Program
    {
        private const string GuiMutexName = @"Local\Speedysearch.Native.Gui";
        private const string GuiShowEventName = @"Local\Speedysearch.Native.Show";
        private const string DaemonMutexName = @"Local\Speedysearch.Native.Daemon";

        [STAThread]
        private static int Main(string[] args)
        {
            WindowsShell.InitializeProcess();
            try
            {
                if (HasArgument(args, "--daemon"))
                {
                    return RunDaemon();
                }
                if (HasArgument(args, "--check-model"))
                {
                    return CheckModel();
                }
                if (HasArgument(args, "--reindex"))
                {
                    return Reindex();
                }
                int queryIndex = ArgumentIndex(args, "--query");
                if (queryIndex >= 0)
                {
                    string query = queryIndex + 1 < args.Length ? args[queryIndex + 1] : String.Empty;
                    return QueryOnce(query);
                }
                return RunGui(HasArgument(args, "--hidden"));
            }
            catch (Exception error)
            {
                if (Environment.UserInteractive && !HasArgument(args, "--daemon"))
                {
                    MessageBox.Show(
                        error.ToString(),
                        "Speedysearch failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else
                {
                    Console.Error.WriteLine(error);
                }
                return 1;
            }
        }

        private static int RunGui(bool initiallyHidden)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, GuiMutexName, out created))
            using (EventWaitHandle showEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                GuiShowEventName))
            {
                if (!created)
                {
                    showEvent.Set();
                    return 0;
                }
                AppConfig config = AppConfig.Load();
                using (SearchEngine engine = new SearchEngine(config))
                {
                    engine.WarmStart();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (MainForm form = new MainForm(engine, config, initiallyHidden))
                    {
                        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                            showEvent,
                            delegate
                            {
                                if (!form.IsDisposed)
                                {
                                    form.ActivateLauncher();
                                }
                            },
                            null,
                            Timeout.Infinite,
                            false);
                        try
                        {
                            Application.Run(form);
                        }
                        finally
                        {
                            registration.Unregister(null);
                        }
                    }
                }
            }
            return 0;
        }

        private static int RunDaemon()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, DaemonMutexName, out created))
            {
                if (!created)
                {
                    Console.Error.WriteLine("Speedysearch daemon is already running.");
                    return 2;
                }
                AppConfig config = AppConfig.Load();
                using (SearchEngine engine = new SearchEngine(config))
                {
                    engine.WarmStart();
                    using (WatcherService watcher = engine.CreateWatcher())
                    using (NamedPipeSearchServer server = new NamedPipeSearchServer(engine, Paths.PipeName))
                    using (ManualResetEvent stopped = new ManualResetEvent(false))
                    {
                        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                        {
                            eventArgs.Cancel = true;
                            stopped.Set();
                        };
                        server.Start();
                        Console.WriteLine(
                            "Speedysearch is listening on \\\\.\\pipe\\"
                            + Paths.PipeName
                            + " with "
                            + engine.Index.Count.ToString("N0")
                            + " indexed items.");
                        if (!engine.LoadedCachedFiles)
                        {
                            engine.ReindexFilesAsync(CancellationToken.None);
                        }
                        stopped.WaitOne();
                    }
                }
            }
            return 0;
        }

        private static int CheckModel()
        {
            AppConfig config = AppConfig.Load();
            if (!config.ModelEnabled)
            {
                Console.WriteLine("Personalized ranking is disabled; frecency fallback is active.");
                return 0;
            }
            string path = Paths.Expand(config.ModelPath);
            LightGbmModel.Load(path);
            Console.WriteLine("Ranker model loaded: " + path);
            return 0;
        }

        private static int Reindex()
        {
            AppConfig config = AppConfig.Load();
            using (SearchEngine engine = new SearchEngine(config))
            {
                engine.WarmStart();
                engine.ReindexFiles(CancellationToken.None);
                Console.WriteLine("Indexed " + engine.Index.Count.ToString("N0") + " items.");
            }
            return 0;
        }

        private static int QueryOnce(string query)
        {
            AppConfig config = AppConfig.Load();
            using (SearchEngine engine = new SearchEngine(config))
            {
                engine.WarmStart();
                QueryResponse response = engine.Query(query, SearchFilter.All);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                List<object> results = response.Results.Select(delegate(SearchResult result)
                {
                    return (object)new Dictionary<string, object>
                    {
                        { "id", result.Entry.Id.ToString() },
                        { "name", result.Entry.Name },
                        { "type", result.Entry.EntryType.ToString().ToLowerInvariant() },
                        { "path", result.Entry.Path },
                        { "score", result.Score }
                    };
                }).ToList();
                Console.WriteLine(serializer.Serialize(new Dictionary<string, object>
                {
                    { "results", results },
                    { "latency_ms", response.LatencyMilliseconds },
                    { "index_stale", response.IndexStale }
                }));
            }
            return 0;
        }

        private static bool HasArgument(string[] args, string value)
        {
            return args.Any(delegate(string argument)
            {
                return argument.Equals(value, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static int ArgumentIndex(string[] args, string value)
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index].Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
