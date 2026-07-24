use anyhow::{Context, Result};
use speedysearch::cache::{default_index_path, default_socket_path, load_snapshot, save_index};
use speedysearch::config::{default_config_path, Config};
use speedysearch::index::{index_applications_in, index_settings, SearchIndex};
use speedysearch::ipc;
use speedysearch::stage3::FileWatcher;
use speedysearch::FeatureOptions;
use speedysearch::SearchLauncher;
use std::collections::HashSet;
use std::path::PathBuf;
use std::sync::Arc;

fn main() -> Result<()> {
    if std::env::args().any(|argument| argument == "--check-model") {
        let home = std::env::var_os("HOME")
            .map(PathBuf::from)
            .context("HOME is not set")?;
        let config = Config::load(&default_config_path(&home))?;
        let Some(path) = config.model_path(&home) else {
            println!("Stage 2 model is disabled; frecency fallback is active");
            return Ok(());
        };
        speedysearch::RankerModel::load_from_file(&path.to_string_lossy())?;
        println!("Stage 2 model loaded: {}", path.display());
        return Ok(());
    }
    if std::env::args().any(|argument| argument == "--daemon") {
        return tokio::runtime::Runtime::new()?.block_on(run_daemon());
    }
    speedysearch::gui::run(default_socket_path())
}

async fn run_daemon() -> Result<()> {
    let cache_path = default_index_path();
    let home = std::env::var_os("HOME")
        .map(PathBuf::from)
        .context("HOME is not set")?;
    let config = Config::load(&default_config_path(&home))?;
    let file_roots = config.watch_paths(&home);
    let desktop_dirs = desktop_dirs(&home);

    let index = SearchIndex::new();
    let model_path = config.model_path(&home);
    let launcher = Arc::new(SearchLauncher::with_options(
        index.clone(),
        config.performance.max_stage1_candidates,
        model_path.as_deref(),
        config.ranking.enable_clickstream,
    )?);
    launcher.set_feature_options(FeatureOptions {
        use_time_of_day: config.features.use_time_of_day,
        use_active_app: config.features.use_active_app,
        use_working_directory: config.features.use_working_directory,
        use_file_type_popularity: config.features.use_file_type_popularity,
    })?;
    // Serve an empty index immediately while the persisted index warms in the background.
    let listener = ipc::bind(&default_socket_path())?;
    let server_launcher = launcher.clone();
    let server = tokio::spawn(async move { ipc::serve_listener(listener, server_launcher).await });

    let build_index = index.clone();
    let build_launcher = launcher.clone();
    let roots = file_roots.clone();
    let desktops = desktop_dirs.clone();
    let cache = cache_path.clone();
    let excludes = config.indexing.exclude_patterns.clone();
    let watcher_excludes = excludes.clone();
    let initialized = tokio::task::spawn_blocking(move || -> Result<()> {
        match load_snapshot(&cache) {
            Ok(snapshot) if !snapshot.entries.is_empty() => {
                build_index.replace_snapshot(snapshot)?
            }
            _ => {
                build_index.discover_files(&roots, &excludes)?;
            }
        }
        let mut catalog = index_applications_in(&desktops)?;
        catalog.extend(index_settings());
        build_index.replace_catalog_entries(catalog)?;
        save_index(&build_index, &cache)?;
        build_launcher.refresh_classifier()
    })
    .await;
    match initialized {
        Ok(Ok(())) => {}
        Ok(Err(error)) => eprintln!("warning: search index initialization failed: {error:#}"),
        Err(error) => eprintln!("warning: search index worker failed: {error}"),
    }

    let mut watch_paths = file_roots;
    watch_paths.extend(desktop_dirs);
    let _watcher = match FileWatcher::start(
        watch_paths,
        watcher_excludes,
        launcher.clone(),
        cache_path,
        std::time::Duration::from_millis(config.indexing.batch_update_interval_ms.max(1)),
    ) {
        Ok(watcher) => Some(watcher),
        Err(error) => {
            eprintln!("warning: file watching is disabled: {error:#}");
            None
        }
    };
    server.await.context("search server task failed")?
}

fn desktop_dirs(home: &std::path::Path) -> Vec<PathBuf> {
    let mut directories = vec![
        home.join(".local/share/applications"),
        PathBuf::from("/usr/share/applications"),
    ];
    if let Some(data_dirs) = std::env::var_os("XDG_DATA_DIRS") {
        directories.extend(std::env::split_paths(&data_dirs).map(|path| path.join("applications")));
    }
    let mut seen = HashSet::new();
    directories.retain(|path| seen.insert(path.clone()));
    directories
}
