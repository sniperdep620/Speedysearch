use speedysearch::cache::{default_index_path, load_snapshot, save_index};
use speedysearch::config::{default_config_path, Config};
use speedysearch::index::{index_applications_in, index_settings, EntryType, IndexEntry, SearchIndex};
use speedysearch::{FeatureOptions, SearchLauncher};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
use tauri::{Manager, State};

#[derive(Clone)]
pub struct LauncherState {
    pub launcher: Arc<SearchLauncher>,
    pub roots: Vec<PathBuf>,
    pub excludes: Vec<String>,
    pub cache_path: PathBuf,
    pub hotkey: Arc<Mutex<String>>,
    pub hotkey_backend: HotkeyBackend,
}

#[derive(Clone, Debug)]
pub enum HotkeyBackend {
    Native,
    Cosmic {
        custom_path: PathBuf,
        defaults_path: PathBuf,
        command: String,
    },
    UnsupportedWayland,
}

#[derive(Clone, Debug, Serialize)]
pub struct UiSearchResult {
    pub id: String,
    pub entry_type: EntryType,
    pub name: String,
    pub path: String,
    pub icon_path: Option<String>,
    pub ml_score: f32,
    pub frecency_score: f32,
    pub is_dir: bool,
    pub category: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
pub struct UiQueryResponse {
    pub results: Vec<UiSearchResult>,
    pub latency_ms: u64,
    pub index_stale: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct UiConfig {
    pub layout_mode: String,
    pub preview_anchor: String,
    pub opacity: f32,
    pub animation_duration_ms: u16,
    pub global_hotkey: String,
    pub show_frecency: bool,
    pub highlight_active_app: bool,
}

impl Default for UiConfig {
    fn default() -> Self {
        Self {
            layout_mode: "three-column".into(),
            preview_anchor: "right".into(),
            opacity: 0.95,
            animation_duration_ms: 200,
            global_hotkey: default_hotkey().into(),
            show_frecency: true,
            highlight_active_app: true,
        }
    }
}

pub fn initialize() -> anyhow::Result<LauncherState> {
    let home = std::env::var_os("HOME").map(PathBuf::from)
        .ok_or_else(|| anyhow::anyhow!("HOME is not set"))?;
    let config = Config::load(&default_config_path(&home))?;
    let cache_path = default_index_path();
    let index = match load_snapshot(&cache_path) {
        Ok(snapshot) => SearchIndex::from_snapshot(snapshot),
        Err(error) => {
            eprintln!("warning: starting with an empty search index: {error:#}");
            SearchIndex::new()
        }
    };
    let desktop_dirs = desktop_dirs(&home);
    let mut catalog = index_applications_in(&desktop_dirs)?;
    catalog.extend(index_settings());
    index.replace_catalog_entries(catalog)?;

    let model_path = config.model_path(&home);
    let launcher = Arc::new(SearchLauncher::with_options(
        index,
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
    let _ = save_index(&launcher.index, &cache_path);
    let hotkey = load_ui_config().global_hotkey;
    let hotkey_backend = detect_hotkey_backend(&home);
    Ok(LauncherState {
        launcher,
        roots: config.watch_paths(&home),
        excludes: config.indexing.exclude_patterns,
        cache_path,
        hotkey: Arc::new(Mutex::new(hotkey)),
        hotkey_backend,
    })
}

pub fn warm_file_index(state: LauncherState) {
    if state.launcher.index.entries.read().is_ok_and(|entries| {
        entries.iter().any(|entry| entry.entry_type == EntryType::File)
    }) {
        return;
    }
    if let Err(error) = state.launcher.index.discover_files(&state.roots, &state.excludes)
        .and_then(|()| state.launcher.refresh_classifier())
        .and_then(|()| save_index(&state.launcher.index, &state.cache_path))
    {
        eprintln!("warning: background file indexing failed: {error:#}");
    }
}

#[tauri::command]
pub async fn search_query(state: State<'_, LauncherState>, query: String) -> Result<UiQueryResponse, String> {
    let launcher = state.launcher.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let response = launcher.query(&query).map_err(|error| error.to_string())?;
        Ok(UiQueryResponse {
            results: response.results.into_iter().map(|result| {
                let entry = result.entry;
                UiSearchResult {
                    id: entry.id.to_string(),
                    entry_type: entry.entry_type,
                    name: entry.name,
                    path: entry.path,
                    icon_path: entry.icon_path,
                    ml_score: result.ml_score,
                    frecency_score: entry.frecency_score,
                    is_dir: entry.metadata.is_dir,
                    category: entry.metadata.category,
                }
            }).collect(),
            latency_ms: response.latency_ms.min(u64::MAX as u128) as u64,
            index_stale: response.index_stale,
        })
    }).await.map_err(|error| error.to_string())?
}

#[tauri::command]
pub async fn log_click(
    state: State<'_, LauncherState>,
    query: String,
    selected_id: String,
    rank_position: u8,
) -> Result<(), String> {
    let launcher = state.launcher.clone();
    let selected_id = selected_id.parse::<u64>().map_err(|_| "invalid result id".to_string())?;
    tauri::async_runtime::spawn_blocking(move || launcher.log_selection(&query, selected_id, rank_position))
        .await.map_err(|error| error.to_string())?.map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn open_result(
    app: tauri::AppHandle,
    state: State<'_, LauncherState>,
    id: String,
    close_window: bool,
) -> Result<(), String> {
    let entry = find_entry(&state.launcher, parse_id(&id)?)?;
    spawn_open(&entry)?;
    if close_window {
        hide_window(app)?;
    }
    Ok(())
}

#[tauri::command]
pub async fn show_in_folder(state: State<'_, LauncherState>, id: String) -> Result<(), String> {
    let entry = find_entry(&state.launcher, parse_id(&id)?)?;
    if entry.entry_type != EntryType::File { return Err("Only files and folders have a containing folder".into()); }
    let path = Path::new(&entry.path);
    let folder = if entry.metadata.is_dir { path } else { path.parent().unwrap_or(path) };
    Command::new("xdg-open").arg(folder).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null())
        .spawn().map(|_| ()).map_err(|error| format!("could not open {}: {error}", folder.display()))
}

#[tauri::command]
pub fn get_active_window_app() -> Option<String> {
    let output = Command::new("xdotool").args(["getactivewindow", "getwindowclassname"]).output().ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).trim().to_string())
        .filter(|value| !value.is_empty())
}

