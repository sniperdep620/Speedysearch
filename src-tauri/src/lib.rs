#[cfg(not(target_os = "windows"))]
mod commands;
#[cfg(target_os = "windows")]
#[path = "windows_commands.rs"]
mod commands;
#[cfg(target_os = "windows")]
mod daemon;
mod system_integration;

#[cfg(feature = "desktop")]
use tauri::{Emitter, Manager};

#[cfg(feature = "desktop")]
fn toggle_launcher(app: &tauri::AppHandle) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    if window.is_visible().unwrap_or(false) {
        let _ = window.hide();
    } else {
        show_launcher(app);
    }
}

#[cfg(feature = "desktop")]
fn show_launcher(app: &tauri::AppHandle) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let _ = window.show();
    let _ = window.set_focus();
    let _ = window.emit("launcher-shown", ());
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
#[cfg(feature = "desktop")]
pub fn run() {
    if system_integration::handle_cli_request() {
        return;
    }
    let start_hidden = std::env::args().any(|argument| argument == "--hidden");

    // Register this first: a second launch must activate the existing launcher
    // before it pays the cost of loading the search index and ranking model.
    let builder =
        tauri::Builder::default().plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_launcher(app);
        }));
    let builder = builder.plugin(
        tauri_plugin_global_shortcut::Builder::new()
            .with_handler(|app, _shortcut, event| {
                use tauri_plugin_global_shortcut::ShortcutState;
                if event.state() != ShortcutState::Pressed {
                    return;
                }
                toggle_launcher(app);
            })
            .build(),
    );
    let app = builder
        .invoke_handler(tauri::generate_handler![
            commands::search_query,
            commands::log_click,
            commands::open_result,
            commands::show_in_folder,
            commands::get_active_window_app,
            commands::get_cwd,
            commands::load_ui_config,
            commands::save_ui_config,
            commands::suspend_global_hotkey,
            commands::restore_global_hotkey,
            commands::set_global_hotkey,
            commands::hide_window,
        ])
        .setup(|app| {
            // Plug-ins are initialized while Tauri builds the app, before this
            // hook runs. A secondary invocation therefore exits through the
            // single-instance plug-in without doing any search initialization.
            #[cfg(not(target_os = "windows"))]
            let state = commands::initialize()?;
            #[cfg(target_os = "windows")]
            let state = commands::initialize(app.handle())?;
            if let Err(error) = commands::register_startup_hotkey(app.handle(), &state) {
                eprintln!("warning: global shortcut is unavailable: {error}");
            }
            #[cfg(not(target_os = "windows"))]
            let background_state = state.clone();
            app.manage(state);
            #[cfg(not(target_os = "windows"))]
            tauri::async_runtime::spawn_blocking(move || {
                commands::warm_file_index(background_state)
            });
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while building the Speedysearch Tauri application");

    app.run(move |app_handle, event| {
        if !start_hidden && matches!(event, tauri::RunEvent::Ready) {
            // The native window starts hidden to avoid a sign-in flash. Tauri's
            // Ready event is the first point where Windows has entered the
            // event loop and can reliably service the initial `show`.
            show_launcher(app_handle);
        }
    });
}

#[cfg(not(feature = "desktop"))]
pub fn run() {
    panic!("speedysearch-tauri was built without its desktop feature")
}
