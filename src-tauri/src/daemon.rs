use serde::de::DeserializeOwned;
use serde::Deserialize;
use serde_json::{json, Value};
use std::fs::{File, OpenOptions};
use std::io::{self, BufRead, BufReader, Read, Write};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant};

const PIPE_PATH: &str = r"\\.\pipe\speedysearch";
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const STARTUP_TIMEOUT: Duration = Duration::from_secs(10);
const CONNECT_TIMEOUT: Duration = Duration::from_secs(2);
const CONNECT_RETRY_DELAY: Duration = Duration::from_millis(10);
const MAX_RESPONSE_BYTES: usize = 1024 * 1024;
const ERROR_PIPE_BUSY: i32 = 231;

pub struct WindowsDaemon {
    process: Mutex<Option<Child>>,
}

#[derive(Deserialize)]
struct ActionResponse {
    ok: bool,
}

impl WindowsDaemon {
    pub fn start(resource_dir: &Path) -> Result<Self, String> {
        let daemon = Self {
            process: Mutex::new(None),
        };
        if daemon.ping().is_ok() {
            return Ok(daemon);
        }

        let executable = resolve_executable(resource_dir)?;
        let child = Command::new(&executable)
            .arg("--daemon")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|error| {
                format!(
                    "could not start the Windows search backend at {}: {error}",
                    executable.display()
                )
            })?;
        *daemon
            .process
            .lock()
            .map_err(|_| "backend process lock is poisoned".to_string())? = Some(child);
        daemon.wait_until_ready()?;
        Ok(daemon)
    }

    pub fn request<T: DeserializeOwned>(&self, request: &Value) -> Result<T, String> {
        let mut pipe = open_pipe()
            .map_err(|error| format!("Windows search backend is unavailable: {error}"))?;
        let mut encoded = serde_json::to_vec(request).map_err(|error| error.to_string())?;
        encoded.push(b'\n');
        pipe.write_all(&encoded)
            .and_then(|()| pipe.flush())
            .map_err(|error| {
                format!("could not send a request to the Windows search backend: {error}")
            })?;

        let mut response = String::new();
        BufReader::new(pipe)
            .take((MAX_RESPONSE_BYTES + 1) as u64)
            .read_line(&mut response)
            .map_err(|error| format!("could not read the Windows search response: {error}"))?;
        if response.is_empty() {
            return Err("Windows search backend closed the pipe without a response".into());
        }
        if response.len() > MAX_RESPONSE_BYTES {
            return Err("Windows search backend returned an oversized response".into());
        }
        let value: Value = serde_json::from_str(&response)
            .map_err(|error| format!("Windows search backend returned invalid JSON: {error}"))?;
        if let Some(error) = value.get("error").and_then(Value::as_str) {
            return Err(error.to_string());
        }
        serde_json::from_value(value).map_err(|error| {
            format!("Windows search backend returned an unexpected response: {error}")
        })
    }

    fn ping(&self) -> Result<(), String> {
        let response: ActionResponse = self.request(&json!({ "action": "ping" }))?;
        response
            .ok
            .then_some(())
            .ok_or_else(|| "Windows search backend did not acknowledge the request".to_string())
    }

    fn wait_until_ready(&self) -> Result<(), String> {
        let deadline = Instant::now() + STARTUP_TIMEOUT;
        let mut last_error = "backend did not create its named pipe".to_string();
        while Instant::now() < deadline {
            match self.ping() {
                Ok(()) => return Ok(()),
                Err(error) => last_error = error,
            }
            if let Ok(mut process) = self.process.lock() {
                if let Some(child) = process.as_mut() {
                    if let Ok(Some(status)) = child.try_wait() {
                        return Err(format!(
                            "Windows search backend exited during startup with {status}: {last_error}"
                        ));
                    }
                }
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        Err(format!(
            "Windows search backend was not ready within {} seconds: {last_error}",
            STARTUP_TIMEOUT.as_secs()
        ))
    }
}

impl Drop for WindowsDaemon {
    fn drop(&mut self) {
        let Ok(process) = self.process.get_mut() else {
            return;
        };
        let Some(child) = process.as_mut() else {
            return;
        };
        let _ = child.kill();
        let _ = child.wait();
    }
}

fn open_pipe() -> io::Result<File> {
    connect_with_retry(CONNECT_TIMEOUT, CONNECT_RETRY_DELAY, || {
        OpenOptions::new().read(true).write(true).open(PIPE_PATH)
    })
}

fn connect_with_retry<T>(
    timeout: Duration,
    retry_delay: Duration,
    mut connect: impl FnMut() -> io::Result<T>,
) -> io::Result<T> {
    let deadline = Instant::now() + timeout;
    loop {
        match connect() {
            Ok(pipe) => return Ok(pipe),
            Err(error) if is_transient_connect_error(&error) && Instant::now() < deadline => {
                std::thread::sleep(retry_delay);
            }
            Err(error) => return Err(error),
        }
    }
}

fn is_transient_connect_error(error: &io::Error) -> bool {
    error.raw_os_error() == Some(ERROR_PIPE_BUSY)
}

fn resolve_executable(resource_dir: &Path) -> Result<PathBuf, String> {
    let mut candidates = Vec::new();
    if let Some(path) = std::env::var_os("SPEEDYSEARCH_WINDOWS_DAEMON") {
        candidates.push(PathBuf::from(path));
    }
    candidates.push(resource_dir.join("speedysearch-cli.exe"));
    if let Ok(current_exe) = std::env::current_exe() {
        if let Some(parent) = current_exe.parent() {
            candidates.push(parent.join("speedysearch-cli.exe"));
            candidates.push(parent.join("resources").join("speedysearch-cli.exe"));
        }
    }
    #[cfg(debug_assertions)]
    {
        candidates.push(
            Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../windows-native/bin/Release/speedysearch-cli.exe"),
        );
        candidates.push(
            Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../windows-native/bin/Debug/speedysearch-cli.exe"),
        );
    }

    candidates
        .iter()
        .find(|path| path.is_file())
        .cloned()
        .ok_or_else(|| {
            let searched = candidates
                .iter()
                .map(|path| path.display().to_string())
                .collect::<Vec<_>>()
                .join(", ");
            format!(
                "speedysearch-cli.exe was not found. Build windows-native/build.ps1 or set \
                 SPEEDYSEARCH_WINDOWS_DAEMON. Searched: {searched}"
            )
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    #[test]
    fn busy_pipe_connections_are_retried() {
        let attempts = AtomicUsize::new(0);
        let connected =
            connect_with_retry(Duration::from_secs(1), Duration::ZERO, || {
                match attempts.fetch_add(1, Ordering::Relaxed) {
                    0 | 1 => Err(io::Error::from_raw_os_error(ERROR_PIPE_BUSY)),
                    _ => Ok("connected"),
                }
            });

        assert_eq!(
            connected.expect("the pipe should connect after transient failures"),
            "connected"
        );
        assert_eq!(attempts.load(Ordering::Relaxed), 3);
    }

    #[test]
    fn permanent_connection_errors_are_not_retried() {
        let attempts = AtomicUsize::new(0);
        let error = connect_with_retry(Duration::from_secs(1), Duration::ZERO, || {
            attempts.fetch_add(1, Ordering::Relaxed);
            Err::<(), _>(io::Error::from_raw_os_error(5))
        })
        .expect_err("access denied must fail immediately");

        assert_eq!(error.raw_os_error(), Some(5));
        assert_eq!(attempts.load(Ordering::Relaxed), 1);
    }
}
