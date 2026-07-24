use crate::index::{now_secs, searchable_terms, IndexEntry, SearchIndex, TrigramIndex};
use crate::stage0::QueryIntent;
use anyhow::{anyhow, Result};
use std::collections::{HashMap, HashSet};

pub fn levenshtein_bounded(first: &str, second: &str, max_dist: usize) -> Option<usize> {
    let left: Vec<char> = first.to_lowercase().chars().collect();
    let right: Vec<char> = second.to_lowercase().chars().collect();
    if left.len().abs_diff(right.len()) > max_dist {
        return None;
    }
    if left.is_empty() {
        return (right.len() <= max_dist).then_some(right.len());
    }
    if right.is_empty() {
        return (left.len() <= max_dist).then_some(left.len());
    }

    let mut previous: Vec<usize> = (0..=right.len()).collect();
    let mut current = vec![0; right.len() + 1];
    for (i, left_char) in left.iter().enumerate() {
        current[0] = i + 1;
        let mut row_minimum = current[0];
        for (j, right_char) in right.iter().enumerate() {
            let substitution = previous[j] + usize::from(left_char != right_char);
            current[j + 1] = substitution.min(previous[j + 1] + 1).min(current[j] + 1);
            row_minimum = row_minimum.min(current[j + 1]);
        }
        if row_minimum > max_dist {
            return None;
        }
        std::mem::swap(&mut previous, &mut current);
    }
    (previous[right.len()] <= max_dist).then_some(previous[right.len()])
}

#[derive(Clone, Debug)]
pub struct Stage1Filter {
    index: SearchIndex,
    max_candidates: usize,
}

impl Stage1Filter {
    pub fn new(index: SearchIndex) -> Self {
        Self {
            index,
            max_candidates: 100,
        }
    }

    pub fn with_max_candidates(index: SearchIndex, max_candidates: usize) -> Self {
        Self {
            index,
            max_candidates: max_candidates.max(1),
        }
    }

    pub fn filter(&self, query: &str, intent: QueryIntent) -> Result<Vec<u64>> {
        let normalized = query.trim().to_lowercase();
        let entries = self
            .index
            .entries
            .read()
            .map_err(|_| anyhow!("entries lock poisoned"))?;
        let positions = self
            .index
            .id_positions
            .read()
            .map_err(|_| anyhow!("positions lock poisoned"))?;
        if normalized.is_empty() {
            let mut ids: Vec<u64> = entries.iter().map(|entry| entry.id).collect();
            self.sort_ids(&mut ids, &entries, &positions, "")?;
            ids.truncate(10);
            return Ok(ids);
        }

        let mut accepted = HashSet::new();
        let mut fuzzy_pool = HashMap::new();
        match &intent {
            QueryIntent::ExactApp(name) => self.collect(
                &self.index.app_index,
                name,
                0.8,
                &mut accepted,
                &mut fuzzy_pool,
            )?,
            QueryIntent::FileExtension(extension) => self.collect(
                &self.index.file_index,
                extension,
                0.6,
                &mut accepted,
                &mut fuzzy_pool,
            )?,
            QueryIntent::Setting(keyword) => {
                self.collect(
                    &self.index.setting_index,
                    keyword,
                    0.8,
                    &mut accepted,
                    &mut fuzzy_pool,
                )?;
                self.collect(
                    &self.index.app_index,
                    &normalized,
                    0.8,
                    &mut accepted,
                    &mut fuzzy_pool,
                )?;
            }
            QueryIntent::Mixed => {
                self.collect(
                    &self.index.app_index,
                    &normalized,
                    0.8,
                    &mut accepted,
                    &mut fuzzy_pool,
                )?;
                self.collect(
                    &self.index.file_index,
                    &normalized,
                    0.7,
                    &mut accepted,
                    &mut fuzzy_pool,
                )?;
                self.collect(
                    &self.index.setting_index,
                    &normalized,
                    0.8,
                    &mut accepted,
                    &mut fuzzy_pool,
                )?;
            }
        }

        self.collect_exact(&intent, &normalized, &mut accepted)?;
        if normalized.chars().count() < 15 {
            let mut fuzzy_pool: Vec<_> = fuzzy_pool.into_iter().collect();
            fuzzy_pool.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
            for (id, _) in fuzzy_pool.into_iter().take(self.max_candidates) {
                if let Some(entry) = positions
                    .get(&id)
                    .and_then(|position| entries.get(*position))
                {
                    if searchable_terms(entry)
                        .any(|term| levenshtein_bounded(&normalized, term, 2).is_some())
                    {
                        accepted.insert(id);
                    }
                }
            }
        }

        let mut ids: Vec<u64> = accepted.into_iter().collect();
        self.sort_ids(&mut ids, &entries, &positions, &normalized)?;
        ids.truncate(10);
        Ok(ids)
    }

