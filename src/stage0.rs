use crate::index::searchable_terms;
use crate::{EntryType, IndexEntry};
use std::collections::HashSet;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum QueryIntent {
    ExactApp(String),
    FileExtension(String),
    Setting(String),
    Mixed,
}

#[derive(Clone, Debug, Default)]
pub struct IntentClassifier {
    app_names: HashSet<String>,
    setting_keywords: HashSet<String>,
    file_extensions: HashSet<String>,
}

impl IntentClassifier {
    pub fn new(entries: &[IndexEntry]) -> Self {
        let mut classifier = Self::default();
        for entry in entries {
            match entry.entry_type {
                EntryType::App => {
                    classifier
                        .app_names
                        .extend(searchable_terms(entry).map(str::to_lowercase));
                }
                EntryType::Setting => {
                    classifier.setting_keywords.extend(
                        entry
                            .name
                            .to_lowercase()
                            .split_whitespace()
                            .map(str::to_owned),
                    );
                }
                EntryType::File => {
                    if let Some(extension) = &entry.metadata.file_type {
                        classifier
                            .file_extensions
                            .insert(extension.trim_start_matches('.').to_lowercase());
                    }
                }
                EntryType::Command => {}
            }
        }
        classifier
    }

    pub fn with_known_values(apps: &[&str], settings: &[&str], extensions: &[&str]) -> Self {
        Self {
            app_names: apps.iter().map(|value| value.to_lowercase()).collect(),
            setting_keywords: settings.iter().map(|value| value.to_lowercase()).collect(),
            file_extensions: extensions
                .iter()
                .map(|value| value.trim_start_matches('.').to_lowercase())
                .collect(),
        }
    }

    pub fn classify(&self, query: &str) -> QueryIntent {
        let lower = query.trim().to_lowercase();
        if self.app_names.contains(&lower) {
            return QueryIntent::ExactApp(lower);
        }
        if let Some((_, extension)) = lower.rsplit_once('.') {
            if self.file_extensions.contains(extension) {
                return QueryIntent::FileExtension(extension.to_owned());
            }
        }
        if let Some(keyword) = self
            .setting_keywords
            .iter()
            .filter(|word| lower.contains(word.as_str()))
            .min()
        {
            return QueryIntent::Setting(keyword.clone());
        }
        QueryIntent::Mixed
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_supported_intents() {
        let classifier =
            IntentClassifier::with_known_values(&["Firefox"], &["bluetooth"], &["pdf"]);
        assert_eq!(
            classifier.classify("firefox"),
            QueryIntent::ExactApp("firefox".into())
        );
        assert_eq!(
            classifier.classify("notes.pdf"),
            QueryIntent::FileExtension("pdf".into())
        );
        assert_eq!(
            classifier.classify("open bluetooth please"),
            QueryIntent::Setting("bluetooth".into())
        );
        assert_eq!(classifier.classify("something"), QueryIntent::Mixed);
    }
}
