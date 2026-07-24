use crate::index::{searchable_terms, EntryType, IndexEntry, TrigramIndex};
use crate::stage1::levenshtein_bounded;
use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

pub const FEATURE_NAMES: [&str; 12] = [
    "is_exact_prefix",
    "is_trigram_match",
    "is_levenshtein_match",
    "frecency",
    "recency_hours",
    "access_count",
    "edit_distance",
    "time_hour_bucket",
    "day_of_week",
    "active_app_match",
    "same_directory",
    "file_type_popularity",
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FeatureOptions {
    pub use_time_of_day: bool,
    pub use_active_app: bool,
    pub use_working_directory: bool,
    pub use_file_type_popularity: bool,
}

impl Default for FeatureOptions {
    fn default() -> Self {
        Self {
            use_time_of_day: true,
            use_active_app: true,
            use_working_directory: true,
            use_file_type_popularity: true,
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct QueryContext {
    pub active_app: Option<String>,
    pub cwd: Option<PathBuf>,
    pub timestamp: u64,
    pub hour_of_day: u8,
    pub day_of_week: u8,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
pub struct RankerFeatures {
    pub frecency: f32,
    pub recency_hours: f32,
    pub access_count: f32,
    pub is_exact_prefix: bool,
    pub is_trigram_match: bool,
    pub is_levenshtein_match: bool,
    pub edit_distance: f32,
    pub time_hour_bucket: f32,
    pub day_of_week: f32,
    pub active_app_match: bool,
    pub same_directory: bool,
    pub file_type_popularity: f32,
}

impl RankerFeatures {
    pub fn as_model_input(&self) -> [f32; 12] {
        [
            self.is_exact_prefix as u8 as f32,
            self.is_trigram_match as u8 as f32,
            self.is_levenshtein_match as u8 as f32,
            self.frecency,
            self.recency_hours,
            self.access_count,
            self.edit_distance,
            self.time_hour_bucket,
            self.day_of_week,
            self.active_app_match as u8 as f32,
            self.same_directory as u8 as f32,
            self.file_type_popularity,
        ]
    }
}

#[derive(Clone, Debug, Default)]
pub struct FeatureExtractor {
    pub file_type_stats: HashMap<String, f32>,
    pub app_stats: HashMap<String, f32>,
    options: FeatureOptions,
}

impl FeatureExtractor {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_entries(entries: &[IndexEntry]) -> Self {
        Self::from_entries_with_options(entries, FeatureOptions::default())
    }

    pub fn from_entries_with_options(entries: &[IndexEntry], options: FeatureOptions) -> Self {
        let total_file_opens: u64 = entries
            .iter()
            .filter(|entry| entry.entry_type == EntryType::File)
            .map(|entry| entry.access_count as u64)
            .sum();
        let total_app_opens: u64 = entries
            .iter()
            .filter(|entry| entry.entry_type == EntryType::App)
            .map(|entry| entry.access_count as u64)
            .sum();
        let mut extractor = Self::new();
        for entry in entries {
            match entry.entry_type {
                EntryType::File if total_file_opens > 0 => {
                    if let Some(file_type) = entry_file_type(entry) {
                        *extractor.file_type_stats.entry(file_type).or_default() +=
                            entry.access_count as f32 / total_file_opens as f32;
                    }
                }
                EntryType::App if total_app_opens > 0 => {
                    *extractor
                        .app_stats
                        .entry(entry.name.to_lowercase())
                        .or_default() += entry.access_count as f32 / total_app_opens as f32;
                }
                _ => {}
            }
        }
        extractor.options = options;
        extractor
    }

    pub fn options(&self) -> FeatureOptions {
        self.options
    }

    pub fn extract(
        &self,
        entry: &IndexEntry,
        context: &QueryContext,
        query: &str,
    ) -> RankerFeatures {
        let normalized_query = query.trim().to_lowercase();
        let normalized_name = entry.name.to_lowercase();
        let normalized_terms: Vec<String> =
            searchable_terms(entry).map(str::to_lowercase).collect();
        let age_seconds = if entry.last_accessed == 0 {
            30 * 86_400
        } else {
            context.timestamp.saturating_sub(entry.last_accessed)
        };
        let age_hours = age_seconds as f32 / 3_600.0;
        let raw_frecency = if entry.access_count == 0 {
            entry.frecency_score.max(0.0)
        } else {
            entry.access_count as f32 * 0.8_f32.powf(age_seconds as f32 / 86_400.0)
        };
        let distance = normalized_terms
            .iter()
            .map(|term| levenshtein_distance_capped(&normalized_query, term, 999))
            .min()
            .unwrap_or_else(|| {
                levenshtein_distance_capped(&normalized_query, &normalized_name, 999)
            });
        let query_trigrams = TrigramIndex::generate_trigrams(&normalized_query);
        let entry_trigrams = if entry.trigrams.is_empty() {
            TrigramIndex::generate_trigrams(&normalized_name)
        } else {
            entry.trigrams.clone()
        };
        let file_type = entry_file_type(entry);

        RankerFeatures {
            frecency: squash(raw_frecency, 10.0),
            recency_hours: (age_hours / 720.0).clamp(0.0, 1.0),
            access_count: ((entry.access_count as f32 + 1.0).ln() / 1001.0_f32.ln())
                .clamp(0.0, 1.0),
            is_exact_prefix: !normalized_query.is_empty()
                && normalized_terms
                    .iter()
                    .any(|term| term.starts_with(&normalized_query)),
            is_trigram_match: !query_trigrams.is_empty()
                && TrigramIndex::overlap_ratio(&query_trigrams, &entry_trigrams) >= 0.6,
            is_levenshtein_match: distance <= 2,
            edit_distance: (distance as f32 / 999.0).clamp(0.0, 1.0),
            time_hour_bucket: if self.options.use_time_of_day {
                (context.hour_of_day.min(23) as f32 / 23.0).clamp(0.0, 1.0)
            } else {
                0.0
            },
            day_of_week: if self.options.use_time_of_day {
                (context.day_of_week.min(6) as f32 / 6.0).clamp(0.0, 1.0)
            } else {
                0.0
            },
            active_app_match: self.options.use_active_app
                && active_app_matches(context.active_app.as_deref(), file_type.as_deref(), entry),
            same_directory: self.options.use_working_directory
                && context.cwd.as_ref().is_some_and(|cwd| {
                    let path = Path::new(&entry.path);
                    path == cwd || path.parent().is_some_and(|parent| parent == cwd)
                }),
            file_type_popularity: if self.options.use_file_type_popularity {
                file_type
                    .as_ref()
                    .and_then(|kind| self.file_type_stats.get(kind))
                    .copied()
                    .unwrap_or(0.0)
                    .clamp(0.0, 1.0)
            } else {
                0.0
            },
        }
    }
}

fn squash(value: f32, midpoint: f32) -> f32 {
    if !value.is_finite() || value <= 0.0 {
        0.0
    } else {
        (value / (value + midpoint)).clamp(0.0, 1.0)
    }
}

fn entry_file_type(entry: &IndexEntry) -> Option<String> {
    entry
        .metadata
        .file_type
        .as_ref()
        .map(|kind| kind.trim_start_matches('.').to_lowercase())
        .or_else(|| {
            Path::new(&entry.path)
                .extension()
                .map(|kind| kind.to_string_lossy().to_lowercase())
        })
}

fn active_app_matches(
    active_app: Option<&str>,
    file_type: Option<&str>,
    entry: &IndexEntry,
) -> bool {
    let Some(app) = active_app.map(str::to_lowercase) else {
        return false;
    };
    if entry.entry_type == EntryType::App {
        return app.contains(&entry.name.to_lowercase())
            || entry.name.to_lowercase().contains(&app);
    }
    let Some(kind) = file_type else { return false };
    const GROUPS: &[(&[&str], &[&str])] = &[
        (
            &["code", "vscode", "codium", "zed", "idea", "vim", "emacs"],
            &[
                "rs", "py", "js", "ts", "tsx", "jsx", "c", "cpp", "h", "go", "java", "toml",
                "yaml", "yml", "json",
            ],
        ),
        (
            &["libreoffice", "writer", "word"],
            &["odt", "doc", "docx", "rtf"],
        ),
        (&["calc", "excel"], &["ods", "xls", "xlsx", "csv"]),
        (
            &["evince", "okular", "document viewer"],
            &["pdf", "djvu", "epub"],
        ),
        (
            &["gimp", "inkscape", "krita"],
            &["png", "jpg", "jpeg", "gif", "svg", "webp", "xcf"],
        ),
    ];
    GROUPS.iter().any(|(apps, types)| {
        apps.iter().any(|candidate| app.contains(candidate)) && types.contains(&kind)
    })
}

fn levenshtein_distance_capped(first: &str, second: &str, cap: usize) -> usize {
    if let Some(distance) = levenshtein_bounded(first, second, 2) {
        return distance;
    }
    let right: Vec<char> = second.chars().collect();
    let mut previous: Vec<usize> = (0..=right.len()).collect();
    for (i, left) in first.chars().enumerate() {
        let mut current = Vec::with_capacity(right.len() + 1);
        current.push(i + 1);
        for (j, right) in right.iter().enumerate() {
            current.push(
                (previous[j] + usize::from(left != *right))
                    .min(previous[j + 1] + 1)
                    .min(current[j] + 1),
            );
        }
        previous = current;
    }
    previous.get(right.len()).copied().unwrap_or(cap).min(cap)
}

#[derive(Clone, Debug)]
pub struct RankerModel {
    trees: Vec<Tree>,
    pub feature_names: Vec<String>,
    average_output: bool,
    sigmoid: f32,
}

#[derive(Clone, Debug)]
struct Tree {
    split_feature: Vec<usize>,
    threshold: Vec<f32>,
    left_child: Vec<i32>,
    right_child: Vec<i32>,
    decision_type: Vec<u8>,
    default_left: Vec<bool>,
    leaf_value: Vec<f32>,
}

impl RankerModel {
    pub fn load_from_file(path: &str) -> Result<Self> {
        let expanded = expand_tilde(path);
        let contents = fs::read_to_string(&expanded)
            .with_context(|| format!("loading ranker model {}", expanded.display()))?;
        Self::parse(&contents)
            .with_context(|| format!("parsing ranker model {}", expanded.display()))
    }

    pub fn predict(&self, features: &RankerFeatures) -> f32 {
        let input = features.as_model_input();
        let mut raw: f32 = self.trees.iter().map(|tree| tree.predict(&input)).sum();
        if self.average_output && !self.trees.is_empty() {
            raw /= self.trees.len() as f32;
        }
        (1.0 / (1.0 + (-self.sigmoid * raw).exp())).clamp(0.0, 1.0)
    }

    fn parse(contents: &str) -> Result<Self> {
        let mut feature_names = FEATURE_NAMES
            .iter()
            .map(|name| name.to_string())
            .collect::<Vec<_>>();
        let mut average_output = false;
        let mut sigmoid = 1.0;
        for line in contents.lines() {
            if let Some(value) = line.strip_prefix("feature_names=") {
                feature_names = value.split_whitespace().map(str::to_owned).collect();
            } else if line == "average_output" {
                average_output = true;
            } else if let Some(value) = line.strip_prefix("sigmoid:") {
                sigmoid = value.trim().parse().unwrap_or(1.0);
            }
        }
        if feature_names != FEATURE_NAMES {
            bail!("ranker feature order does not match this build");
        }
        let mut trees = Vec::new();
        for block in contents.split("\nTree=").skip(1) {
            trees.push(Tree::parse(block)?);
        }
        if trees.is_empty() {
            bail!("model contains no trees");
        }
        Ok(Self {
            trees,
            feature_names,
            average_output,
            sigmoid,
        })
    }
}

impl Tree {
    fn parse(block: &str) -> Result<Self> {
        let values: HashMap<&str, &str> = block
            .lines()
            .filter_map(|line| line.split_once('='))
            .collect();
        let split_feature = parse_vec(values.get("split_feature").copied().unwrap_or(""))?;
        let threshold = parse_vec(values.get("threshold").copied().unwrap_or(""))?;
        let left_child = parse_vec(values.get("left_child").copied().unwrap_or(""))?;
        let right_child = parse_vec(values.get("right_child").copied().unwrap_or(""))?;
        let decision_type = parse_vec(values.get("decision_type").copied().unwrap_or(""))?;
        let default_left = decision_type
            .iter()
            .map(|value: &u8| value & 2 != 0)
            .collect();
        let leaf_value = parse_vec(values.get("leaf_value").copied().unwrap_or(""))?;
        if leaf_value.is_empty() {
            bail!("tree has no leaves");
        }
        if !split_feature.is_empty()
            && [threshold.len(), left_child.len(), right_child.len()]
                .iter()
                .any(|len| *len != split_feature.len())
        {
            bail!("tree arrays have inconsistent lengths");
        }
        Ok(Self {
            split_feature,
            threshold,
            left_child,
            right_child,
            decision_type,
            default_left,
            leaf_value,
        })
    }

    fn predict(&self, input: &[f32]) -> f32 {
        if self.split_feature.is_empty() {
            return self.leaf_value[0];
        }
        let mut node = 0_i32;
        while node >= 0 {
            let index = node as usize;
            let value = input
                .get(self.split_feature[index])
                .copied()
                .unwrap_or(f32::NAN);
            let categorical = self.decision_type.get(index).copied().unwrap_or(0) & 1 != 0;
            let go_left = if value.is_nan() {
                self.default_left.get(index).copied().unwrap_or(true)
            } else if categorical {
                (value - self.threshold[index]).abs() < f32::EPSILON
            } else {
                value <= self.threshold[index]
            };
            node = if go_left {
                self.left_child[index]
            } else {
                self.right_child[index]
            };
        }
        self.leaf_value
            .get((-node - 1) as usize)
            .copied()
            .unwrap_or(0.0)
    }
}

fn parse_vec<T: std::str::FromStr>(value: &str) -> Result<Vec<T>>
where
    T::Err: std::fmt::Display,
{
    value
        .split_whitespace()
        .map(|item| {
            item.parse()
                .map_err(|error| anyhow::anyhow!("invalid model value {item}: {error}"))
        })
        .collect()
}

fn expand_tilde(path: &str) -> PathBuf {
    if path == "~" {
        return std::env::var_os("HOME")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(path));
    }
    if let Some(suffix) = path.strip_prefix("~/") {
        if let Some(home) = std::env::var_os("HOME") {
            return PathBuf::from(home).join(suffix);
        }
    }
    PathBuf::from(path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_and_evaluates_lightgbm_text_tree() -> Result<()> {
        let model = RankerModel::parse(&format!(
            "tree\nfeature_names={}\nTree=0\nnum_leaves=2\nsplit_feature=0\nthreshold=0.5\ndecision_type=2\nleft_child=-1\nright_child=-2\nleaf_value=-2 2\n",
            FEATURE_NAMES.join(" ")
        ))?;
        let mut features = RankerFeatures::default();
        assert!(model.predict(&features) < 0.5);
        features.is_exact_prefix = true;
        assert!(model.predict(&features) > 0.5);
        Ok(())
    }
}
