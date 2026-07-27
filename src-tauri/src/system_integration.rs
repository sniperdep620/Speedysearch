#[cfg(any(target_os = "linux", test))]
use serde::{Deserialize, Serialize};
#[cfg(any(target_os = "linux", test))]
use std::path::Path;
#[cfg(target_os = "linux")]
use std::path::PathBuf;

const SET_DEFAULT_ARG: &str = "--replace-system-search";
const RESTORE_DEFAULT_ARG: &str = "--restore-system-search";
const STATUS_ARG: &str = "--system-search-status";

#[cfg(any(target_os = "linux", test))]
#[derive(Debug, Serialize, Deserialize)]
struct CosmicLauncherBackup {
    installed_command: String,
    original_entry: Option<String>,
}

/// Handles integration-only invocations before Tauri initializes a window.
/// Returns `true` when the process should exit without starting the GUI.
pub fn handle_cli_request() -> bool {
    let Some(argument) = std::env::args().nth(1) else {
        return false;
    };
    let result = match argument.as_str() {
        SET_DEFAULT_ARG => replace_system_search().map(|message| message.to_string()),
        RESTORE_DEFAULT_ARG => restore_system_search().map(|message| message.to_string()),
        STATUS_ARG => system_search_status(),
        _ => return false,
    };

    match result {
        Ok(message) => println!("{message}"),
        Err(error) => {
            eprintln!("Speedysearch system integration failed: {error}");
            std::process::exit(1);
        }
    }
    true
}

#[cfg(target_os = "linux")]
fn replace_system_search() -> Result<&'static str, String> {
    ensure_cosmic_desktop()?;
    let executable = std::env::current_exe()
        .map_err(|error| format!("could not locate the Speedysearch executable: {error}"))?;
    let command = shell_quote(&executable.to_string_lossy());
    let (system_actions, backup) = cosmic_paths()?;
    set_cosmic_launcher_at(&system_actions, &backup, &command)?;
    Ok("Speedysearch now handles the COSMIC system launcher action.")
}

#[cfg(not(target_os = "linux"))]
fn replace_system_search() -> Result<&'static str, String> {
    Err("this operating system does not expose a replaceable system-search provider; use Speedysearch's global shortcut instead".into())
}

#[cfg(target_os = "linux")]
fn restore_system_search() -> Result<&'static str, String> {
    ensure_cosmic_desktop()?;
    let (system_actions, backup) = cosmic_paths()?;
    restore_cosmic_launcher_at(&system_actions, &backup)?;
    Ok("The previous COSMIC system launcher has been restored.")
}

#[cfg(not(target_os = "linux"))]
fn restore_system_search() -> Result<&'static str, String> {
    Err("there is no managed system-search replacement to restore on this operating system".into())
}

#[cfg(target_os = "linux")]
fn system_search_status() -> Result<String, String> {
    ensure_cosmic_desktop()?;
    let (system_actions, backup_path) = cosmic_paths()?;
    let Some(backup) = read_backup(&backup_path)? else {
        return Ok("Speedysearch is not the configured COSMIC system launcher.".into());
    };
    let contents = read_map_or_empty(&system_actions)?;
    let active = launcher_command(&contents)?.as_deref() == Some(&backup.installed_command);
    Ok(if active {
        "Speedysearch is the configured COSMIC system launcher."
    } else {
        "COSMIC's launcher setting changed after Speedysearch was configured."
    }
    .into())
}

#[cfg(not(target_os = "linux"))]
fn system_search_status() -> Result<String, String> {
    Ok("This operating system does not expose a replaceable system-search provider; Speedysearch uses its global shortcut.".into())
}

#[cfg(target_os = "linux")]
fn ensure_cosmic_desktop() -> Result<(), String> {
    let desktop = std::env::var("XDG_CURRENT_DESKTOP").unwrap_or_default();
    if desktop
        .split(':')
        .any(|part| part.eq_ignore_ascii_case("cosmic"))
        || Path::new("/usr/share/cosmic/com.system76.CosmicSettings.Shortcuts/v1/system_actions")
            .is_file()
    {
        Ok(())
    } else {
        Err(
            "the current Linux desktop is not COSMIC; use Speedysearch's global shortcut instead"
                .into(),
        )
    }
}

#[cfg(target_os = "linux")]
fn cosmic_paths() -> Result<(PathBuf, PathBuf), String> {
    let config_root = std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")))
        .ok_or_else(|| "neither XDG_CONFIG_HOME nor HOME is set".to_string())?;
    Ok((
        config_root.join("cosmic/com.system76.CosmicSettings.Shortcuts/v1/system_actions"),
        config_root.join("speedysearch/cosmic-launcher-backup.json"),
    ))
}

