using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Speedysearch.Windows
{
    internal sealed class TrigramIndex
    {
        private readonly Dictionary<int, List<ulong>> _postings;
        private readonly Dictionary<ulong, int[]> _entryTrigrams;
        private readonly Dictionary<string, ulong> _exact;

        public TrigramIndex()
        {
            _postings = new Dictionary<int, List<ulong>>();
            _entryTrigrams = new Dictionary<ulong, int[]>();
            _exact = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        }

        public void Insert(IndexEntry entry)
        {
            foreach (string term in SearchTerms(entry))
            {
                string normalized = term.Trim();
                if (normalized.Length > 0)
                {
                    _exact[normalized] = entry.Id;
                }
            }
            _entryTrigrams[entry.Id] = entry.Trigrams;
            foreach (int trigram in entry.Trigrams)
            {
                List<ulong> values;
                if (!_postings.TryGetValue(trigram, out values))
                {
                    values = new List<ulong>();
                    _postings.Add(trigram, values);
                }
                values.Add(entry.Id);
            }
        }

        public void CollectExact(string query, HashSet<ulong> accepted)
        {
            ulong id;
            if (_exact.TryGetValue(query, out id))
            {
                accepted.Add(id);
            }
        }

        public List<ulong> Search(string query, float minimumOverlap, int limit)
        {
            int[] queryTrigrams = Generate(query);
            if (queryTrigrams.Length == 0)
            {
                return new List<ulong>();
            }

            int required = Math.Max(1, Math.Min(
                queryTrigrams.Length,
                (int)Math.Ceiling(queryTrigrams.Length * minimumOverlap)));
            int seedCount = queryTrigrams.Length - required + 1;
            List<PostingSize> seeds = new List<PostingSize>();
            foreach (int trigram in queryTrigrams)
            {
                List<ulong> list;
                seeds.Add(new PostingSize(
                    trigram,
                    _postings.TryGetValue(trigram, out list) ? list.Count : 0));
            }
            seeds.Sort(delegate(PostingSize left, PostingSize right)
            {
                return left.Count.CompareTo(right.Count);
            });

            HashSet<ulong> seen = new HashSet<ulong>();
            for (int index = 0; index < Math.Min(seedCount, seeds.Count); index++)
            {
                List<ulong> values;
                if (_postings.TryGetValue(seeds[index].Trigram, out values))
                {
                    foreach (ulong id in values)
                    {
                        seen.Add(id);
                    }
                }
            }

            List<ScoredId> scored = new List<ScoredId>();
            foreach (ulong id in seen)
            {
                int[] entry;
                if (_entryTrigrams.TryGetValue(id, out entry))
                {
                    float overlap = OverlapRatio(queryTrigrams, entry);
                    if (overlap >= minimumOverlap)
                    {
                        scored.Add(new ScoredId(id, overlap));
                    }
                }
            }
            scored.Sort(delegate(ScoredId left, ScoredId right)
            {
                int scoreOrder = right.Score.CompareTo(left.Score);
                return scoreOrder != 0 ? scoreOrder : left.Id.CompareTo(right.Id);
            });
            return scored.Take(limit).Select(delegate(ScoredId item) { return item.Id; }).ToList();
        }

        public void AddFuzzySeeds(string query, Dictionary<ulong, int> pool)
        {
            List<PostingSize> seeds = new List<PostingSize>();
            foreach (int trigram in Generate(query))
            {
                List<ulong> list;
                if (_postings.TryGetValue(trigram, out list) && list.Count > 0)
                {
                    seeds.Add(new PostingSize(trigram, list.Count));
                }
            }
            seeds.Sort(delegate(PostingSize left, PostingSize right)
            {
                return left.Count.CompareTo(right.Count);
            });
            for (int index = 0; index < Math.Min(3, seeds.Count); index++)
            {
                foreach (ulong id in _postings[seeds[index].Trigram])
                {
                    int count;
                    pool.TryGetValue(id, out count);
                    pool[id] = count + 1;
                }
            }
        }

        public static int[] Generate(string input)
        {
            string normalized = "  " + (input ?? String.Empty).ToLowerInvariant() + "  ";
            if (normalized.Length < 3)
            {
                return new int[0];
            }
            int[] hashes = new int[normalized.Length - 2];
            for (int index = 0; index <= normalized.Length - 3; index++)
            {
                unchecked
                {
                    uint hash = 2166136261;
                    for (int offset = 0; offset < 3; offset++)
                    {
                        char value = normalized[index + offset];
                        hash ^= (byte)(value & 0xff);
                        hash *= 16777619;
                        hash ^= (byte)(value >> 8);
                        hash *= 16777619;
                    }
                    hashes[index] = (int)hash;
                }
            }
            Array.Sort(hashes);
            int unique = 0;
            for (int index = 0; index < hashes.Length; index++)
            {
                if (index == 0 || hashes[index] != hashes[index - 1])
                {
                    hashes[unique++] = hashes[index];
                }
            }
            if (unique == hashes.Length)
            {
                return hashes;
            }
            int[] result = new int[unique];
            Array.Copy(hashes, result, unique);
            return result;
        }

        public static int[] SearchTrigrams(IndexEntry entry)
        {
            HashSet<int> values = new HashSet<int>();
            foreach (string term in SearchTerms(entry))
            {
                foreach (int trigram in Generate(term))
                {
                    values.Add(trigram);
                }
            }
            int[] result = values.ToArray();
            Array.Sort(result);
            return result;
        }

        public static float OverlapRatio(int[] query, int[] entry)
        {
            if (query.Length == 0)
            {
                return 0.0f;
            }
            int queryPosition = 0;
            int entryPosition = 0;
            int matches = 0;
            while (queryPosition < query.Length && entryPosition < entry.Length)
            {
                if (query[queryPosition] < entry[entryPosition])
                {
                    queryPosition++;
                }
                else if (query[queryPosition] > entry[entryPosition])
                {
                    entryPosition++;
                }
                else
                {
                    matches++;
                    queryPosition++;
                    entryPosition++;
                }
            }
            return (float)matches / query.Length;
        }

        public static IEnumerable<string> SearchTerms(IndexEntry entry)
        {
            yield return entry.Name ?? String.Empty;
            if (entry.Metadata != null && entry.Metadata.Tags != null)
            {
                foreach (string tag in entry.Metadata.Tags)
                {
                    yield return tag;
                }
            }
        }

        public static ulong StableId(EntryType type, string source)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                ((int)type).ToString() + "\0" + (source ?? String.Empty).ToLowerInvariant());
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (byte value in bytes)
                {
                    hash ^= value;
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }

        private struct PostingSize
        {
            public readonly int Trigram;
            public readonly int Count;

            public PostingSize(int trigram, int count)
            {
                Trigram = trigram;
                Count = count;
            }
        }

        private struct ScoredId
        {
            public readonly ulong Id;
            public readonly float Score;

            public ScoredId(ulong id, float score)
            {
                Id = id;
                Score = score;
            }
        }
    }
}
