use anyhow::{Context, Result};
use fasthash::xx::hash64;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::ffi::OsStr;
use std::fs;
use std::os::unix::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, RwLock};
use std::time::{SystemTime, UNIX_EPOCH};
use walkdir::{DirEntry, WalkDir};

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq, Hash)]
pub enum EntryType {
    App,
    File,
    Setting,
    Command,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default, PartialEq)]
pub struct EntryMetadata {
    pub file_type: Option<String>,
    pub file_size: Option<u64>,
    pub is_dir: bool,
    pub tags: Vec<String>,
    pub category: Option<String>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct IndexEntry {
    pub id: u64,
    pub entry_type: EntryType,
    pub name: String,
    pub path: String,
    pub icon_path: Option<String>,
    pub frecency_score: f32,
    pub last_accessed: u64,
    pub access_count: u32,
    #[serde(skip)]
    pub trigrams: Vec<u32>,
    pub metadata: EntryMetadata,
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct TrigramIndex {
    pub trigrams: HashMap<u32, Vec<u64>>,
    pub exact_index: HashMap<String, u64>,
    pub entry_trigrams: HashMap<u64, Vec<u32>>,
}

impl TrigramIndex {
    pub fn generate_trigrams(input: &str) -> Vec<u32> {
        let normalized = format!("  {}  ", input.to_lowercase());
        let chars: Vec<char> = normalized.chars().collect();
        let mut hashes: Vec<u32> = chars
            .windows(3)
            .map(|window| {
                let mut bytes = [0; 12];
                let mut length = 0;
                for character in window {
                    length += character.encode_utf8(&mut bytes[length..]).len();
                }
                hash64(&bytes[..length]) as u32
            })
            .collect();
        hashes.sort_unstable();
        hashes.dedup();
        hashes
    }

    pub fn overlap_ratio(query: &[u32], entry: &[u32]) -> f32 {
        if query.is_empty() {
            return 0.0;
        }
        let (mut query_position, mut entry_position, mut matches) = (0, 0, 0);
        while query_position < query.len() && entry_position < entry.len() {
            match query[query_position].cmp(&entry[entry_position]) {
                std::cmp::Ordering::Less => query_position += 1,
                std::cmp::Ordering::Greater => entry_position += 1,
                std::cmp::Ordering::Equal => {
                    matches += 1;
                    query_position += 1;
                    entry_position += 1;
                }
            }
        }
        matches as f32 / query.len() as f32
    }

    pub fn insert(&mut self, entry: &IndexEntry) {
        for term in searchable_terms(entry) {
            self.exact_index
                .insert(term.trim().to_lowercase(), entry.id);
        }
        self.entry_trigrams.insert(entry.id, entry.trigrams.clone());
        for &tri in &entry.trigrams {
            self.trigrams.entry(tri).or_default().push(entry.id);
        }
    }

    pub fn remove(&mut self, id: u64) {
        self.exact_index.retain(|_, value| *value != id);
        self.entry_trigrams.remove(&id);
        self.trigrams.retain(|_, postings| {
            postings.retain(|entry_id| *entry_id != id);
            !postings.is_empty()
        });
    }

    pub fn search_by_trigrams(&self, query: &str, min_overlap: f32, limit: usize) -> Vec<u64> {
        let query_trigrams = Self::generate_trigrams(query);
        if query_trigrams.is_empty() {
            return Vec::new();
        }
        let required = ((query_trigrams.len() as f32 * min_overlap).ceil() as usize)
            .clamp(1, query_trigrams.len());
        let seed_count = query_trigrams.len() - required + 1;
        let mut seeds: Vec<(u32, usize)> = query_trigrams
            .iter()
            .map(|tri| (*tri, self.trigrams.get(tri).map_or(0, Vec::len)))
            .collect();
        seeds.sort_by_key(|(_, posting_count)| *posting_count);
        let mut seen = HashSet::new();
        for (tri, _) in seeds.into_iter().take(seed_count) {
            if let Some(postings) = self.trigrams.get(&tri) {
                seen.extend(postings.iter().copied());
            }
        }
        let mut scored = Vec::new();
        for id in seen {
            if let Some(entry_tris) = self.entry_trigrams.get(&id) {
                let overlap = Self::overlap_ratio(&query_trigrams, entry_tris);
                if overlap >= min_overlap {
                    scored.push((id, overlap));
                }
            }
        }
        scored.sort_by(|a, b| b.1.total_cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
        scored.into_iter().take(limit).map(|(id, _)| id).collect()
    }
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
pub struct IndexSnapshot {
    pub entries: Vec<IndexEntry>,
    pub frecency: HashMap<u64, (u32, u64)>,
    pub last_indexed: u64,
}

#[derive(Clone, Debug)]
pub struct SearchIndex {
    pub entries: Arc<RwLock<Vec<IndexEntry>>>,
    pub id_positions: Arc<RwLock<HashMap<u64, usize>>>,
    pub app_index: Arc<RwLock<TrigramIndex>>,
    pub file_index: Arc<RwLock<TrigramIndex>>,
    pub setting_index: Arc<RwLock<TrigramIndex>>,
    pub frecency: Arc<RwLock<HashMap<u64, (u32, u64)>>>,
    pub last_indexed: Arc<Mutex<u64>>,
}

impl Default for SearchIndex {
    fn default() -> Self {
        Self::new()
    }
}

impl SearchIndex {
    pub fn new() -> Self {
        Self::from_snapshot(IndexSnapshot::default())
    }

    pub fn from_snapshot(mut snapshot: IndexSnapshot) -> Self {
        hydrate_trigrams(&mut snapshot.entries);
        let index = Self {
            entries: Arc::new(RwLock::new(snapshot.entries)),
            id_positions: Arc::new(RwLock::new(HashMap::new())),
            app_index: Arc::new(RwLock::new(TrigramIndex::default())),
            file_index: Arc::new(RwLock::new(TrigramIndex::default())),
            setting_index: Arc::new(RwLock::new(TrigramIndex::default())),
            frecency: Arc::new(RwLock::new(snapshot.frecency)),
            last_indexed: Arc::new(Mutex::new(snapshot.last_indexed)),
        };
        index.rebuild_indices();
        index
    }

    pub fn replace_snapshot(&self, mut snapshot: IndexSnapshot) -> Result<()> {
        hydrate_trigrams(&mut snapshot.entries);
        *self
            .entries
            .write()
            .map_err(|_| anyhow::anyhow!("entries lock poisoned"))? = snapshot.entries;
        *self
            .frecency
            .write()
            .map_err(|_| anyhow::anyhow!("frecency lock poisoned"))? = snapshot.frecency;
        *self
            .last_indexed
            .lock()
            .map_err(|_| anyhow::anyhow!("timestamp lock poisoned"))? = snapshot.last_indexed;
        self.rebuild_indices();
        Ok(())
    }

    pub fn snapshot(&self) -> Result<IndexSnapshot> {
        Ok(IndexSnapshot {
            entries: self
                .entries
                .read()
                .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?
                .clone(),
            frecency: self
                .frecency
                .read()
                .map_err(|_| anyhow::anyhow!("frecency lock poisoned"))?
                .clone(),
            last_indexed: *self
                .last_indexed
                .lock()
                .map_err(|_| anyhow::anyhow!("timestamp lock poisoned"))?,
        })
    }

    pub fn rebuild_indices(&self) {
        let Ok(entries) = self.entries.read() else {
            return;
        };
        let (Ok(mut positions), Ok(mut apps), Ok(mut files), Ok(mut settings)) = (
            self.id_positions.write(),
            self.app_index.write(),
            self.file_index.write(),
            self.setting_index.write(),
        ) else {
            return;
        };
        positions.clear();
        *apps = TrigramIndex::default();
        *files = TrigramIndex::default();
        *settings = TrigramIndex::default();
        for (position, entry) in entries.iter().enumerate() {
            positions.insert(entry.id, position);
            match entry.entry_type {
                EntryType::App => apps.insert(entry),
                EntryType::File => files.insert(entry),
                EntryType::Setting | EntryType::Command => settings.insert(entry),
            }
        }
    }

    pub fn upsert(&self, entry: IndexEntry) -> Result<()> {
        let mut entries = self
            .entries
            .write()
            .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?;
        if let Some(position) = entries.iter().position(|existing| existing.id == entry.id) {
            entries[position] = entry;
        } else {
            entries.push(entry);
        }
        drop(entries);
        self.rebuild_indices();
        self.touch_indexed()
    }

    pub fn remove_path(&self, path: &Path) -> Result<()> {
        let path_text = path.to_string_lossy();
        self.entries
            .write()
            .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?
            .retain(|entry| entry.path != path_text);
        self.rebuild_indices();
        self.touch_indexed()
    }

    pub fn add_file(&self, path: &Path) -> Result<()> {
        if !path.exists() {
            return Ok(());
        }
        self.upsert(file_entry(path)?)
    }

    pub fn touch_indexed(&self) -> Result<()> {
        *self
            .last_indexed
            .lock()
            .map_err(|_| anyhow::anyhow!("timestamp lock poisoned"))? = now_secs();
        Ok(())
    }

    pub fn record_access(&self, id: u64, timestamp: u64) -> Result<()> {
        let mut entries = self
            .entries
            .write()
            .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?;
        let entry = entries
            .iter_mut()
            .find(|entry| entry.id == id)
            .ok_or_else(|| anyhow::anyhow!("entry {id} is no longer indexed"))?;
        entry.access_count = entry.access_count.saturating_add(1);
        entry.last_accessed = timestamp;
        entry.frecency_score = entry.access_count as f32;
        self.frecency
            .write()
            .map_err(|_| anyhow::anyhow!("frecency lock poisoned"))?
            .insert(id, (entry.access_count, timestamp));
        Ok(())
    }

    pub fn discover_files(&self, roots: &[PathBuf], excludes: &[String]) -> Result<()> {
        let mut unique = HashSet::new();
        let mut discovered = Vec::new();
        for root in roots {
            if !root.exists() {
                continue;
            }
            for item in WalkDir::new(root)
                .follow_links(false)
                .into_iter()
                .filter_entry(|e| allowed(e, excludes))
            {
                let entry = match item {
                    Ok(entry) => entry,
                    Err(_) => continue,
                };
                let path = entry.path();
                if unique.insert(path.to_path_buf()) {
                    if let Ok(index_entry) = file_entry(path) {
                        discovered.push(index_entry);
                    }
                }
            }
        }
        self.merge_entries(discovered)?;
        self.touch_indexed()
    }

    pub fn discover_desktop_entries(&self, dirs: &[PathBuf]) -> Result<()> {
        let mut discovered = Vec::new();
        for dir in dirs {
            let items = match fs::read_dir(dir) {
                Ok(items) => items,
                Err(_) => continue,
            };
            for item in items.flatten() {
                let path = item.path();
                if path.extension() != Some(OsStr::new("desktop")) {
                    continue;
                }
                if let Some(entry) = parse_desktop_entry(&path)? {
                    discovered.push(entry);
                }
            }
        }
        self.merge_entries(discovered)?;
        self.touch_indexed()
    }

    pub fn merge_entries(&self, discovered: Vec<IndexEntry>) -> Result<()> {
        let mut entries = self
            .entries
            .write()
            .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?;
        let mut positions: HashMap<u64, usize> = entries
            .iter()
            .enumerate()
            .map(|(position, entry)| (entry.id, position))
            .collect();
        for entry in discovered {
            if let Some(position) = positions.get(&entry.id).copied() {
                entries[position] = entry;
            } else {
                positions.insert(entry.id, entries.len());
                entries.push(entry);
            }
        }
        drop(entries);
        self.rebuild_indices();
        Ok(())
    }

    pub fn replace_catalog_entries(&self, discovered: Vec<IndexEntry>) -> Result<()> {
        let mut entries = self
            .entries
            .write()
            .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?;
        entries.retain(|entry| matches!(entry.entry_type, EntryType::File | EntryType::Command));
        let mut seen: HashSet<u64> = entries.iter().map(|entry| entry.id).collect();
        let mut catalog_names = HashSet::new();
        entries.extend(discovered.into_iter().filter(|entry| {
            seen.insert(entry.id)
                && catalog_names.insert((entry.entry_type.clone(), entry.name.to_lowercase()))
        }));
        drop(entries);
        self.rebuild_indices();
        self.touch_indexed()
    }
}

pub fn index_applications() -> Result<Vec<IndexEntry>> {
    let home = std::env::var_os("HOME").map(PathBuf::from);
    let mut dirs = Vec::new();
    if let Some(home) = home {
        dirs.push(home.join(".local/share/applications"));
    }
    dirs.push(PathBuf::from("/usr/share/applications"));
    if let Some(data_dirs) = std::env::var_os("XDG_DATA_DIRS") {
        dirs.extend(std::env::split_paths(&data_dirs).map(|path| path.join("applications")));
    }
    index_applications_in(&dirs)
}

pub fn index_applications_in(dirs: &[PathBuf]) -> Result<Vec<IndexEntry>> {
    let mut entries = Vec::new();
    let mut seen = HashSet::new();
    for dir in dirs {
        if !dir.exists() {
            continue;
        }
        for item in WalkDir::new(dir)
            .follow_links(false)
            .max_depth(4)
            .into_iter()
            .filter_map(|item| item.ok())
        {
            let path = item.path();
            if path.extension() != Some(OsStr::new("desktop")) {
                continue;
            }
            if let Some(entry) = parse_desktop_entry(path)? {
                if seen.insert(entry.id) {
                    entries.push(entry);
                }
            }
        }
    }
    Ok(entries)
}

pub fn index_settings() -> Vec<IndexEntry> {
    const SETTINGS: &[(&str, &str, &str)] = &[
        ("Settings", "com.system76.CosmicSettings", "System"),
        (
            "Accessibility",
            "com.system76.CosmicSettings.Accessibility",
            "System",
        ),
        (
            "Appearance",
            "com.system76.CosmicSettings.Appearance",
            "Desktop",
        ),
        (
            "Applications",
            "com.system76.CosmicSettings.Applications",
            "System",
        ),
        (
            "Background",
            "com.system76.CosmicSettings.Wallpaper",
            "Desktop",
        ),
        (
            "Bluetooth",
            "com.system76.CosmicSettings.Bluetooth",
            "Connections",
        ),
        ("Date & Time", "com.system76.CosmicSettings.Time", "System"),
        (
            "Default Applications",
            "com.system76.CosmicSettings.DefaultApps",
            "System",
        ),
        ("Desktop", "com.system76.CosmicSettings.Desktop", "Desktop"),
        (
            "Displays",
            "com.system76.CosmicSettings.Displays",
            "Hardware",
        ),
        ("Keyboard", "com.system76.CosmicSettings.Keyboard", "Input"),
        (
            "Mouse & Touchpad",
            "com.system76.CosmicSettings.Input",
            "Input",
        ),
        (
            "Network",
            "com.system76.CosmicSettings.Network",
            "Connections",
        ),
        (
            "Notifications",
            "com.system76.CosmicSettings.Notifications",
            "System",
        ),
        ("Panel", "com.system76.CosmicSettings.Panel", "Desktop"),
        ("Power", "com.system76.CosmicSettings.Power", "System"),
        (
            "Region & Language",
            "com.system76.CosmicSettings.RegionLanguage",
            "System",
        ),
        ("Sound", "com.system76.CosmicSettings.Sound", "Hardware"),
        (
            "Startup Applications",
            "com.system76.CosmicSettings.StartupApps",
            "System",
        ),
        ("Users", "com.system76.CosmicSettings.Users", "System"),
        (
            "Window Management",
            "com.system76.CosmicSettings.WindowManagement",
            "Desktop",
        ),
        (
            "Workspaces",
            "com.system76.CosmicSettings.Workspaces",
            "Desktop",
        ),
    ];
    SETTINGS
        .iter()
        .map(|(name, desktop_id, category)| {
            let source = Path::new(desktop_id);
            let mut entry = IndexEntry {
                id: stable_id(&EntryType::Setting, source),
                entry_type: EntryType::Setting,
                name: (*name).to_string(),
                path: (*desktop_id).to_string(),
                icon_path: Some("preferences-system".into()),
                frecency_score: 0.0,
                last_accessed: 0,
                access_count: 0,
                trigrams: Vec::new(),
                metadata: EntryMetadata {
                    tags: common_aliases(name),
                    category: Some((*category).to_string()),
                    ..EntryMetadata::default()
                },
            };
            entry.trigrams = search_trigrams(&entry);
            entry
        })
        .collect()
}

pub fn stable_id(entry_type: &EntryType, source: &Path) -> u64 {
    let prefix = match entry_type {
        EntryType::App => 1,
        EntryType::File => 2,
        EntryType::Setting => 3,
        EntryType::Command => 4,
    };
    let mut bytes = vec![prefix];
    bytes.extend_from_slice(source.as_os_str().as_bytes());
    hash64(&bytes)
}

pub fn file_entry(path: &Path) -> Result<IndexEntry> {
    let metadata = fs::symlink_metadata(path)
        .with_context(|| format!("reading metadata for {}", path.display()))?;
    let name = path
        .file_name()
        .unwrap_or(path.as_os_str())
        .to_string_lossy()
        .into_owned();
    Ok(IndexEntry {
        id: stable_id(&EntryType::File, path),
        entry_type: EntryType::File,
        name: name.clone(),
        path: path.to_string_lossy().into_owned(),
        icon_path: None,
        frecency_score: 0.0,
        last_accessed: 0,
        access_count: 0,
        trigrams: TrigramIndex::generate_trigrams(&name),
        metadata: EntryMetadata {
            file_type: path
                .extension()
                .map(|ext| ext.to_string_lossy().to_lowercase()),
            file_size: Some(metadata.len()),
            is_dir: metadata.is_dir(),
            tags: Vec::new(),
            category: None,
        },
    })
}

pub fn parse_desktop_entry(path: &Path) -> Result<Option<IndexEntry>> {
    let contents = match fs::read_to_string(path) {
        Ok(value) => value,
        Err(_) => return Ok(None),
    };
    let mut in_section = false;
    let mut values = HashMap::new();
    for line in contents.lines() {
        let line = line.trim();
        if line.starts_with('[') {
            in_section = line == "[Desktop Entry]";
            continue;
        }
        if !in_section || line.starts_with('#') {
            continue;
        }
        if let Some((key, value)) = line.split_once('=') {
            values.entry(key).or_insert(value);
        }
    }
    if values.get("Type").copied().unwrap_or("Application") != "Application"
        || values.get("Hidden") == Some(&"true")
    {
        return Ok(None);
    }
    let Some(name) = values.get("Name").map(|value| value.to_string()) else {
        return Ok(None);
    };
    let desktop_id = path.file_stem().unwrap_or_default().to_string_lossy();
    let is_cosmic_setting = desktop_id.starts_with("com.system76.CosmicSettings");
    if values.get("NoDisplay") == Some(&"true") && !is_cosmic_setting {
        return Ok(None);
    }
    let is_settings_category = values.get("Categories").is_some_and(|categories| {
        categories
            .split(';')
            .any(|category| category.eq_ignore_ascii_case("Settings"))
    });
    let entry_type = if is_cosmic_setting || is_settings_category {
        EntryType::Setting
    } else {
        EntryType::App
    };
    let mut tags = Vec::new();
    for key in ["GenericName", "Comment", "Keywords"] {
        if let Some(value) = values.get(key) {
            tags.extend(
                value
                    .split(';')
                    .map(str::trim)
                    .filter(|tag| !tag.is_empty())
                    .map(str::to_owned),
            );
        }
    }
    if let Some(executable) = values
        .get("Exec")
        .and_then(|exec| exec.split_whitespace().next())
    {
        let executable = Path::new(executable.trim_matches(['\'', '"']))
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .replace(['-', '_'], " ");
        if !executable.is_empty() {
            tags.push(executable);
        }
    }
    tags.push(desktop_id.replace(['-', '_', '.'], " "));
    tags.extend(common_aliases(&name));
    deduplicate_tags(&mut tags, &name);
    let mut entry = IndexEntry {
        id: stable_id(&entry_type, path),
        entry_type,
        name: name.clone(),
        path: desktop_id.into_owned(),
        icon_path: values.get("Icon").map(|value| value.to_string()),
        frecency_score: 0.0,
        last_accessed: 0,
        access_count: 0,
        trigrams: Vec::new(),
        metadata: EntryMetadata {
            tags,
            category: values.get("Categories").map(|value| value.to_string()),
            ..EntryMetadata::default()
        },
    };
    entry.trigrams = search_trigrams(&entry);
    Ok(Some(entry))
}

pub fn searchable_terms(entry: &IndexEntry) -> impl Iterator<Item = &str> {
    std::iter::once(entry.name.as_str()).chain(entry.metadata.tags.iter().map(String::as_str))
}

pub fn search_trigrams(entry: &IndexEntry) -> Vec<u32> {
    let mut trigrams: Vec<u32> = searchable_terms(entry)
        .flat_map(TrigramIndex::generate_trigrams)
        .collect();
    trigrams.sort_unstable();
    trigrams.dedup();
    trigrams
}

fn common_aliases(name: &str) -> Vec<String> {
    let words: Vec<&str> = name
        .split(|character: char| !character.is_alphanumeric())
        .filter(|word| !word.is_empty())
        .collect();
    let mut aliases = Vec::new();
    if words.len() >= 2 {
        aliases.push(
            words
                .iter()
                .filter_map(|word| word.chars().next())
                .collect(),
        );
    }
    if words.len() >= 3 {
        let initials: String = words[..words.len() - 1]
            .iter()
            .filter_map(|word| word.chars().next())
            .collect();
        aliases.push(format!("{initials} {}", words[words.len() - 1]));
    }

    let lower = name.to_lowercase();
    const KNOWN: &[(&str, &[&str])] = &[
        ("visual studio code", &["vs code", "vscode", "code editor"]),
        (
            "nvidia x server settings",
            &["nvidia settings", "gpu settings", "graphics settings"],
        ),
        ("system monitor", &["task manager", "process manager"]),
        ("file manager", &["files", "explorer"]),
        ("terminal", &["console", "shell"]),
        ("libreoffice writer", &["word processor", "writer"]),
        ("libreoffice calc", &["spreadsheet", "excel", "calc"]),
        ("mozilla firefox", &["firefox"]),
        ("google chrome", &["chrome"]),
    ];
    for (needle, values) in KNOWN {
        if lower.contains(needle) {
            aliases.extend(values.iter().map(|value| (*value).to_string()));
        }
    }
    if matches!(lower.as_str(), "settings" | "cosmic settings") {
        aliases.extend(
            [
                "preferences",
                "control panel",
                "system settings",
                "general settings",
            ]
            .into_iter()
            .map(str::to_owned),
        );
    }
    aliases
}

fn deduplicate_tags(tags: &mut Vec<String>, name: &str) {
    let mut seen = HashSet::from([name.trim().to_lowercase()]);
    tags.retain(|tag| seen.insert(tag.trim().to_lowercase()) && !tag.trim().is_empty());
}

fn allowed(entry: &DirEntry, excludes: &[String]) -> bool {
    if entry.depth() > 0 && entry.file_name().to_string_lossy().starts_with('.') {
        return false;
    }
    entry.path().components().all(|part| {
        let name = part.as_os_str().to_string_lossy();
        !excludes.iter().any(|excluded| name == excluded.as_str())
    })
}

fn hydrate_trigrams(entries: &mut [IndexEntry]) {
    for entry in entries.iter_mut().filter(|entry| entry.trigrams.is_empty()) {
        entry.trigrams = search_trigrams(entry);
    }
}

pub fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn containment_overlap_matches_embedded_name() {
        let query = TrigramIndex::generate_trigrams("document");
        let entry = TrigramIndex::generate_trigrams("important_document");
        assert!(TrigramIndex::overlap_ratio(&query, &entry) > 0.7);
    }

    #[test]
    fn unicode_trigrams_are_stable() {
        assert_eq!(
            TrigramIndex::generate_trigrams("Résumé"),
            TrigramIndex::generate_trigrams("résumé")
        );
    }

    #[test]
    fn desktop_entries_are_classified() -> Result<()> {
        let directory = tempfile::tempdir()?;
        let app = directory.path().join("org.example.App.desktop");
        let setting = directory
            .path()
            .join("com.system76.CosmicSettings.Sound.desktop");
        std::fs::write(
            &app,
            "[Desktop Entry]\nType=Application\nName=Example\nIcon=example\n",
        )?;
        std::fs::write(&setting, "[Desktop Entry]\nType=Application\nName=Sound\n")?;
        assert_eq!(
            parse_desktop_entry(&app)?.map(|entry| entry.entry_type),
            Some(EntryType::App)
        );
        assert_eq!(
            parse_desktop_entry(&setting)?.map(|entry| entry.entry_type),
            Some(EntryType::Setting)
        );
        Ok(())
    }

    #[test]
    fn desktop_metadata_and_common_abbreviations_are_searchable() -> Result<()> {
        let directory = tempfile::tempdir()?;
        let path = directory.path().join("code.desktop");
        std::fs::write(
            &path,
            concat!(
                "[Desktop Entry]\nType=Application\nName=Visual Studio Code\n",
                "GenericName=Text Editor\nKeywords=vscode;development;\nExec=/usr/bin/code %F\n",
            ),
        )?;
        let entry = parse_desktop_entry(&path)?.expect("desktop entry");
        assert!(entry
            .metadata
            .tags
            .iter()
            .any(|tag| tag.eq_ignore_ascii_case("vs code")));
        assert!(entry.metadata.tags.iter().any(|tag| tag == "vscode"));
        let query = TrigramIndex::generate_trigrams("vs code");
        assert_eq!(TrigramIndex::overlap_ratio(&query, &entry.trigrams), 1.0);
        Ok(())
    }

    #[test]
    fn visible_settings_apps_are_classified_as_settings() -> Result<()> {
        let directory = tempfile::tempdir()?;
        let path = directory.path().join("nvidia-settings.desktop");
        std::fs::write(
            &path,
            concat!(
                "[Desktop Entry]\nType=Application\nName=NVIDIA X Server Settings\n",
                "Categories=System;Settings;\nExec=nvidia-settings\n",
            ),
        )?;
        let entry = parse_desktop_entry(&path)?.expect("desktop entry");
        assert_eq!(entry.entry_type, EntryType::Setting);
        assert!(entry
            .metadata
            .tags
            .iter()
            .any(|tag| tag == "nvidia settings"));
        Ok(())
    }

    #[test]
    fn hidden_cosmic_panels_are_kept_but_hidden_apps_are_not() -> Result<()> {
        let directory = tempfile::tempdir()?;
        let setting = directory
            .path()
            .join("com.system76.CosmicSettings.Keyboard.desktop");
        let app = directory.path().join("background-helper.desktop");
        let contents = "[Desktop Entry]\nType=Application\nName=Keyboard\nNoDisplay=true\n";
        std::fs::write(&setting, contents)?;
        std::fs::write(&app, contents)?;
        assert!(parse_desktop_entry(&setting)?.is_some());
        assert!(parse_desktop_entry(&app)?.is_none());
        Ok(())
    }
}
