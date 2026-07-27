use crate::daemon::WindowsDaemon;
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use tauri::{Manager, State};

#[derive(Clone)]
pub struct LauncherState {
    daemon: Arc<WindowsDaemon>,
    hotkey: Arc<Mutex<String>>,
}

#[derive(Clone, Debug, Serialize)]
pub struct UiSearchResult {
    pub id: String,
    pub entry_type: String,
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
            global_hotkey: "Ctrl+Space".into(),
            show_frecency: true,
            highlight_active_app: true,
        }
    }
}

#[derive(Debug, Deserialize)]
struct WindowsQueryResponse {
    #[serde(default)]
    results: Vec<WindowsSearchResult>,
    #[serde(default)]
    latency_ms: f64,
    #[serde(default)]
    index_stale: bool,
}

#[derive(Debug, Deserialize)]
struct WindowsSearchResult {
    id: String,
    entry_type: String,
    #[serde(default)]
    name: String,
    #[serde(default)]
    path: String,
    #[serde(default)]
    icon_path: Option<String>,
    #[serde(default)]
    score: f32,
    #[serde(default)]
    frecency_score: f32,
    #[serde(default)]
    is_dir: bool,
    #[serde(default)]
    category: Option<String>,
}

#[derive(Deserialize)]
struct ActionResponse {
    ok: bool,
}

pub fn initialize(app: &tauri::AppHandle) -> anyhow::Result<LauncherState> {
    let resource_dir = app.path().resource_dir()?;
    let daemon = WindowsDaemon::start(&resource_dir).map_err(anyhow::Error::msg)?;
    Ok(LauncherState {
        daemon: Arc::new(daemon),
        hotkey: Arc::new(Mutex::new(load_ui_config().global_hotkey)),
    })
}

#[tauri::command]
pub async fn search_query(
    state: State<'_, LauncherState>,
    query: String,
) -> Result<UiQueryResponse, String> {
    let daemon = state.daemon.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let response: WindowsQueryResponse = daemon.request(&json!({ "query": query }))?;
        map_query_response(response)
    })
    .await
    .map_err(|error| error.to_string())?
}

#[tauri::command]
pub async fn log_click(
    state: State<'_, LauncherState>,
    query: String,
    selected_id: String,
    rank_position: u8,
) -> Result<(), String> {
    let daemon = state.daemon.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let response: ActionResponse = daemon.request(&json!({
            "query": query,
            "selected_id": selected_id,
            "rank_position": rank_position,
        }))?;
        response
            .ok
            .then_some(())
            .ok_or_else(|| "the selected result is no longer indexed".to_string())
    })
    .await
    .map_err(|error| error.to_string())?
}

#[tauri::command]
pub async fn open_result(
    app: tauri::AppHandle,
    state: State<'_, LauncherState>,
    id: String,
    close_window: bool,
) -> Result<(), String> {
    invoke_action(state.daemon.clone(), "open", id).await?;
    if close_window {
        hide_window(app)?;
    }
    Ok(())
}

#[tauri::command]
pub async fn show_in_folder(state: State<'_, LauncherState>, id: String) -> Result<(), String> {
    invoke_action(state.daemon.clone(), "show_in_folder", id).await
}

async fn invoke_action(
    daemon: Arc<WindowsDaemon>,
    action: &'static str,
    id: String,
) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        let response: ActionResponse = daemon.request(&json!({ "action": action, "id": id }))?;
        response
            .ok
            .then_some(())
            .ok_or_else(|| "Windows search backend did not complete the action".to_string())
    })
    .await
    .map_err(|error| error.to_string())?
}

#[tauri::command]
pub fn get_active_window_app() -> Option<String> {
    // The Windows search engine captures this context natively for ranking.
    None
}

#[tauri::command]
pub fn get_cwd() -> Result<String, String> {
    std::env::current_dir()
        .map(|path| path.to_string_lossy().into_owned())
        .map_err(|error| error.to_string())
}

