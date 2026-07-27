using System;
using System.Collections.Generic;

namespace Speedysearch.Windows
{
    public enum EntryType
    {
        App = 1,
        File = 2,
        Setting = 3,
        Command = 4
    }

    public enum SearchFilter
    {
        All,
        Apps,
        Files,
        Settings
    }

    [Serializable]
    public sealed class EntryMetadata
    {
        public string FileType { get; set; }
        public long FileSize { get; set; }
        public bool HasFileSize { get; set; }
        public bool IsDirectory { get; set; }
        public List<string> Tags { get; set; }
        public string Category { get; set; }

        public EntryMetadata()
        {
            Tags = new List<string>();
        }

        public EntryMetadata Clone()
        {
            return new EntryMetadata
            {
                FileType = FileType,
                FileSize = FileSize,
                HasFileSize = HasFileSize,
                IsDirectory = IsDirectory,
                Tags = new List<string>(Tags),
                Category = Category
            };
        }
    }

    [Serializable]
    public sealed class IndexEntry
    {
        public ulong Id { get; set; }
        public EntryType EntryType { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string IconPath { get; set; }
        public float FrecencyScore { get; set; }
        public long LastAccessed { get; set; }
        public int AccessCount { get; set; }
        public int[] Trigrams { get; set; }
        public EntryMetadata Metadata { get; set; }

        public IndexEntry()
        {
            Name = String.Empty;
            Path = String.Empty;
            Trigrams = new int[0];
            Metadata = new EntryMetadata();
        }

        public IndexEntry Clone()
        {
            return new IndexEntry
            {
                Id = Id,
                EntryType = EntryType,
                Name = Name,
                Path = Path,
                IconPath = IconPath,
                FrecencyScore = FrecencyScore,
                LastAccessed = LastAccessed,
                AccessCount = AccessCount,
                Trigrams = (int[])Trigrams.Clone(),
                Metadata = Metadata.Clone()
            };
        }
    }

    public sealed class SearchResult
    {
        public IndexEntry Entry { get; set; }
        public float Score { get; set; }
        public RankerFeatures Features { get; set; }
    }

    public sealed class QueryResponse
    {
        public List<SearchResult> Results { get; set; }
        public long LatencyMicroseconds { get; set; }
        public bool IndexStale { get; set; }

        public QueryResponse()
        {
            Results = new List<SearchResult>();
        }

        public double LatencyMilliseconds
        {
            get { return LatencyMicroseconds / 1000.0; }
        }
    }

    public sealed class QueryContext
    {
        public string ActiveApplication { get; set; }
        public string WorkingDirectory { get; set; }
        public long Timestamp { get; set; }
        public int HourOfDay { get; set; }
        public int DayOfWeek { get; set; }
    }

    public sealed class RankerFeatures
    {
        public float Frecency { get; set; }
        public float RecencyHours { get; set; }
        public float AccessCount { get; set; }
        public bool IsExactPrefix { get; set; }
        public bool IsTrigramMatch { get; set; }
        public bool IsLevenshteinMatch { get; set; }
        public float EditDistance { get; set; }
        public float TimeHourBucket { get; set; }
        public float DayOfWeek { get; set; }
        public bool ActiveAppMatch { get; set; }
        public bool SameDirectory { get; set; }
        public float FileTypePopularity { get; set; }

        public float[] AsModelInput()
        {
            return new[]
            {
                IsExactPrefix ? 1.0f : 0.0f,
                IsTrigramMatch ? 1.0f : 0.0f,
                IsLevenshteinMatch ? 1.0f : 0.0f,
                Frecency,
                RecencyHours,
                AccessCount,
                EditDistance,
                TimeHourBucket,
                DayOfWeek,
                ActiveAppMatch ? 1.0f : 0.0f,
                SameDirectory ? 1.0f : 0.0f,
                FileTypePopularity
            };
        }
    }

    public sealed class IndexSnapshot
    {
        public List<IndexEntry> Entries { get; set; }
        public long LastIndexed { get; set; }

        public IndexSnapshot()
        {
            Entries = new List<IndexEntry>();
        }
    }
}
