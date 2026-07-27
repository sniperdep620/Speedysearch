using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Speedysearch.Windows
{
    public static class IndexStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SPDYSEARCH-WIN");
        private const int Version = 1;
        private const int MaximumEntries = 10000000;

        public static bool TryLoad(string path, out IndexSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                snapshot = Load(path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        public static IndexSnapshot Load(string path)
        {
            using (FileStream file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan))
            using (BinaryReader reader = new BinaryReader(file, Encoding.UTF8))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (!magic.SequenceEqual(Magic) || reader.ReadInt32() != Version)
                {
                    throw new InvalidDataException("Unsupported Windows index format.");
                }
                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumEntries)
                {
                    throw new InvalidDataException("Windows index entry count is invalid.");
                }
                IndexSnapshot snapshot = new IndexSnapshot();
                snapshot.LastIndexed = reader.ReadInt64();
                snapshot.Entries.Capacity = count;
                for (int index = 0; index < count; index++)
                {
                    snapshot.Entries.Add(ReadEntry(reader));
                }
                return snapshot;
            }
        }

        public static void Save(SearchIndex index, string path)
        {
            Save(index.Snapshot(), path);
        }

        public static void Save(IndexSnapshot snapshot, string path)
        {
            string parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.WriteThrough))
                using (BinaryWriter writer = new BinaryWriter(file, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(snapshot.Entries.Count);
                    writer.Write(snapshot.LastIndexed);
                    foreach (IndexEntry entry in snapshot.Entries)
                    {
                        WriteEntry(writer, entry);
                    }
                    writer.Flush();
                    file.Flush(true);
                }
                if (File.Exists(path))
                {
                    string backup = path + ".bak";
                    try
                    {
                        File.Replace(temporary, path, backup, true);
                        if (File.Exists(backup))
                        {
                            File.Delete(backup);
                        }
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                    catch (IOException)
                    {
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch { }
                }
            }
        }

        private static void WriteEntry(BinaryWriter writer, IndexEntry entry)
        {
            writer.Write(entry.Id);
            writer.Write((byte)entry.EntryType);
            WriteString(writer, entry.Name);
            WriteString(writer, entry.Path);
            WriteNullableString(writer, entry.IconPath);
            writer.Write(entry.FrecencyScore);
            writer.Write(entry.LastAccessed);
            writer.Write(entry.AccessCount);
            EntryMetadata metadata = entry.Metadata ?? new EntryMetadata();
            WriteNullableString(writer, metadata.FileType);
            writer.Write(metadata.FileSize);
            writer.Write(metadata.HasFileSize);
            writer.Write(metadata.IsDirectory);
            WriteNullableString(writer, metadata.Category);
            writer.Write(metadata.Tags.Count);
            foreach (string tag in metadata.Tags)
            {
                WriteString(writer, tag);
            }
        }

        private static IndexEntry ReadEntry(BinaryReader reader)
        {
            IndexEntry entry = new IndexEntry();
            entry.Id = reader.ReadUInt64();
            entry.EntryType = (EntryType)reader.ReadByte();
            if (!Enum.IsDefined(typeof(EntryType), entry.EntryType))
            {
                throw new InvalidDataException("Windows index contains an invalid entry type.");
            }
            entry.Name = ReadString(reader);
            entry.Path = ReadString(reader);
            entry.IconPath = ReadNullableString(reader);
            entry.FrecencyScore = reader.ReadSingle();
            entry.LastAccessed = reader.ReadInt64();
            entry.AccessCount = reader.ReadInt32();
            entry.Metadata = new EntryMetadata
            {
                FileType = ReadNullableString(reader),
                FileSize = reader.ReadInt64(),
                HasFileSize = reader.ReadBoolean(),
                IsDirectory = reader.ReadBoolean(),
                Category = ReadNullableString(reader)
            };
            int tagCount = reader.ReadInt32();
            if (tagCount < 0 || tagCount > 10000)
            {
                throw new InvalidDataException("Windows index tag count is invalid.");
            }
            for (int index = 0; index < tagCount; index++)
            {
                entry.Metadata.Tags.Add(ReadString(reader));
            }
            entry.Trigrams = TrigramIndex.SearchTrigrams(entry);
            return entry;
        }

        private static void WriteNullableString(BinaryWriter writer, string value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                WriteString(writer, value);
            }
        }

        private static string ReadNullableString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? ReadString(reader) : null;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value ?? String.Empty);
        }

        private static string ReadString(BinaryReader reader)
        {
            string value = reader.ReadString();
            if (value.Length > 32768)
            {
                throw new InvalidDataException("Windows index contains an oversized string.");
            }
            return value;
        }
    }

    public sealed class ClickstreamLogger : IDisposable
    {
        private readonly object _sync = new object();
        private readonly StreamWriter _writer;
        private readonly JavaScriptSerializer _serializer;

        public string Path { get; private set; }
        public int ClickCount { get; private set; }

        public ClickstreamLogger(string path)
        {
            string parent = System.IO.Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            Path = path;
            ClickCount = File.Exists(path) ? File.ReadLines(path).Count() : 0;
            _writer = new StreamWriter(new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough), new UTF8Encoding(false));
            _writer.AutoFlush = true;
            _serializer = new JavaScriptSerializer();
        }

        public void Log(
            string query,
            ulong selectedId,
            string selectedName,
            int rankPosition,
            QueryContext context,
            IEnumerable<SearchResult> candidates)
        {
            Dictionary<string, object> value = new Dictionary<string, object>();
            value["query"] = query;
            value["selected_id"] = selectedId.ToString();
            value["selected_name"] = selectedName;
            value["timestamp"] = context.Timestamp;
            value["rank_position"] = Math.Max(1, Math.Min(10, rankPosition));
            value["context"] = new Dictionary<string, object>
            {
                { "active_app", context.ActiveApplication },
                { "cwd", context.WorkingDirectory },
                { "timestamp", context.Timestamp },
                { "hour_of_day", context.HourOfDay },
                { "day_of_week", context.DayOfWeek }
            };
            value["candidates"] = candidates.Select(delegate(SearchResult result)
            {
                return (object)new Dictionary<string, object>
                {
                    { "id", result.Entry.Id.ToString() },
                    { "name", result.Entry.Name },
                    { "features", FeatureDictionary(result.Features) }
                };
            }).ToArray();
            string json = _serializer.Serialize(value);
            lock (_sync)
            {
                _writer.WriteLine(json);
                ClickCount++;
            }
        }

        private static Dictionary<string, object> FeatureDictionary(RankerFeatures features)
        {
            return new Dictionary<string, object>
            {
                { "is_exact_prefix", features.IsExactPrefix },
                { "is_trigram_match", features.IsTrigramMatch },
                { "is_levenshtein_match", features.IsLevenshteinMatch },
                { "frecency", features.Frecency },
                { "recency_hours", features.RecencyHours },
                { "access_count", features.AccessCount },
                { "edit_distance", features.EditDistance },
                { "time_hour_bucket", features.TimeHourBucket },
                { "day_of_week", features.DayOfWeek },
                { "active_app_match", features.ActiveAppMatch },
                { "same_directory", features.SameDirectory },
                { "file_type_popularity", features.FileTypePopularity }
            };
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _writer.Dispose();
            }
        }
    }
}
