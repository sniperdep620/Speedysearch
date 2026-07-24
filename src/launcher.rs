use crate::cache::{
    default_index_path, default_model_path, save_index, ClickstreamCandidate, ClickstreamEntry,
    ClickstreamLogger,
};
use crate::index::{now_secs, IndexEntry, SearchIndex};
use crate::ipc::{QueryResponse, SearchResult};
use crate::stage0::IntentClassifier;
use crate::stage1::Stage1Filter;
use crate::stage2::{FeatureExtractor, FeatureOptions, QueryContext, RankerFeatures, RankerModel};
use anyhow::{anyhow, Result};
use chrono::{Datelike, Local, Timelike};
use std::path::Path;
use std::process::Command;
use std::sync::{Arc, RwLock};
use std::time::Instant;

#[derive(Clone, Debug)]
pub struct SearchLauncher {
    pub index: SearchIndex,
    classifier: Arc<RwLock<IntentClassifier>>,
    stage1: Stage1Filter,
    feature_extractor: Arc<RwLock<FeatureExtractor>>,
    ranker_model: Option<Arc<RankerModel>>,
    clickstream_logger: Option<ClickstreamLogger>,
}

impl SearchLauncher {
    pub fn new(index: SearchIndex) -> Result<Self> {
        Self::with_max_candidates(index, 100)
    }

    pub fn with_max_candidates(index: SearchIndex, max_candidates: usize) -> Result<Self> {
        let model_path = default_model_path();
        Self::with_options(index, max_candidates, Some(&model_path), true)
    }

    pub fn with_options(
        index: SearchIndex,
        max_candidates: usize,
        model_path: Option<&Path>,
        enable_clickstream: bool,
    ) -> Result<Self> {
        let entries = index
            .entries
            .read()
            .map_err(|_| anyhow!("entries lock poisoned"))?;
        let classifier = IntentClassifier::new(&entries);
        let feature_extractor = FeatureExtractor::from_entries(&entries);
        drop(entries);
        let ranker_model = model_path
            .and_then(|path| RankerModel::load_from_file(&path.to_string_lossy()).ok())
            .map(Arc::new);
        let clickstream_logger = if enable_clickstream {
            ClickstreamLogger::new().ok()
        } else {
            None
        };
        Ok(Self {
            stage1: Stage1Filter::with_max_candidates(index.clone(), max_candidates),
            index,
            classifier: Arc::new(RwLock::new(classifier)),
            feature_extractor: Arc::new(RwLock::new(feature_extractor)),
            ranker_model,
            clickstream_logger,
        })
    }

    pub fn refresh_classifier(&self) -> Result<()> {
        let entries = self
            .index
            .entries
            .read()
            .map_err(|_| anyhow!("entries lock poisoned"))?;
        let classifier = IntentClassifier::new(&entries);
        let options = self
            .feature_extractor
            .read()
            .map_err(|_| anyhow!("feature extractor lock poisoned"))?
            .options();
        let extractor = FeatureExtractor::from_entries_with_options(&entries, options);
        *self
            .classifier
            .write()
            .map_err(|_| anyhow!("classifier lock poisoned"))? = classifier;
        *self
            .feature_extractor
            .write()
            .map_err(|_| anyhow!("feature extractor lock poisoned"))? = extractor;
        Ok(())
    }

    pub fn set_feature_options(&self, options: FeatureOptions) -> Result<()> {
        let entries = self
            .index
            .entries
            .read()
            .map_err(|_| anyhow!("entries lock poisoned"))?;
        *self
            .feature_extractor
            .write()
            .map_err(|_| anyhow!("feature extractor lock poisoned"))? =
            FeatureExtractor::from_entries_with_options(&entries, options);
        Ok(())
    }

    pub fn query(&self, query: &str) -> Result<QueryResponse> {
        let start = Instant::now();
        let context = self.build_query_context();
        let ranked = self.rank_candidates(query, &context)?;
        let results = ranked
            .into_iter()
            .map(|(entry, score, _)| SearchResult {
                entry,
                ml_score: score,
            })
            .collect();
        let last_indexed = *self
            .index
            .last_indexed
            .lock()
            .map_err(|_| anyhow!("timestamp lock poisoned"))?;
        let index_stale = now_secs().saturating_sub(last_indexed) > 1;
        Ok(QueryResponse {
            results,
            latency_ms: start.elapsed().as_millis(),
            index_stale,
        })
    }