#[tauri::command]
pub fn get_cwd() -> Result<String, String> {
    std::env::current_dir().map(|path| path.to_string_lossy().into_owned()).map_err(|error| error.to_string())
}

#[tauri::command]
pub fn load_ui_config() -> UiConfig {
    let path = ui_config_path();
    std::fs::read_to_string(path).ok().and_then(|value| serde_json::from_str(&value).ok()).unwrap_or_default()
}

#[tauri::command]
pub fn save_ui_config(mut config: UiConfig) -> Result<UiConfig, String> {
    sanitize_config(&mut config);
    write_ui_config(&config)?;
    Ok(config)
}

#[cfg(feature = "desktop")]
#[tauri::command]
pub fn suspend_global_hotkey(app: tauri::AppHandle, state: State<'_, LauncherState>) -> Result<(), String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    let hotkey = state.hotkey.lock().map_err(|_| "shortcut lock is poisoned".to_string())?.clone();
    match &state.hotkey_backend {
        HotkeyBackend::Native => {
            let _ = app.global_shortcut().unregister(hotkey.as_str());
            Ok(())
        }
        HotkeyBackend::Cosmic { custom_path, .. } => write_cosmic_shortcut(custom_path, None, None),
        HotkeyBackend::UnsupportedWayland => Ok(()),
    }
}

#[cfg(feature = "desktop")]
#[tauri::command]
pub fn restore_global_hotkey(app: tauri::AppHandle, state: State<'_, LauncherState>) -> Result<(), String> {
    let hotkey = state.hotkey.lock().map_err(|_| "shortcut lock is poisoned".to_string())?.clone();
    register_hotkey(&app, &state.hotkey_backend, &hotkey)
}

