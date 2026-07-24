use crate::index::{IndexSnapshot, SearchIndex};
use crate::stage2::{QueryContext, RankerFeatures};
use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

const CACHE_HEADER: &[u8] = b"SPEEDYSEARCH01";

pub fn default_cache_dir() -> PathBuf {
    if let Some(path) = std::env::var_os("XDG_CACHE_HOME") {
        return PathBuf::from(path).join("speedysearch");
    }
    std::env::var_os("HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("/tmp"))
        .join(".cache/speedysearch")
}

pub fn default_index_path() -> PathBuf {
    default_cache_dir().join("index.bin")
}
pub fn default_model_path() -> PathBuf {
    default_cache_dir().join("ranker.txt")
}
pub fn default_clickstream_path() -> PathBuf {
    default_cache_dir().join("clickstream.jsonl")
}

pub fn default_socket_path() -> PathBuf {
    std::env::var_os("HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("/tmp"))
        .join(".cache/speedysearch.sock")
}

pub fn save_index(index: &SearchIndex, path: &Path) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).with_context(|| format!("creating {}", parent.display()))?;
    }
    let encoded = bincode::serialize(&index.snapshot()?).context("serializing index")?;
    let mut bytes = Vec::with_capacity(CACHE_HEADER.len() + encoded.len());
    bytes.extend_from_slice(CACHE_HEADER);
    bytes.extend_from_slice(&encoded);
    let temporary = path.with_extension("bin.tmp");
    fs::write(&temporary, bytes).with_context(|| format!("writing {}", temporary.display()))?;
    fs::rename(&temporary, path).with_context(|| format!("replacing {}", path.display()))?;
    Ok(())
}

pub fn load_index(path: &Path) -> Result<SearchIndex> {
    Ok(SearchIndex::from_snapshot(load_snapshot(path)?))
}

pub fn load_snapshot(path: &Path) -> Result<IndexSnapshot> {
    let bytes = fs::read(path).with_context(|| format!("reading {}", path.display()))?;
    let encoded = bytes
        .strip_prefix(CACHE_HEADER)
        .context("unsupported search index version")?;
    bincode::deserialize(encoded).context("deserializing index")
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct ClickstreamCandidate {
    pub id: u64,
    pub name: String,
    pub rank_position: u8,
    pub features: RankerFeatures,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct ClickstreamEntry {
    pub query: String,
    pub selected_id: u64,
    pub selected_name: String,
    pub timestamp: u64,
    pub context: QueryContext,
    pub rank_position: u8,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub candidates: Vec<ClickstreamCandidate>,
}

#[derive(Clone, Debug)]
pub struct ClickstreamLogger {
    file: Arc<Mutex<File>>,
    path: PathBuf,
    click_count: Arc<AtomicUsize>,
}

impl ClickstreamLogger {
    pub fn new() -> Result<Self> {
        Self::at_path(default_clickstream_path())
    }

    pub fn at_path(path: PathBuf) -> Result<Self> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).with_context(|| format!("creating {}", parent.display()))?;
        }
        let count = File::open(&path)
            .ok()
            .map(|file| BufReader::new(file).lines().count())
            .unwrap_or(0);
        let file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .with_context(|| format!("opening clickstream {}", path.display()))?;
        Ok(Self {
            file: Arc::new(Mutex::new(file)),
            path,
            click_count: Arc::new(AtomicUsize::new(count)),
        })
    }

    pub fn log(&self, entry: &ClickstreamEntry) -> Result<()> {
        let json = serde_json::to_string(entry).context("serializing clickstream entry")?;
        let mut file = self
            .file
            .lock()
            .map_err(|_| anyhow::anyhow!("clickstream lock poisoned"))?;
        writeln!(file, "{json}").context("writing clickstream entry")?;
        file.flush().context("flushing clickstream entry")?;
        self.click_count.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    pub fn get_path(&self) -> &Path {
        &self.path
    }
    pub fn click_count(&self) -> usize {
        self.click_count.load(Ordering::Relaxed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_empty_index() -> Result<()> {
        let directory = tempfile::tempdir()?;
        let path = directory.path().join("index.bin");
        save_index(&SearchIndex::new(), &path)?;
        assert!(load_index(&path)?.snapshot()?.entries.is_empty());
        Ok(())
    }
}