#[cfg(any(target_os = "linux", test))]
fn set_cosmic_launcher_at(
    system_actions: &Path,
    backup_path: &Path,
    command: &str,
) -> Result<(), String> {
    let original = read_map_or_empty(system_actions)?;
    let (without_launcher, current_entry) = remove_launcher_entry(&original)?;
    let current_command = current_entry
        .as_deref()
        .map(parse_launcher_entry)
        .transpose()?;

    let existing_backup = read_backup(backup_path)?;
    let original_entry = match existing_backup {
        Some(backup) if current_command.as_deref() == Some(&backup.installed_command) => {
            backup.original_entry
        }
        _ => current_entry,
    };
    let backup = CosmicLauncherBackup {
        installed_command: command.to_string(),
        original_entry,
    };
    write_json_atomic(backup_path, &backup)?;

    let entry = format!("    Launcher: {},\n", json_string(command)?);
    let updated = insert_map_entry(&without_launcher, &entry)?;
    write_atomic(
        system_actions,
        updated.as_bytes(),
        "COSMIC launcher configuration",
    )
}

#[cfg(any(target_os = "linux", test))]
fn restore_cosmic_launcher_at(system_actions: &Path, backup_path: &Path) -> Result<(), String> {
    let Some(backup) = read_backup(backup_path)? else {
        return Ok(());
    };
    let original = read_map_or_empty(system_actions)?;
    let (without_launcher, current_entry) = remove_launcher_entry(&original)?;
    let current_command = current_entry
        .as_deref()
        .map(parse_launcher_entry)
        .transpose()?;
    if current_command.as_deref() != Some(&backup.installed_command) {
        return Err("the COSMIC launcher was changed after Speedysearch installed it; refusing to overwrite the newer setting".into());
    }

    let restored = if let Some(entry) = backup.original_entry {
        insert_map_entry(&without_launcher, entry.trim())?
    } else {
        without_launcher
    };
    write_atomic(
        system_actions,
        restored.as_bytes(),
        "COSMIC launcher configuration",
    )?;
    std::fs::remove_file(backup_path).map_err(|error| {
        format!("launcher restored, but its backup marker could not be removed: {error}")
    })?;
    Ok(())
}

#[cfg(any(target_os = "linux", test))]
fn read_map_or_empty(path: &Path) -> Result<String, String> {
    match std::fs::read_to_string(path) {
        Ok(contents) => Ok(contents),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok("{\n}\n".into()),
        Err(error) => Err(format!("could not read {}: {error}", path.display())),
    }
}

#[cfg(any(target_os = "linux", test))]
fn read_backup(path: &Path) -> Result<Option<CosmicLauncherBackup>, String> {
    match std::fs::read_to_string(path) {
        Ok(contents) => serde_json::from_str(&contents)
            .map(Some)
            .map_err(|error| format!("could not parse {}: {error}", path.display())),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(format!("could not read {}: {error}", path.display())),
    }
}

#[cfg(any(target_os = "linux", test))]
fn write_json_atomic(path: &Path, value: &CosmicLauncherBackup) -> Result<(), String> {
    let contents = serde_json::to_vec_pretty(value).map_err(|error| error.to_string())?;
    write_atomic(path, &contents, "launcher backup")
}

#[cfg(any(target_os = "linux", test))]
fn write_atomic(path: &Path, contents: &[u8], description: &str) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|error| format!("could not create {}: {error}", parent.display()))?;
    }
    let temporary = path.with_extension("tmp");
    std::fs::write(&temporary, contents)
        .map_err(|error| format!("could not write {description}: {error}"))?;
    std::fs::rename(&temporary, path)
        .map_err(|error| format!("could not activate {description}: {error}"))
}

#[cfg(any(target_os = "linux", test))]
fn remove_launcher_entry(contents: &str) -> Result<(String, Option<String>), String> {
    let open = contents
        .find('{')
        .ok_or_else(|| "COSMIC system-actions config is not a map".to_string())?;
    let close = contents
        .rfind('}')
        .filter(|close| *close > open)
        .ok_or_else(|| "COSMIC system-actions config is incomplete".to_string())?;
    let inner = &contents[open + 1..close];
    let mut kept = String::with_capacity(inner.len());
    let mut found = None;
    let mut start = 0;
    let mut quoted = false;
    let mut escaped = false;
    for (index, character) in inner.char_indices() {
        if quoted {
            if escaped {
                escaped = false;
            } else if character == '\\' {
                escaped = true;
            } else if character == '"' {
                quoted = false;
            }
            continue;
        }
        match character {
            '"' => quoted = true,
            ',' => {
                let end = index + 1;
                collect_system_entry(&inner[start..end], &mut kept, &mut found)?;
                start = end;
            }
            _ => {}
        }
    }
    collect_system_entry(&inner[start..], &mut kept, &mut found)?;
    Ok((
        format!("{}{}{}", &contents[..open + 1], kept, &contents[close..]),
        found,
    ))
}