#[cfg(feature = "desktop")]
#[tauri::command]
pub fn set_global_hotkey(
    app: tauri::AppHandle,
    state: State<'_, LauncherState>,
    shortcut: String,
) -> Result<String, String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    let shortcut = normalize_hotkey(&shortcut)?;
    let previous = state.hotkey.lock().map_err(|_| "shortcut lock is poisoned".to_string())?.clone();
    if matches!(state.hotkey_backend, HotkeyBackend::Native) {
        let _ = app.global_shortcut().unregister(previous.as_str());
    }
    if let Err(error) = register_hotkey(&app, &state.hotkey_backend, &shortcut) {
        let _ = register_hotkey(&app, &state.hotkey_backend, &previous);
        return Err(error);
    }

    let mut config = load_ui_config();
    config.global_hotkey.clone_from(&shortcut);
    if let Err(error) = write_ui_config(&config) {
        match &state.hotkey_backend {
            HotkeyBackend::Native => { let _ = app.global_shortcut().unregister(shortcut.as_str()); }
            HotkeyBackend::Cosmic { custom_path, .. } => { let _ = write_cosmic_shortcut(custom_path, None, None); }
            HotkeyBackend::UnsupportedWayland => {}
        }
        let _ = register_hotkey(&app, &state.hotkey_backend, &previous);
        return Err(error);
    }
    *state.hotkey.lock().map_err(|_| "shortcut lock is poisoned".to_string())? = shortcut.clone();
    Ok(shortcut)
}

#[cfg(feature = "desktop")]
pub fn register_startup_hotkey(app: &tauri::AppHandle, state: &LauncherState) -> Result<(), String> {
    let hotkey = state.hotkey.lock().map_err(|_| "shortcut lock is poisoned".to_string())?.clone();
    register_hotkey(app, &state.hotkey_backend, &hotkey)
}

#[cfg(feature = "desktop")]
fn register_hotkey(app: &tauri::AppHandle, backend: &HotkeyBackend, hotkey: &str) -> Result<(), String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    match backend {
        HotkeyBackend::Native => app.global_shortcut().register(hotkey)
            .map_err(|error| friendly_hotkey_error(hotkey, &error.to_string())),
        HotkeyBackend::Cosmic { custom_path, defaults_path, command } => {
            write_cosmic_shortcut(custom_path, Some(defaults_path), Some((hotkey, command)))
        }
        HotkeyBackend::UnsupportedWayland => Err(
            "Global shortcuts are not available on this Wayland desktop. Configure a desktop shortcut that launches Speedysearch.".into()
        ),
    }
}

fn write_ui_config(config: &UiConfig) -> Result<(), String> {
    let path = ui_config_path();
    if let Some(parent) = path.parent() { std::fs::create_dir_all(parent).map_err(|error| error.to_string())?; }
    let data = serde_json::to_vec_pretty(&config).map_err(|error| error.to_string())?;
    let temporary = path.with_extension("json.tmp");
    std::fs::write(&temporary, data).map_err(|error| error.to_string())?;
    std::fs::rename(&temporary, &path).map_err(|error| error.to_string())?;
    Ok(())
}

#[tauri::command]
pub fn hide_window(app: tauri::AppHandle) -> Result<(), String> {
    app.get_webview_window("main").ok_or_else(|| "main window is unavailable".to_string())?
        .hide().map_err(|error| error.to_string())
}

fn find_entry(launcher: &SearchLauncher, id: u64) -> Result<IndexEntry, String> {
    launcher.index.entries.read().map_err(|_| "search index lock is poisoned".to_string())?
        .iter().find(|entry| entry.id == id).cloned().ok_or_else(|| format!("result {id} is no longer indexed"))
}

fn parse_id(id: &str) -> Result<u64, String> {
    id.parse().map_err(|_| "invalid result id".to_string())
}

