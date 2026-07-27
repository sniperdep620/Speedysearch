using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Speedysearch.Windows
{
    public sealed class WatcherService : IDisposable
    {
        private readonly object _sync;
        private readonly Dictionary<string, PathChange> _pending;
        private readonly List<FileSystemWatcher> _watchers;
        private readonly HashSet<string> _excludes;
        private readonly Action<IEnumerable<PathChange>> _fileCallback;
        private readonly Action _catalogCallback;
        private readonly Action _rescanCallback;
        private readonly Timer _timer;
        private bool _catalogDirty;
        private bool _disposed;

        public WatcherService(
            IEnumerable<string> fileRoots,
            IEnumerable<string> applicationRoots,
            IEnumerable<string> excludePatterns,
            int intervalMilliseconds,
            Action<IEnumerable<PathChange>> fileCallback,
            Action catalogCallback,
            Action rescanCallback)
        {
            _sync = new object();
            _pending = new Dictionary<string, PathChange>(StringComparer.OrdinalIgnoreCase);
            _watchers = new List<FileSystemWatcher>();
            _excludes = new HashSet<string>(
                excludePatterns ?? new string[0],
                StringComparer.OrdinalIgnoreCase);
            _fileCallback = fileCallback;
            _catalogCallback = catalogCallback;
            _rescanCallback = rescanCallback;
            foreach (string root in fileRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddWatcher(root, false);
            }
            foreach (string root in applicationRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddWatcher(root, true);
            }
            _timer = new Timer(
                delegate { Flush(); },
                null,
                intervalMilliseconds,
                intervalMilliseconds);
        }

        private void AddWatcher(string root, bool catalog)
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            FileSystemWatcher watcher = new FileSystemWatcher(root);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter =
                NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size;
            watcher.InternalBufferSize = 64 * 1024;
            watcher.Created += delegate(object sender, FileSystemEventArgs args)
            {
                Queue(args.FullPath, PathChangeKind.Created, catalog);
            };
            watcher.Changed += delegate(object sender, FileSystemEventArgs args)
            {
                Queue(args.FullPath, PathChangeKind.Modified, catalog);
            };
            watcher.Deleted += delegate(object sender, FileSystemEventArgs args)
            {
                Queue(args.FullPath, PathChangeKind.Removed, catalog);
            };
            watcher.Renamed += delegate(object sender, RenamedEventArgs args)
            {
                Queue(args.OldFullPath, PathChangeKind.Removed, catalog);
                Queue(args.FullPath, PathChangeKind.Created, catalog);
            };
            watcher.Error += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { _rescanCallback(); }
                    catch { }
                });
            };
            try
            {
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (IOException)
            {
                watcher.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                watcher.Dispose();
            }
        }

        private void Queue(string path, PathChangeKind kind, bool catalog)
        {
            if (_disposed || IsExcluded(path))
            {
                return;
            }
            lock (_sync)
            {
                if (catalog)
                {
                    _catalogDirty = true;
                }
                else
                {
                    _pending[path] = new PathChange(kind, path);
                }
            }
        }

        private bool IsExcluded(string path)
        {
            try
            {
                return path.Split(new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                }, StringSplitOptions.RemoveEmptyEntries).Any(delegate(string component)
                {
                    return _excludes.Contains(component);
                });
            }
            catch
            {
                return true;
            }
        }

        private void Flush()
        {
            List<PathChange> files;
            bool catalog;
            lock (_sync)
            {
                files = _pending.Values.ToList();
                _pending.Clear();
                catalog = _catalogDirty;
                _catalogDirty = false;
            }
            if (files.Count > 0)
            {
                try { _fileCallback(files); }
                catch { }
            }
            if (catalog)
            {
                try { _catalogCallback(); }
                catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _timer.Dispose();
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.Dispose();
            }
            Flush();
        }
    }
}