#[cfg(any(target_os = "linux", test))]
fn collect_system_entry(
    segment: &str,
    kept: &mut String,
    found: &mut Option<String>,
) -> Result<(), String> {
    let trimmed = segment.trim().trim_end_matches(',').trim();
    if trimmed.starts_with("Launcher") && trimmed["Launcher".len()..].trim_start().starts_with(':')
    {
        if found.is_some() {
            return Err("COSMIC system-actions config contains multiple Launcher entries".into());
        }
        *found = Some(segment.to_string());
    } else {
        kept.push_str(segment);
    }
    Ok(())
}

#[cfg(any(target_os = "linux", test))]
fn parse_launcher_entry(entry: &str) -> Result<String, String> {
    let trimmed = entry.trim().trim_end_matches(',').trim();
    let value = trimmed
        .strip_prefix("Launcher")
        .and_then(|rest| rest.trim_start().strip_prefix(':'))
        .ok_or_else(|| "invalid COSMIC Launcher entry".to_string())?
        .trim();
    serde_json::from_str(value).map_err(|error| format!("invalid COSMIC Launcher command: {error}"))
}

#[cfg(any(target_os = "linux", test))]
fn launcher_command(contents: &str) -> Result<Option<String>, String> {
    let (_, entry) = remove_launcher_entry(contents)?;
    entry.as_deref().map(parse_launcher_entry).transpose()
}

#[cfg(any(target_os = "linux", test))]
fn insert_map_entry(contents: &str, entry: &str) -> Result<String, String> {
    let close = contents
        .rfind('}')
        .ok_or_else(|| "COSMIC system-actions config is incomplete".to_string())?;
    let mut output = contents[..close].trim_end().to_string();
    output.push('\n');
    output.push_str(entry);
    if !entry.ends_with('\n') {
        output.push('\n');
    }
    output.push_str(&contents[close..]);
    Ok(output)
}

#[cfg(any(target_os = "linux", test))]
fn json_string(value: &str) -> Result<String, String> {
    serde_json::to_string(value).map_err(|error| error.to_string())
}

#[cfg(target_os = "linux")]
fn shell_quote(value: &str) -> String {
    if !value.is_empty()
        && value
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || "/._-".contains(character))
    {
        value.to_string()
    } else {
        format!("'{}'", value.replace('\'', "'\\''"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replacing_and_restoring_launcher_preserves_other_actions() -> anyhow::Result<()> {
        let directory = tempfile::tempdir()?;
        let actions = directory.path().join("system_actions");
        let backup = directory.path().join("backup.json");
        std::fs::write(
            &actions,
            "{\n    Terminal: \"my-term\",\n    Launcher: \"old-launcher\",\n}\n",
        )?;

        set_cosmic_launcher_at(&actions, &backup, "/opt/speedysearch-ui")
            .map_err(anyhow::Error::msg)?;
        let installed = std::fs::read_to_string(&actions)?;
        assert!(installed.contains("Terminal: \"my-term\""));
        assert_eq!(
            launcher_command(&installed)
                .map_err(anyhow::Error::msg)?
                .as_deref(),
            Some("/opt/speedysearch-ui")
        );

        set_cosmic_launcher_at(&actions, &backup, "/opt/speedysearch-ui")
            .map_err(anyhow::Error::msg)?;
        restore_cosmic_launcher_at(&actions, &backup).map_err(anyhow::Error::msg)?;
        let restored = std::fs::read_to_string(&actions)?;
        assert_eq!(
            launcher_command(&restored)
                .map_err(anyhow::Error::msg)?
                .as_deref(),
            Some("old-launcher")
        );
        assert!(restored.contains("Terminal: \"my-term\""));
        assert!(!backup.exists());
        Ok(())
    }

    #[test]
    fn restore_does_not_overwrite_a_newer_user_choice() -> anyhow::Result<()> {
        let directory = tempfile::tempdir()?;
        let actions = directory.path().join("system_actions");
        let backup = directory.path().join("backup.json");
        set_cosmic_launcher_at(&actions, &backup, "/opt/speedysearch-ui")
            .map_err(anyhow::Error::msg)?;
        std::fs::write(&actions, "{\n    Launcher: \"newer-launcher\",\n}\n")?;
        assert!(restore_cosmic_launcher_at(&actions, &backup).is_err());
        assert_eq!(
            launcher_command(&std::fs::read_to_string(actions)?)
                .map_err(anyhow::Error::msg)?
                .as_deref(),
            Some("newer-launcher")
        );
        Ok(())
    }
}
