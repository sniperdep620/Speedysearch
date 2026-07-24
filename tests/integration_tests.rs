use anyhow::Result;
use speedysearch::cache::{load_index, save_index, ClickstreamEntry, ClickstreamLogger};
use speedysearch::index::{
    index_applications_in, index_settings, now_secs, EntryMetadata, EntryType, IndexEntry,
    IndexSnapshot, SearchIndex, TrigramIndex,
};
use speedysearch::ipc::{self, QueryRequest, QueryResponse};
use speedysearch::stage0::QueryIntent;
use speedysearch::stage3::{apply_batch, FsEvent};
use speedysearch::{FeatureExtractor, QueryContext, RankerModel, SearchLauncher, Stage1Filter};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::UnixStream;

fn entry(id: u64, name: &str, entry_type: EntryType) -> IndexEntry {
    IndexEntry {
        id,
        entry_type,
        name: name.into(),
        path: format!("/tmp/{name}"),
        icon_path: None,
        frecency_score: 0.0,
        last_accessed: now_secs(),
        access_count: 1,
        trigrams: TrigramIndex::generate_trigrams(name),
        metadata: EntryMetadata::default(),
    }
}

#[test]
fn typo_and_exact_queries_find_document() -> Result<()> {
    let document = entry(1, "document", EntryType::File);
    let index = SearchIndex::from_snapshot(IndexSnapshot {
        entries: vec![document],
        frecency: HashMap::from([(1, (4, now_secs()))]),
        last_indexed: now_secs(),
    });
    let filter = Stage1Filter::new(index);
    assert_eq!(filter.filter("document", QueryIntent::Mixed)?, vec![1]);
    assert_eq!(filter.filter("dcument", QueryIntent::Mixed)?, vec![1]);
    assert_eq!(filter.filter("   ", QueryIntent::Mixed)?, vec![1]);
    Ok(())
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn concurrent_queries_are_safe() -> Result<()> {
    let index = SearchIndex::from_snapshot(IndexSnapshot {
        entries: vec![entry(1, "document", EntryType::File)],
        frecency: HashMap::new(),
        last_indexed: now_secs(),
    });
    let launcher = Arc::new(SearchLauncher::new(index)?);
    let mut tasks = Vec::new();
    for _ in 0..16 {
        let launcher = launcher.clone();
        tasks.push(tokio::task::spawn_blocking(move || {
            launcher.query("dcument")
        }));
    }
    for task in tasks {
        assert_eq!(
            task.await??.results.first().map(|result| result.entry.id),
            Some(1)
        );
    }
    Ok(())
}

#[test]
fn persistence_round_trip_rebuilds_derived_indexes() -> Result<()> {
    let directory = tempfile::tempdir()?;
    let path = directory.path().join("index.bin");
    let index = SearchIndex::from_snapshot(IndexSnapshot {
        entries: vec![entry(7, "Firefox", EntryType::App)],
        frecency: HashMap::new(),
        last_indexed: 10,
    });
    save_index(&index, &path)?;
    let loaded = load_index(&path)?;
    assert_eq!(
        loaded
            .app_index
            .read()
            .map_err(|_| anyhow::anyhow!("lock poisoned"))?
            .exact_index
            .get("firefox"),
        Some(&7)
    );
    Ok(())
}

#[test]
fn watcher_batch_adds_and_removes_files() -> Result<()> {
    let directory = tempfile::tempdir()?;
    let path = directory.path().join("notes.txt");
    std::fs::write(&path, "hello")?;
    let index = SearchIndex::new();
    apply_batch(&index, vec![FsEvent::Created(path.clone())])?;
    assert_eq!(index.snapshot()?.entries.len(), 1);
    std::fs::remove_file(&path)?;
    apply_batch(&index, vec![FsEvent::Removed(path)])?;
    assert!(index.snapshot()?.entries.is_empty());
    Ok(())
}

#[tokio::test]
async fn unix_socket_accepts_queries_and_reports_bad_json() -> Result<()> {
    let directory = tempfile::tempdir_in(std::env::current_dir()?)?;
    let socket = directory.path().join("search.sock");
    let index = SearchIndex::from_snapshot(IndexSnapshot {
        entries: vec![entry(1, "document", EntryType::File)],
        frecency: HashMap::new(),
        last_indexed: now_secs(),
    });
    let launcher = Arc::new(SearchLauncher::new(index)?);
    let listener = match ipc::bind(&socket) {
        Ok(listener) => listener,
        Err(error) if sandbox_denied(&error) => return Ok(()),
        Err(error) => return Err(error),
    };
    let handle = tokio::spawn(async move { ipc::serve_listener(listener, launcher).await });
    tokio::task::yield_now().await;
    let mut stream = match UnixStream::connect(&socket).await {
        Ok(stream) => stream,
        Err(error) if error.raw_os_error() == Some(1) => {
            handle.abort();
            return Ok(());
        }
        Err(error) => return Err(error.into()),
    };
    let request = serde_json::to_string(&QueryRequest {
        query: "document".into(),
    })?;
    stream
        .write_all(format!("{request}\nnot-json\n").as_bytes())
        .await?;
    let mut lines = BufReader::new(stream).lines();
    let response: QueryResponse = serde_json::from_str(
        &lines
            .next_line()
            .await?
            .ok_or_else(|| anyhow::anyhow!("missing response"))?,
    )?;
    assert_eq!(
        response
            .results
            .first()
            .map(|result| result.entry.name.as_str()),
        Some("document")
    );
    assert!(response
        .results
        .first()
        .is_some_and(|result| (0.0..=1.0).contains(&result.ml_score)));
    let error: serde_json::Value = serde_json::from_str(
        &lines
            .next_line()
            .await?
            .ok_or_else(|| anyhow::anyhow!("missing error"))?,
    )?;
    assert!(error.get("error").is_some());
    handle.abort();
    Ok(())
}

#[test]
fn feature_extraction_normalizes_all_numeric_features() {
    let mut item = entry(9, "report.pdf", EntryType::File);
    item.access_count = 42;
    item.last_accessed = 1_699_999_000;
    item.metadata.file_type = Some("pdf".into());
    let extractor = FeatureExtractor::from_entries(&[item.clone()]);
    let context = QueryContext {
        active_app: Some("org.gnome.Evince".into()),
        cwd: Some(PathBuf::from("/tmp")),
        timestamp: 1_700_000_000,
        hour_of_day: 12,
        day_of_week: 3,
    };
    let features = extractor.extract(&item, &context, "report");
    for value in [
        features.frecency,
        features.recency_hours,
        features.access_count,
        features.edit_distance,
        features.time_hour_bucket,
        features.day_of_week,
        features.file_type_popularity,
    ] {
        assert!(
            (0.0..=1.0).contains(&value),
            "feature was not normalized: {value}"
        );
    }
    assert!(features.is_exact_prefix);
    assert!(features.active_app_match);
    assert!(features.same_directory);
}

#[test]
fn missing_model_returns_an_error_without_panicking() {
    assert!(RankerModel::load_from_file("/definitely/missing/speedysearch-ranker.txt").is_err());
}

#[test]
fn clickstream_logger_writes_jsonl() -> Result<()> {
    let directory = tempfile::tempdir()?;
    let logger = ClickstreamLogger::at_path(directory.path().join("clickstream.jsonl"))?;
    logger.log(&ClickstreamEntry {
        query: "test".into(),
        selected_id: 123,
        selected_name: "Test".into(),
        timestamp: 1_700_000_000,
        context: QueryContext {
            timestamp: 1_700_000_000,
            ..QueryContext::default()
        },
        rank_position: 1,
        candidates: Vec::new(),
    })?;
    let line = std::fs::read_to_string(logger.get_path())?;
    let decoded: ClickstreamEntry = serde_json::from_str(line.trim())?;
    assert_eq!(decoded.selected_id, 123);
    assert_eq!(logger.click_count(), 1);
    Ok(())
}

#[test]
fn application_indexing_parses_desktop_files() -> Result<()> {
    let directory = tempfile::tempdir()?;
    std::fs::write(
        directory.path().join("example.desktop"),
        "[Desktop Entry]\nType=Application\nName=Example App\nExec=example\nIcon=example\n",
    )?;
    let apps = index_applications_in(&[directory.path().to_path_buf()])?;
    assert!(apps
        .iter()
        .any(|app| app.entry_type == EntryType::App && app.name == "Example App"));
    Ok(())
}

#[test]
fn aliases_and_settings_apps_are_found_by_natural_queries() -> Result<()> {
    let directory = tempfile::tempdir()?;
    std::fs::write(
        directory.path().join("code.desktop"),
        concat!(
            "[Desktop Entry]\nType=Application\nName=Visual Studio Code\n",
            "GenericName=Text Editor\nKeywords=vscode;\nExec=/usr/bin/code %F\n",
        ),
    )?;
    std::fs::write(
        directory.path().join("nvidia-settings.desktop"),
        concat!(
        "[Desktop Entry]\nType=Application\nName=NVIDIA X Server Settings\n",
        "Comment=Configure NVIDIA graphics\nCategories=System;Settings;\nExec=nvidia-settings\n",
    ),
    )?;
    let mut entries = index_applications_in(&[directory.path().to_path_buf()])?;
    entries.extend(index_settings());
    let index = SearchIndex::from_snapshot(IndexSnapshot {
        entries,
        frecency: HashMap::new(),
        last_indexed: now_secs(),
    });
    let launcher = SearchLauncher::new(index)?;

    assert_eq!(
        launcher
            .query("vs code")?
            .results
            .first()
            .map(|result| result.entry.name.as_str()),
        Some("Visual Studio Code")
    );
    assert_eq!(
        launcher
            .query("vscode")?
            .results
            .first()
            .map(|result| result.entry.name.as_str()),
        Some("Visual Studio Code")
    );
    assert_eq!(
        launcher
            .query("nvidia settings")?
            .results
            .first()
            .map(|result| result.entry.name.as_str()),
        Some("NVIDIA X Server Settings")
    );
    assert_eq!(
        launcher
            .query("general settings")?
            .results
            .first()
            .map(|result| result.entry.name.as_str()),
        Some("Settings")
    );
    Ok(())
}

#[tokio::test]
async fn binding_does_not_replace_an_active_search_service() -> Result<()> {
    let directory = tempfile::tempdir_in(std::env::current_dir()?)?;
    let socket = directory.path().join("search.sock");
    let listener = match ipc::bind(&socket) {
        Ok(listener) => listener,
        Err(error) if sandbox_denied(&error) => return Ok(()),
        Err(error) => return Err(error),
    };

    let error = match ipc::bind(&socket) {
        Ok(_) => panic!("a second daemon must not replace the active socket"),
        Err(error) if sandbox_denied(&error) => return Ok(()),
        Err(error) => error,
    };
    assert!(error.to_string().contains("already listening"));

    drop(listener);
    let replacement = ipc::bind(&socket)?;
    drop(replacement);
    Ok(())
}

fn sandbox_denied(error: &anyhow::Error) -> bool {
    error.chain().any(|cause| {
        cause
            .downcast_ref::<std::io::Error>()
            .is_some_and(|io| io.raw_os_error() == Some(1))
    })
}
