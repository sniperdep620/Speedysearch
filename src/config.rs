use anyhow::{Context, Result};
use serde::Deserialize;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(default)]
pub struct Config {
    pub indexing: IndexingConfig,
    pub performance: PerformanceConfig,
    pub ranking: RankingConfig,
    pub training: TrainingConfig,
    pub features: FeaturesConfig,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(default)]
pub struct IndexingConfig {
    pub watch_paths: Vec<String>,
    pub exclude_patterns: Vec<String>,
    pub index_update_interval_secs: u64,
    pub batch_update_interval_ms: u64,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(default)]
pub struct PerformanceConfig {
    pub max_stage1_candidates: usize,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(default)]
pub struct RankingConfig {
    pub model_path: String,
    pub model_enabled: bool,
    pub enable_clickstream: bool,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(default)]
pub struct TrainingConfig {
    pub auto_train_on_idle: bool,
    pub min_samples_to_train: usize,
    pub training_script: String,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(default)]
pub struct FeaturesConfig {
    pub use_time_of_day: bool,
    pub use_active_app: bool,
    pub use_working_directory: bool,
    pub use_file_type_popularity: bool,
}

impl Default for IndexingConfig {
    fn default() -> Self {
        Self {
            watch_paths: vec![
                "~/".into(),
                "~/Desktop".into(),
                "~/Documents".into(),
                "~/Downloads".into(),
            ],
            exclude_patterns: vec![
                ".git".into(),
                ".cache".into(),
                "__pycache__".into(),
                "node_modules".into(),
                "target".into(),
            ],
            index_update_interval_secs: 3_600,
            batch_update_interval_ms: 500,
        }
    }
}

impl Default for PerformanceConfig {
    fn default() -> Self {
        Self {
            max_stage1_candidates: 100,
        }
    }
}

impl Default for RankingConfig {
    fn default() -> Self {
        Self {
            model_path: "~/.cache/speedysearch/ranker.txt".into(),
            model_enabled: true,
            enable_clickstream: true,
        }
    }
}

impl Default for TrainingConfig {
    fn default() -> Self {
        Self {
            auto_train_on_idle: true,
            min_samples_to_train: 50,
            training_script: "~/.config/speedysearch/train_ranker.py".into(),
        }
    }
}

impl Default for FeaturesConfig {
    fn default() -> Self {
        Self {
            use_time_of_day: true,
            use_active_app: true,
            use_working_directory: true,
            use_file_type_popularity: true,
        }
    }
}

impl Config {
    pub fn load(path: &Path) -> Result<Self> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let contents =
            std::fs::read_to_string(path).with_context(|| format!("reading {}", path.display()))?;
        toml::from_str(&contents).with_context(|| format!("parsing {}", path.display()))
    }

    pub fn watch_paths(&self, home: &Path) -> Vec<PathBuf> {
        let mut paths: Vec<PathBuf> = self
            .indexing
            .watch_paths
            .iter()
            .map(|raw| {
                if raw == "~" {
                    home.to_path_buf()
                } else if let Some(suffix) = raw.strip_prefix("~/") {
                    home.join(suffix)
                } else {
                    PathBuf::from(raw)
                }
            })
            .collect();
        paths.sort();
        paths.dedup();
        let candidates = paths.clone();
        paths.retain(|candidate| {
            !candidates
                .iter()
                .any(|other| other != candidate && candidate.starts_with(other))
        });
        paths
    }

    pub fn model_path(&self, home: &Path) -> Option<PathBuf> {
        if !self.ranking.model_enabled {
            return None;
        }
        Some(expand_home(&self.ranking.model_path, home))
    }
}

fn expand_home(raw: &str, home: &Path) -> PathBuf {
    if raw == "~" {
        home.to_path_buf()
    } else if let Some(suffix) = raw.strip_prefix("~/") {
        home.join(suffix)
    } else {
        PathBuf::from(raw)
    }
}

pub fn default_config_path(home: &Path) -> PathBuf {
    std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| home.join(".config"))
        .join("speedysearch/config.toml")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overlapping_default_roots_are_deduplicated() {
        let paths = Config::default().watch_paths(Path::new("/home/test"));
        assert_eq!(paths, vec![PathBuf::from("/home/test")]);
    }
}