#[tauri::command]
pub fn load_ui_config() -> UiConfig {
    std::fs::read_to_string(ui_config_path())
        .ok()
        .and_then(|value| serde_json::from_str(&value).ok())
        .unwrap_or_default()
}

#[tauri::command]
pub fn save_ui_config(mut config: UiConfig) -> Result<UiConfig, String> {
    sanitize_config(&mut config);
    write_ui_config(&config)?;
    Ok(config)
}

#[tauri::command]
pub fn suspend_global_hotkey(
    app: tauri::AppHandle,
    state: State<'_, LauncherState>,
) -> Result<(), String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    let hotkey = state
        .hotkey
        .lock()
        .map_err(|_| "shortcut lock is poisoned".to_string())?
        .clone();
    let _ = app.global_shortcut().unregister(hotkey.as_str());
    Ok(())
}

#[tauri::command]
pub fn restore_global_hotkey(
    app: tauri::AppHandle,
    state: State<'_, LauncherState>,
) -> Result<(), String> {
    let hotkey = state
        .hotkey
        .lock()
        .map_err(|_| "shortcut lock is poisoned".to_string())?
        .clone();
    register_hotkey(&app, &hotkey)
}

#[tauri::command]
pub fn set_global_hotkey(
    app: tauri::AppHandle,
    state: State<'_, LauncherState>,
    shortcut: String,
) -> Result<String, String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    let shortcut = normalize_hotkey(&shortcut)?;
    let previous = state
        .hotkey
        .lock()
        .map_err(|_| "shortcut lock is poisoned".to_string())?
        .clone();
    let _ = app.global_shortcut().unregister(previous.as_str());
    if let Err(error) = register_hotkey(&app, &shortcut) {
        let _ = register_hotkey(&app, &previous);
        return Err(error);
    }

    let mut config = load_ui_config();
    config.global_hotkey.clone_from(&shortcut);
    if let Err(error) = write_ui_config(&config) {
        let _ = app.global_shortcut().unregister(shortcut.as_str());
        let _ = register_hotkey(&app, &previous);
        return Err(error);
    }
    *state
        .hotkey
        .lock()
        .map_err(|_| "shortcut lock is poisoned".to_string())? = shortcut.clone();
    Ok(shortcut)
}

pub fn register_startup_hotkey(
    app: &tauri::AppHandle,
    state: &LauncherState,
) -> Result<(), String> {
    let hotkey = state
        .hotkey
        .lock()
        .map_err(|_| "shortcut lock is poisoned".to_string())?
        .clone();
    register_hotkey(app, &hotkey)
}

fn register_hotkey(app: &tauri::AppHandle, hotkey: &str) -> Result<(), String> {
    use tauri_plugin_global_shortcut::GlobalShortcutExt;
    app.global_shortcut()
        .register(hotkey)
        .map_err(|error| friendly_hotkey_error(hotkey, &error.to_string()))
}

#[tauri::command]
pub fn hide_window(app: tauri::AppHandle) -> Result<(), String> {
    app.get_webview_window("main")
        .ok_or_else(|| "main window is unavailable".to_string())?
        .hide()
        .map_err(|error| error.to_string())
}

fn map_query_response(response: WindowsQueryResponse) -> Result<UiQueryResponse, String> {
    let results = response
        .results
        .into_iter()
        .map(|result| {
            Ok(UiSearchResult {
                id: result.id,
                entry_type: normalize_entry_type(&result.entry_type)?,
                name: result.name,
                path: result.path,
                icon_path: result.icon_path,
                ml_score: result.score,
                frecency_score: result.frecency_score,
                is_dir: result.is_dir,
                category: result.category,
            })
        })
        .collect::<Result<Vec<_>, String>>()?;
    let latency_ms = if response.latency_ms.is_finite() {
        response.latency_ms.clamp(0.0, u64::MAX as f64).round() as u64
    } else {
        0
    };
    Ok(UiQueryResponse {
        results,
        latency_ms,
        index_stale: response.index_stale,
    })
}