fn spawn_open(entry: &IndexEntry) -> Result<(), String> {
    let mut command = match entry.entry_type {
        EntryType::App | EntryType::Setting => {
            let mut command = Command::new("gtk-launch");
            command.arg(&entry.path);
            command
        }
        EntryType::File => {
            let mut command = Command::new("xdg-open");
            command.arg(&entry.path);
            command
        }
        EntryType::Command => return Err("Command entries cannot be launched yet".into()),
    };
    command.stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn()
        .map(|_| ()).map_err(|error| format!("could not open {}: {error}", entry.name))
}

fn sanitize_config(config: &mut UiConfig) {
    if !matches!(config.layout_mode.as_str(), "single" | "three-column" | "dynamic") {
        config.layout_mode = UiConfig::default().layout_mode;
    }
    if !matches!(config.preview_anchor.as_str(), "left" | "right" | "bottom") {
        config.preview_anchor = UiConfig::default().preview_anchor;
    }
    config.opacity = config.opacity.clamp(0.5, 1.0);
    config.animation_duration_ms = config.animation_duration_ms.clamp(0, 1_000);
    if config.global_hotkey.trim().is_empty() { config.global_hotkey = UiConfig::default().global_hotkey; }
}

fn normalize_hotkey(value: &str) -> Result<String, String> {
    let parts: Vec<_> = value.split('+').map(str::trim).filter(|part| !part.is_empty()).collect();
    if parts.len() < 2 { return Err("Use at least one modifier, such as Ctrl, Alt, or Super, plus another key.".into()); }
    let key = parts.last().copied().unwrap_or_default();
    if matches!(key.to_lowercase().as_str(), "ctrl" | "control" | "alt" | "shift" | "cmd" | "command" | "super" | "meta") {
        return Err("Choose a non-modifier key for the shortcut.".into());
    }
    if !parts[..parts.len() - 1].iter().any(|part| matches!(part.to_lowercase().as_str(), "ctrl" | "control" | "alt" | "cmd" | "command" | "super" | "meta" | "cmdorctrl" | "commandorcontrol")) {
        return Err("Shortcuts using Shift alone are not supported. Add Ctrl, Alt, or Super.".into());
    }
    Ok(parts.join("+"))
}

fn friendly_hotkey_error(shortcut: &str, detail: &str) -> String {
    format!("Could not register {shortcut}. It may already be used by the desktop or another app. ({detail})")
}

fn detect_hotkey_backend(home: &Path) -> HotkeyBackend {
    #[cfg(target_os = "linux")]
    {
        let wayland = std::env::var("XDG_SESSION_TYPE").is_ok_and(|value| value.eq_ignore_ascii_case("wayland"))
            || std::env::var_os("WAYLAND_DISPLAY").is_some();
        if wayland {
            let desktop = std::env::var("XDG_CURRENT_DESKTOP").unwrap_or_default().to_lowercase();
            if desktop.split(':').any(|part| part == "cosmic") {
                let config_root = std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from)
                    .unwrap_or_else(|| home.join(".config"));
                let executable = std::env::current_exe().unwrap_or_else(|_| PathBuf::from("speedysearch-ui"));
                return HotkeyBackend::Cosmic {
                    custom_path: config_root.join("cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom"),
                    defaults_path: PathBuf::from("/usr/share/cosmic/com.system76.CosmicSettings.Shortcuts/v1/defaults"),
                    command: shell_quote(&executable.to_string_lossy()),
                };
            }
            return HotkeyBackend::UnsupportedWayland;
        }
    }
    HotkeyBackend::Native
}

fn shell_quote(value: &str) -> String {
    if !value.is_empty() && value.chars().all(|character| character.is_ascii_alphanumeric() || "/._-".contains(character)) {
        value.to_string()
    } else {
        format!("'{}'", value.replace('\'', "'\\''"))
    }
}