    pub fn log_selection(&self, query: &str, selected_id: u64, rank_position: u8) -> Result<()> {
        let context = self.build_query_context();
        let ranked = self.rank_candidates(query, &context)?;
        let selected_name = ranked
            .iter()
            .find(|(entry, _, _)| entry.id == selected_id)
            .map(|(entry, _, _)| entry.name.clone())
            .or_else(|| {
                self.index
                    .entries
                    .read()
                    .ok()?
                    .iter()
                    .find(|entry| entry.id == selected_id)
                    .map(|entry| entry.name.clone())
            })
            .ok_or_else(|| anyhow!("selected entry {selected_id} is no longer indexed"))?;
        let candidates = ranked
            .iter()
            .enumerate()
            .map(|(position, (entry, _, features))| ClickstreamCandidate {
                id: entry.id,
                name: entry.name.clone(),
                rank_position: (position + 1) as u8,
                features: features.clone(),
            })
            .collect();
        if let Some(logger) = &self.clickstream_logger {
            if let Err(error) = logger.log(&ClickstreamEntry {
                query: query.to_string(),
                selected_id,
                selected_name,
                timestamp: context.timestamp,
                context: context.clone(),
                rank_position: rank_position.clamp(1, 10),
                candidates,
            }) {
                eprintln!("warning: clickstream write failed: {error:#}");
            }
        }
        self.index.record_access(selected_id, context.timestamp)?;
        if let Ok(entries) = self.index.entries.read() {
            if let Ok(mut extractor) = self.feature_extractor.write() {
                let options = extractor.options();
                *extractor = FeatureExtractor::from_entries_with_options(&entries, options);
            }
        }
        if let Err(error) = save_index(&self.index, &default_index_path()) {
            eprintln!("warning: could not persist updated frecency: {error:#}");
        }
        Ok(())
    }

    pub fn build_query_context(&self) -> QueryContext {
        let now = Local::now();
        QueryContext {
            active_app: active_application(),
            cwd: std::env::current_dir().ok(),
            timestamp: now.timestamp().max(0) as u64,
            hour_of_day: now.hour() as u8,
            day_of_week: now.weekday().num_days_from_monday() as u8,
        }
    }

    pub fn model_loaded(&self) -> bool {
        self.ranker_model.is_some()
    }
    pub fn clickstream_path(&self) -> Option<&Path> {
        self.clickstream_logger
            .as_ref()
            .map(ClickstreamLogger::get_path)
    }

    fn rank_candidates(
        &self,
        query: &str,
        context: &QueryContext,
    ) -> Result<Vec<(IndexEntry, f32, RankerFeatures)>> {
        let intent = self
            .classifier
            .read()
            .map_err(|_| anyhow!("classifier lock poisoned"))?
            .classify(query);
        let ids = self.stage1.filter(query, intent)?;
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
        let extractor = self
            .feature_extractor
            .read()
            .map_err(|_| anyhow!("feature extractor lock poisoned"))?;
        let mut ranked: Vec<_> = ids
            .into_iter()
            .filter_map(|id| {
                let entry = positions
                    .get(&id)
                    .and_then(|position| entries.get(*position))?
                    .clone();
                let features = extractor.extract(&entry, context, query);
                let score = self.ranker_model.as_ref().map_or_else(
                    || fallback_rank_score(&features),
                    |model| model.predict(&features),
                );
                Some((entry, score.clamp(0.0, 1.0), features))
            })
            .collect();
        ranked.sort_by(|left, right| {
            right
                .1
                .total_cmp(&left.1)
                .then_with(|| left.0.name.cmp(&right.0.name))
                .then_with(|| left.0.id.cmp(&right.0.id))
        });
        ranked.truncate(10);
        Ok(ranked)
    }
}

fn fallback_rank_score(features: &RankerFeatures) -> f32 {
    let match_quality = if features.is_exact_prefix {
        1.0
    } else if features.is_levenshtein_match {
        0.9
    } else if features.is_trigram_match {
        0.7
    } else {
        0.0
    };
    match_quality * 0.85 + features.frecency * 0.15
}

fn active_application() -> Option<String> {
    if let Some(value) = std::env::var_os("SPEEDYSEARCH_ACTIVE_APP") {
        return Some(value.to_string_lossy().into_owned());
    }
    let output = Command::new("xdotool")
        .args(["getactivewindow", "getwindowclassname"])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    let value = String::from_utf8_lossy(&output.stdout).trim().to_string();
    (!value.is_empty()).then_some(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fallback_ranking_prefers_text_matches_over_unrelated_frecency() {
        let exact = RankerFeatures {
            is_exact_prefix: true,
            ..RankerFeatures::default()
        };
        let unrelated = RankerFeatures {
            frecency: 1.0,
            ..RankerFeatures::default()
        };
        assert!(fallback_rank_score(&exact) > fallback_rank_score(&unrelated));
    }
}
