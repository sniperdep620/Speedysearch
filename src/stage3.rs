use crate::cache::save_index;
use crate::index::{file_entry, parse_desktop_entry, stable_id, EntryType, SearchIndex};
use crate::launcher::SearchLauncher;
use anyhow::{Context, Result};
use notify::event::{ModifyKind, RenameMode};
use notify::{Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, RecvTimeoutError, Sender};
use std::sync::Arc;
use std::thread::{self, JoinHandle};
use std::time::Duration;

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum FsEvent {
    Created(PathBuf),
    Modified(PathBuf),
    Removed(PathBuf),
}

#[derive(Debug)]
pub struct FileWatcher {
    shutdown: Sender<()>,
    handle: Option<JoinHandle<()>>,
}

impl FileWatcher {
    pub fn start(
        paths: Vec<PathBuf>,
        exclude_patterns: Vec<String>,
        launcher: Arc<SearchLauncher>,
        cache_path: PathBuf,
        batch_interval: Duration,
    ) -> Result<Self> {
        let paths: Vec<PathBuf> = paths.into_iter().filter(|path| path.exists()).collect();
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let (shutdown_tx, shutdown_rx) = mpsc::channel();
        let handle = thread::Builder::new()
            .name("speedysearch-watcher".into())
            .spawn(move || {
                let (event_tx, event_rx) = mpsc::channel::<notify::Result<Event>>();
                let watcher_result: notify::Result<RecommendedWatcher> =
                    notify::recommended_watcher(move |event| {
                        let _ = event_tx.send(event);
                    });
                let mut watcher = match watcher_result {
                    Ok(watcher) => watcher,
                    Err(error) => {
                        let _ = ready_tx.send(Err(error.to_string()));
                        return;
                    }
                };
                for path in &paths {
                    if let Err(error) = watcher.watch(path, RecursiveMode::Recursive) {
                        let _ = ready_tx.send(Err(format!("watching {}: {error}", path.display())));
                        return;
                    }
                }
                let _ = ready_tx.send(Ok(()));
                loop {
                    if shutdown_rx.try_recv().is_ok() {
                        break;
                    }
                    let mut batch = Vec::new();
                    match event_rx.recv_timeout(batch_interval) {
                        Ok(Ok(event)) => {
                            batch.extend(translate_event(event, &paths, &exclude_patterns))
                        }
                        Ok(Err(_)) | Err(RecvTimeoutError::Timeout) => {}
                        Err(RecvTimeoutError::Disconnected) => break,
                    }
                    while let Ok(Ok(event)) = event_rx.try_recv() {
                        batch.extend(translate_event(event, &paths, &exclude_patterns));
                    }
                    if !batch.is_empty() {
                        let _ = apply_batch(&launcher.index, batch);
                        let _ = launcher.refresh_classifier();
                        let _ = save_index(&launcher.index, &cache_path);
                    }
                }
            })
            .context("spawning file watcher")?;
        match ready_rx
            .recv()
            .context("waiting for file watcher startup")?
        {
            Ok(()) => Ok(Self {
                shutdown: shutdown_tx,
                handle: Some(handle),
            }),
            Err(message) => {
                let _ = handle.join();
                Err(anyhow::anyhow!(message))
            }
        }
    }
}

impl Drop for FileWatcher {
    fn drop(&mut self) {
        let _ = self.shutdown.send(());
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

fn translate_event(event: Event, roots: &[PathBuf], exclude_patterns: &[String]) -> Vec<FsEvent> {
    let events = match event.kind {
        EventKind::Create(_) => event.paths.into_iter().map(FsEvent::Created).collect(),
        EventKind::Remove(_) => event.paths.into_iter().map(FsEvent::Removed).collect(),
        EventKind::Modify(ModifyKind::Name(RenameMode::Both)) if event.paths.len() >= 2 => vec![
            FsEvent::Removed(event.paths[0].clone()),
            FsEvent::Created(event.paths[1].clone()),
        ],
        EventKind::Modify(_) => event.paths.into_iter().map(FsEvent::Modified).collect(),
        _ => Vec::new(),
    };
    events
        .into_iter()
        .filter(|event| {
            let path = match event {
                FsEvent::Created(path) | FsEvent::Modified(path) | FsEvent::Removed(path) => path,
            };
            roots.iter().any(|root| {
                path.strip_prefix(root).is_ok_and(|relative| {
                    relative.components().all(|component| {
                        let name = component.as_os_str().to_string_lossy();
                        !name.starts_with('.')
                            && !exclude_patterns
                                .iter()
                                .any(|excluded| name == excluded.as_str())
                    })
                })
            })
        })
        .collect()
}

pub fn apply_batch(index: &SearchIndex, events: Vec<FsEvent>) -> Result<()> {
    let mut entries = index
        .entries
        .write()
        .map_err(|_| anyhow::anyhow!("entries lock poisoned"))?;
    for event in events {
        match event {
            FsEvent::Removed(path) => remove_tree(&mut entries, &path),
            FsEvent::Created(path) | FsEvent::Modified(path) => {
                remove_tree(&mut entries, &path);
                if path.exists() {
                    if path.extension().and_then(|value| value.to_str()) == Some("desktop") {
                        if let Ok(Some(entry)) = parse_desktop_entry(&path) {
                            entries.push(entry);
                        }
                    } else if let Ok(entry) = file_entry(&path) {
                        entries.push(entry);
                    }
                }
            }
        }
    }
    drop(entries);
    index.rebuild_indices();
    index.touch_indexed()
}

fn remove_tree(entries: &mut Vec<crate::IndexEntry>, path: &Path) {
    let value = path.to_string_lossy();
    let app_id = stable_id(&EntryType::App, path);
    let setting_id = stable_id(&EntryType::Setting, path);
    entries.retain(|entry| {
        entry.id != app_id
            && entry.id != setting_id
            && entry.path != value
            && !Path::new(&entry.path).starts_with(path)
    });
}