fn write_cosmic_shortcut(
    custom_path: &Path,
    defaults_path: Option<&PathBuf>,
    registration: Option<(&str, &str)>,
) -> Result<(), String> {
    let original = std::fs::read_to_string(custom_path).unwrap_or_else(|_| "{\n}\n".into());
    let mut contents = remove_managed_cosmic_shortcut(&original)?;
    if let Some((shortcut, command)) = registration {
        let (binding_prefix, entry) = cosmic_shortcut_entry(shortcut, command)?;
        let compact_custom = compact_ron(&contents);
        let compact_defaults = defaults_path.and_then(|path| std::fs::read_to_string(path).ok())
            .map(|value| compact_ron(&value)).unwrap_or_default();
        if compact_custom.contains(&binding_prefix) || compact_defaults.contains(&binding_prefix) {
            return Err(format!(
                "Could not register {shortcut}. It is already used by a COSMIC desktop shortcut."
            ));
        }
        contents = insert_cosmic_entry(&contents, &entry)?;
    }
    if let Some(parent) = custom_path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| format!("could not create COSMIC shortcut directory: {error}"))?;
    }
    let temporary = custom_path.with_extension("tmp");
    std::fs::write(&temporary, contents).map_err(|error| format!("could not write COSMIC shortcut: {error}"))?;
    std::fs::rename(&temporary, custom_path).map_err(|error| format!("could not activate COSMIC shortcut: {error}"))
}

fn cosmic_shortcut_entry(shortcut: &str, command: &str) -> Result<(String, String), String> {
    let normalized = normalize_hotkey(shortcut)?;
    let mut modifier_flags = [false; 4];
    let parts: Vec<&str> = normalized.split('+').collect();
    for modifier in &parts[..parts.len() - 1] {
        match modifier.to_ascii_lowercase().as_str() {
            "super" | "meta" | "cmd" | "command" => modifier_flags[0] = true,
            "ctrl" | "control" | "cmdorctrl" | "commandorcontrol" => modifier_flags[1] = true,
            "alt" => modifier_flags[2] = true,
            "shift" => modifier_flags[3] = true,
            value => return Err(format!("{value} is not a supported shortcut modifier")),
        }
    }
    let modifiers: Vec<&str> = ["Super", "Ctrl", "Alt", "Shift"].into_iter().enumerate()
        .filter_map(|(index, modifier)| modifier_flags[index].then_some(modifier)).collect();
    let key = cosmic_key_name(parts.last().copied().unwrap_or_default())?;
    let key = serde_json::to_string(&key).map_err(|error| error.to_string())?;
    let command = serde_json::to_string(command).map_err(|error| error.to_string())?;
    let binding_prefix = format!("(modifiers:[{}],key:{key}", modifiers.join(","));
    let entry = format!(
        "    (modifiers: [{}], key: {key}, description: Some(\"Speedysearch\")): Spawn({command}),\n",
        modifiers.join(", ")
    );
    Ok((binding_prefix, entry))
}

fn cosmic_key_name(key: &str) -> Result<String, String> {
    if key.len() == 1 && key.chars().all(|character| character.is_ascii_alphanumeric()) {
        return Ok(key.to_ascii_lowercase());
    }
    let uppercase = key.to_ascii_uppercase();
    if uppercase.strip_prefix('F').and_then(|number| number.parse::<u8>().ok())
        .is_some_and(|number| (1..=24).contains(&number)) {
        return Ok(uppercase);
    }
    let mapped = match key.to_ascii_lowercase().as_str() {
        "space" => "space", "enter" | "return" => "Return", "tab" => "Tab",
        "arrowup" | "up" => "Up", "arrowdown" | "down" => "Down",
        "arrowleft" | "left" => "Left", "arrowright" | "right" => "Right",
        "comma" => "comma", "period" => "period", "slash" => "slash",
        "semicolon" => "semicolon", "quote" => "apostrophe", "bracketleft" => "bracketleft",
        "bracketright" => "bracketright", "backslash" => "backslash", "minus" => "minus",
        "equal" => "equal", "backquote" => "grave", "home" => "Home", "end" => "End",
        "pageup" => "Page_Up", "pagedown" => "Page_Down",
        _ => return Err(format!("{key} is not a supported COSMIC shortcut key")),
    };
    Ok(mapped.into())
}