    fn collect(
        &self,
        index: &std::sync::RwLock<TrigramIndex>,
        query: &str,
        threshold: f32,
        accepted: &mut HashSet<u64>,
        fuzzy_pool: &mut HashMap<u64, usize>,
    ) -> Result<()> {
        let index = index
            .read()
            .map_err(|_| anyhow!("trigram index lock poisoned"))?;
        accepted.extend(index.search_by_trigrams(query, threshold, self.max_candidates));
        let query_trigrams = TrigramIndex::generate_trigrams(query);
        let mut rarest: Vec<(u32, usize)> = query_trigrams
            .into_iter()
            .map(|tri| (tri, index.trigrams.get(&tri).map_or(0, Vec::len)))
            .filter(|(_, posting_count)| *posting_count > 0)
            .collect();
        rarest.sort_by_key(|(_, posting_count)| *posting_count);
        for (tri, _) in rarest.into_iter().take(3) {
            if let Some(postings) = index.trigrams.get(&tri) {
                for id in postings {
                    *fuzzy_pool.entry(*id).or_default() += 1;
                }
            }
        }
        Ok(())
    }

    fn collect_exact(
        &self,
        intent: &QueryIntent,
        query: &str,
        accepted: &mut HashSet<u64>,
    ) -> Result<()> {
        let indexes = match intent {
            QueryIntent::ExactApp(_) => vec![&self.index.app_index],
            QueryIntent::FileExtension(_) => vec![&self.index.file_index],
            QueryIntent::Setting(_) => vec![&self.index.setting_index, &self.index.app_index],
            QueryIntent::Mixed => vec![
                &self.index.app_index,
                &self.index.file_index,
                &self.index.setting_index,
            ],
        };
        for index in indexes {
            if let Some(id) = index
                .read()
                .map_err(|_| anyhow!("trigram index lock poisoned"))?
                .exact_index
                .get(query)
            {
                accepted.insert(*id);
            }
        }
        Ok(())
    }

    fn sort_ids(
        &self,
        ids: &mut [u64],
        entries: &[IndexEntry],
        positions: &HashMap<u64, usize>,
        query: &str,
    ) -> Result<()> {
        let frecency = self
            .index
            .frecency
            .read()
            .map_err(|_| anyhow!("frecency lock poisoned"))?;
        let now = now_secs();
        ids.sort_by(|left, right| {
            let left_entry = positions
                .get(left)
                .and_then(|position| entries.get(*position));
            let right_entry = positions
                .get(right)
                .and_then(|position| entries.get(*position));
            let quality = |entry: Option<&IndexEntry>| {
                if query.is_empty() {
                    0
                } else {
                    entry.map_or(0, |item| {
                        searchable_terms(item)
                            .map(str::to_lowercase)
                            .map(|term| {
                                if term == query {
                                    2
                                } else if term.starts_with(query) {
                                    1
                                } else {
                                    0
                                }
                            })
                            .max()
                            .unwrap_or(0)
                    })
                }
            };
            let left_score = calculate_frecency(frecency.get(left).copied(), left_entry, now);
            let right_score = calculate_frecency(frecency.get(right).copied(), right_entry, now);
            quality(right_entry)
                .cmp(&quality(left_entry))
                .then_with(|| right_score.total_cmp(&left_score))
                .then_with(|| {
                    left_entry
                        .map(|entry| entry.name.as_str())
                        .cmp(&right_entry.map(|entry| entry.name.as_str()))
                })
                .then_with(|| left.cmp(right))
        });
        Ok(())
    }
}

pub fn calculate_frecency(value: Option<(u32, u64)>, entry: Option<&IndexEntry>, now: u64) -> f32 {
    let fallback = entry
        .map(|item| (item.access_count, item.last_accessed))
        .unwrap_or((0, 0));
    let (count, last_accessed) = value.unwrap_or(fallback);
    if count == 0 {
        return 0.0;
    }
    let days = now.saturating_sub(last_accessed) / 86_400;
    count as f32 * 0.8_f32.powf(days as f32)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounded_distance_handles_unicode_and_limits() {
        assert_eq!(levenshtein_bounded("document", "dcument", 2), Some(1));
        assert_eq!(levenshtein_bounded("résumé", "resume", 2), Some(2));
        assert_eq!(levenshtein_bounded("document", "xyz", 2), None);
    }

    #[test]
    fn frecency_decays_daily() {
        assert!(
            (calculate_frecency(Some((10, 100_000)), None, 186_400) - 8.0).abs() < f32::EPSILON
        );
    }
}