fn normalize_entry_type(value: &str) -> Result<String, String> {
    match value.to_ascii_lowercase().as_str() {
        "app" => Ok("App".into()),
        "file" => Ok("File".into()),
        "setting" => Ok("Setting".into()),
        "command" => Ok("Command".into()),
        _ => Err(format!(
            "Windows search backend returned an unknown result type: {value}"
        )),
    }
}

fn sanitize_config(config: &mut UiConfig) {
    if !matches!(
        config.layout_mode.as_str(),
        "single" | "three-column" | "dynamic"
    ) {
        config.layout_mode = UiConfig::default().layout_mode;
    }
    if !matches!(config.preview_anchor.as_str(), "left" | "right" | "bottom") {
        config.preview_anchor = UiConfig::default().preview_anchor;
    }
    config.opacity = config.opacity.clamp(0.5, 1.0);
    config.animation_duration_ms = config.animation_duration_ms.clamp(0, 1_000);
    if config.global_hotkey.trim().is_empty() {
        config.global_hotkey = UiConfig::default().global_hotkey;
    }
}

fn normalize_hotkey(value: &str) -> Result<String, String> {
    let parts: Vec<_> = value
        .split('+')
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .collect();
    if parts.len() < 2 {
        return Err(
            "Use at least one modifier, such as Ctrl, Alt, or Super, plus another key.".into(),
        );
    }
    let key = parts.last().copied().unwrap_or_default();
    if matches!(
        key.to_ascii_lowercase().as_str(),
        "ctrl" | "control" | "alt" | "shift" | "cmd" | "command" | "super" | "meta"
    ) {
        return Err("Choose a non-modifier key for the shortcut.".into());
    }
    if !parts[..parts.len() - 1].iter().any(|part| {
        matches!(
            part.to_ascii_lowercase().as_str(),
            "ctrl"
                | "control"
                | "alt"
                | "cmd"
                | "command"
                | "super"
                | "meta"
                | "cmdorctrl"
                | "commandorcontrol"
        )
    }) {
        return Err(
            "Shortcuts using Shift alone are not supported. Add Ctrl, Alt, or Super.".into(),
        );
    }
    Ok(parts.join("+"))
}

fn friendly_hotkey_error(shortcut: &str, detail: &str) -> String {
    format!("Could not register {shortcut}. It may already be used by Windows or another app. ({detail})")
}

fn write_ui_config(config: &UiConfig) -> Result<(), String> {
    let path = ui_config_path();
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let data = serde_json::to_vec_pretty(config).map_err(|error| error.to_string())?;
    let temporary = path.with_extension("json.tmp");
    std::fs::write(&temporary, data).map_err(|error| error.to_string())?;
    if path.exists() {
        std::fs::remove_file(&path).map_err(|error| error.to_string())?;
    }
    std::fs::rename(&temporary, path).map_err(|error| error.to_string())
}

fn ui_config_path() -> PathBuf {
    std::env::var_os("LOCALAPPDATA")
        .or_else(|| std::env::var_os("APPDATA"))
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
        .join("Speedysearch")
        .join("ui-config.json")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn windows_result_types_match_the_react_contract() {
        assert_eq!(normalize_entry_type("app").as_deref(), Ok("App"));
        assert_eq!(normalize_entry_type("FILE").as_deref(), Ok("File"));
        assert!(normalize_entry_type("unknown").is_err());
    }

    #[test]
    fn ui_config_is_sanitized() {
        let mut config = UiConfig {
            layout_mode: "invalid".into(),
            opacity: 4.0,
            ..UiConfig::default()
        };
        sanitize_config(&mut config);
        assert_eq!(config.layout_mode, "three-column");
        assert_eq!(config.opacity, 1.0);
    }
}