fn compact_ron(contents: &str) -> String {
    let mut compact = String::with_capacity(contents.len());
    let (mut quoted, mut escaped) = (false, false);
    for character in contents.chars() {
        if quoted {
            compact.push(character);
            if escaped { escaped = false; }
            else if character == '\\' { escaped = true; }
            else if character == '"' { quoted = false; }
        } else if character == '"' {
            quoted = true;
            compact.push(character);
        } else if !character.is_whitespace() {
            compact.push(character);
        }
    }
    compact
}

fn remove_managed_cosmic_shortcut(contents: &str) -> Result<String, String> {
    let open = contents.find('{').ok_or_else(|| "COSMIC shortcut config is not a map".to_string())?;
    let close = contents.rfind('}').filter(|close| *close > open)
        .ok_or_else(|| "COSMIC shortcut config is incomplete".to_string())?;
    let inner = &contents[open + 1..close];
    let mut kept = String::with_capacity(inner.len());
    let (mut start, mut parens, mut brackets, mut braces) = (0, 0_i32, 0_i32, 0_i32);
    let (mut quoted, mut escaped) = (false, false);
    for (index, character) in inner.char_indices() {
        if quoted {
            if escaped { escaped = false; }
            else if character == '\\' { escaped = true; }
            else if character == '"' { quoted = false; }
            continue;
        }
        match character {
            '"' => quoted = true,
            '(' => parens += 1, ')' => parens -= 1,
            '[' => brackets += 1, ']' => brackets -= 1,
            '{' => braces += 1, '}' => braces -= 1,
            ',' if parens == 0 && brackets == 0 && braces == 0 => {
                let end = index + character.len_utf8();
                let segment = &inner[start..end];
                if !is_managed_cosmic_shortcut(segment) { kept.push_str(segment); }
                start = end;
            }
            _ => {}
        }
    }
    let tail = &inner[start..];
    if !is_managed_cosmic_shortcut(tail) { kept.push_str(tail); }
    Ok(format!("{}{}{}", &contents[..open + 1], kept, &contents[close..]))
}

fn is_managed_cosmic_shortcut(entry: &str) -> bool {
    entry.contains("description: Some(\"Speedysearch\")")
        // Remove entries written by v0.2.0 as well. That format is invalid for
        // COSMIC's Option<String> description field and disables the binding.
        || entry.contains("description: \"Speedysearch\"")
        // Cosmic Search was the project's previous name. Treat its shortcut
        // as ours so upgrades replace the old executable instead of leaving
        // two independently managed launcher windows behind.
        || entry.contains("description: Some(\"Cosmic Search\")")
        || entry.contains("description: \"Cosmic Search\"")
}

fn insert_cosmic_entry(contents: &str, entry: &str) -> Result<String, String> {
    let close = contents.rfind('}').ok_or_else(|| "COSMIC shortcut config is incomplete".to_string())?;
    let mut output = contents[..close].trim_end().to_string();
    output.push('\n');
    output.push_str(entry);
    output.push_str(&contents[close..]);
    Ok(output)
}

fn ui_config_path() -> PathBuf {
    let base = std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from).or_else(|| {
        std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config"))
    }).unwrap_or_else(|| PathBuf::from("/tmp"));
    base.join("speedysearch/ui-config.json")
}

fn default_hotkey() -> &'static str {
    if cfg!(target_os = "macos") { "Cmd+Space" } else { "Ctrl+Space" }
}

