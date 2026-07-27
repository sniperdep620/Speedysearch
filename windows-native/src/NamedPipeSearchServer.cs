using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Speedysearch.Windows
{
    public sealed class NamedPipeSearchServer : IDisposable
    {
        private readonly SearchEngine _engine;
        private readonly string _pipeName;
        private readonly JavaScriptSerializer _serializer;
        private Thread _acceptThread;
        private volatile bool _stopping;

        public NamedPipeSearchServer(SearchEngine engine, string pipeName)
        {
            _engine = engine;
            _pipeName = pipeName;
            _serializer = new JavaScriptSerializer();
            _serializer.MaxJsonLength = 1024 * 1024;
        }

        public void Start()
        {
            if (_acceptThread != null)
            {
                throw new InvalidOperationException("The named-pipe server is already running.");
            }
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.Name = "speedysearch-pipe";
            _acceptThread.IsBackground = true;
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipe();
                    pipe.WaitForConnection();
                    if (_stopping)
                    {
                        pipe.Dispose();
                        break;
                    }
                    NamedPipeServerStream client = pipe;
                    pipe = null;
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch (IOException)
                {
                    if (!_stopping)
                    {
                        Thread.Sleep(25);
                    }
                }
                finally
                {
                    if (pipe != null)
                    {
                        pipe.Dispose();
                    }
                }
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(
                user,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                8,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                64 * 1024,
                64 * 1024,
                security);
        }

        private void HandleClient(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                using (StreamReader reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false),
                    false,
                    4096,
                    true))
                using (StreamWriter writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(false),
                    4096,
                    true))
                {
                    writer.AutoFlush = true;
                    string line;
                    while (!_stopping && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 1024 * 1024)
                        {
                            writer.WriteLine(Error("request is too large"));
                            continue;
                        }
                        writer.WriteLine(HandleRequest(line));
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private string HandleRequest(string line)
        {
            try
            {
                Dictionary<string, object> request =
                    _serializer.Deserialize<Dictionary<string, object>>(line);
                if (request == null)
                {
                    return Error("invalid request");
                }
                object actionValue;
                if (request.TryGetValue("action", out actionValue))
                {
                    string action = Convert.ToString(actionValue);
                    if (String.Equals(action, "ping", StringComparison.OrdinalIgnoreCase))
                    {
                        return Success();
                    }
                    object idValue;
                    ulong id;
                    if (!request.TryGetValue("id", out idValue)
                        || !UInt64.TryParse(Convert.ToString(idValue), out id))
                    {
                        return Error("invalid result id");
                    }
                    IndexEntry entry = _engine.Index.Find(id);
                    if (entry == null)
                    {
                        return Error("result is no longer indexed");
                    }
                    if (String.Equals(action, "open", StringComparison.OrdinalIgnoreCase))
                    {
                        WindowsShell.Open(entry);
                        return Success();
                    }
                    if (String.Equals(action, "show_in_folder", StringComparison.OrdinalIgnoreCase))
                    {
                        if (entry.EntryType != EntryType.File)
                        {
                            return Error("only files and folders have a containing folder");
                        }
                        WindowsShell.ShowInFolder(entry);
                        return Success();
                    }
                    return Error("unknown action");
                }
                object selectedValue;
                if (request.TryGetValue("selected_id", out selectedValue))
                {
                    ulong selected;
                    object queryValue;
                    object rankValue;
                    if (!UInt64.TryParse(Convert.ToString(selectedValue), out selected)
                        || !request.TryGetValue("query", out queryValue)
                        || !request.TryGetValue("rank_position", out rankValue))
                    {
                        return Error("invalid click request");
                    }
                    int rank = Convert.ToInt32(rankValue);
                    bool recorded = _engine.RecordSelection(
                        Convert.ToString(queryValue),
                        selected,
                        rank);
                    return _serializer.Serialize(new Dictionary<string, object>
                    {
                        { "ok", recorded }
                    });
                }
                object query;
                if (!request.TryGetValue("query", out query))
                {
                    return Error("query is required");
                }
                QueryResponse response = _engine.Query(Convert.ToString(query), SearchFilter.All);
                List<object> results = new List<object>();
                foreach (SearchResult result in response.Results)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "id", result.Entry.Id.ToString() },
                        { "name", result.Entry.Name },
                        { "path", result.Entry.Path },
                        { "entry_type", result.Entry.EntryType.ToString().ToLowerInvariant() },
                        { "icon_path", result.Entry.IconPath },
                        { "score", result.Score },
                        { "frecency_score", result.Entry.FrecencyScore },
                        { "is_dir", result.Entry.Metadata.IsDirectory },
                        { "category", result.Entry.Metadata.Category }
                    });
                }
                return _serializer.Serialize(new Dictionary<string, object>
                {
                    { "results", results },
                    { "latency_ms", response.LatencyMilliseconds },
                    { "index_stale", response.IndexStale }
                });
            }
            catch (Exception error)
            {
                return Error("invalid request: " + error.Message);
            }
        }

        private string Success()
        {
            return _serializer.Serialize(new Dictionary<string, object>
            {
                { "ok", true }
            });
        }

        private string Error(string message)
        {
            return _serializer.Serialize(new Dictionary<string, object>
            {
                { "error", message }
            });
        }

        public void Dispose()
        {
            _stopping = true;
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out))
                {
                    client.Connect(100);
                }
            }
            catch { }
            if (_acceptThread != null && !_acceptThread.Join(2000))
            {
                _acceptThread.Interrupt();
            }
        }
    }
}