fn desktop_dirs(home: &Path) -> Vec<PathBuf> {
    let mut directories = vec![home.join(".local/share/applications"), PathBuf::from("/usr/share/applications")];
    if let Some(data_dirs) = std::env::var_os("XDG_DATA_DIRS") {
        directories.extend(std::env::split_paths(&data_dirs).map(|path| path.join("applications")));
    }
    let mut seen = HashSet::new();
    directories.retain(|path| seen.insert(path.clone()));
    directories
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ui_config_is_sanitized() {
        let mut config = UiConfig { layout_mode: "invalid".into(), opacity: 4.0, ..UiConfig::default() };
        sanitize_config(&mut config);
        assert_eq!(config.layout_mode, "three-column");
        assert_eq!(config.opacity, 1.0);
    }

    #[test]
    fn global_hotkeys_require_a_real_modifier_and_key() {
        assert_eq!(normalize_hotkey("Ctrl + Alt + K").as_deref(), Ok("Ctrl+Alt+K"));
        assert!(normalize_hotkey("K").is_err());
        assert!(normalize_hotkey("Shift+K").is_err());
        assert!(normalize_hotkey("Ctrl+Shift").is_err());
    }

    #[test]
    fn cosmic_shortcut_uses_compositor_key_names() {
        let (prefix, entry) = cosmic_shortcut_entry("Ctrl+Shift+Space", "/usr/bin/speedysearch-ui")
            .expect("valid shortcut");
        assert_eq!(prefix, "(modifiers:[Ctrl,Shift],key:\"space\"");
        assert!(entry.contains("description: Some(\"Speedysearch\")"));
        assert!(entry.contains("Spawn(\"/usr/bin/speedysearch-ui\")"));
    }

    #[test]
    fn managed_cosmic_shortcut_can_be_replaced_without_touching_others() {
        let contents = concat!(
            "{\n",
            "    (modifiers: [Alt], key: \"F2\"): Spawn(\"other-command\"),\n",
            "    (modifiers: [Ctrl], key: \"space\", ",
            "description: Some(\"Cosmic Search\")): Spawn(\"legacy-command\"),\n",
            "    (\n        modifiers: [Ctrl],\n        key: \"space\",\n",
            "        description: \"Speedysearch\",\n    ): Spawn(\"old-command\"),\n",
            "    (modifiers: [Ctrl, Shift], key: \"space\", ",
            "description: Some(\"Speedysearch\")): Spawn(\"current-command\"),\n",
            "}\n",
        );
        let cleaned = remove_managed_cosmic_shortcut(contents).expect("valid shortcut map");
        assert!(cleaned.contains("other-command"));
        assert!(!cleaned.contains("legacy-command"));
        assert!(!cleaned.contains("old-command"));
        assert!(!cleaned.contains("current-command"));
        assert!(!cleaned.contains("Speedysearch"));
    }

    #[test]
    fn cosmic_shortcut_writer_is_atomic_and_detects_conflicts() -> anyhow::Result<()> {
        let directory = tempfile::tempdir()?;
        let custom = directory.path().join("custom");
        let defaults = directory.path().join("defaults");
        std::fs::write(&custom, concat!(
            "{\n",
            "    (modifiers: [Alt], key: \"F2\"): Spawn(\"other-command\"),\n",
            "    (modifiers: [Ctrl], key: \"space\", description: Some(\"Cosmic Search\")): ",
            "Spawn(\"/old/cosmic-search-ui\"),\n",
            "}\n",
        ))?;
        std::fs::write(&defaults, "{\n    (modifiers: [Super], key: \"space\"): System(InputSourceSwitch),\n}\n")?;
        write_cosmic_shortcut(&custom, Some(&defaults), Some(("Ctrl+Space", "/usr/bin/speedysearch-ui")))
            .map_err(anyhow::Error::msg)?;
        let written = std::fs::read_to_string(&custom)?;
        assert!(written.contains("other-command"));
        assert!(written.contains("description: Some(\"Speedysearch\")"));
        assert!(written.contains("/usr/bin/speedysearch-ui"));
        assert!(!written.contains("Cosmic Search"));
        assert!(!written.contains("/old/cosmic-search-ui"));
        assert!(write_cosmic_shortcut(&custom, Some(&defaults), Some(("Super+Space", "/usr/bin/speedysearch-ui"))).is_err());
        assert_eq!(std::fs::read_to_string(&custom)?, written);
        Ok(())
    }
}
