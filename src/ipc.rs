use crate::index::IndexEntry;
use crate::launcher::SearchLauncher;
use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::path::Path;
use std::sync::Arc;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use tokio::net::{UnixListener, UnixStream};

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
pub struct QueryRequest {
    pub query: String,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
pub struct ClickRequest {
    pub query: String,
    pub selected_id: u64,
    pub rank_position: u8,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct SearchResult {
    pub entry: IndexEntry,
    pub ml_score: f32,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct QueryResponse {
    pub results: Vec<SearchResult>,
    pub latency_ms: u128,
    pub index_stale: bool,
}

pub async fn serve(path: &Path, launcher: Arc<SearchLauncher>) -> Result<()> {
    let listener = bind(path)?;
    serve_listener(listener, launcher).await
}

pub fn bind(path: &Path) -> Result<UnixListener> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    if path.exists() {
        if std::os::unix::net::UnixStream::connect(path).is_ok() {
            anyhow::bail!("search service is already listening on {}", path.display());
        }
        std::fs::remove_file(path)
            .with_context(|| format!("removing stale socket {}", path.display()))?;
    }
    UnixListener::bind(path).with_context(|| format!("binding {}", path.display()))
}

pub async fn serve_listener(listener: UnixListener, launcher: Arc<SearchLauncher>) -> Result<()> {
    loop {
        let (stream, _) = listener.accept().await?;
        let launcher = launcher.clone();
        tokio::spawn(async move {
            let _ = handle_connection(stream, launcher).await;
        });
    }
}

async fn handle_connection(stream: UnixStream, launcher: Arc<SearchLauncher>) -> Result<()> {
    let (reader, mut writer) = stream.into_split();
    let mut lines = BufReader::new(reader).lines();
    while let Some(line) = lines.next_line().await? {
        let value: serde_json::Value = match serde_json::from_str(&line) {
            Ok(value) => value,
            Err(error) => {
                writer
                    .write_all(
                        serde_json::to_string(
                            &json!({"error": format!("invalid request: {error}")}),
                        )?
                        .as_bytes(),
                    )
                    .await?;
                writer.write_all(b"\n").await?;
                continue;
            }
        };
        let response = if value.get("selected_id").is_some() {
            match serde_json::from_value::<ClickRequest>(value) {
                Ok(request) => {
                    let launcher = launcher.clone();
                    match tokio::task::spawn_blocking(move || {
                        launcher.log_selection(
                            &request.query,
                            request.selected_id,
                            request.rank_position,
                        )
                    })
                    .await
                    {
                        Ok(Ok(())) => json!({"ok": true}),
                        Ok(Err(error)) => json!({"error": error.to_string()}),
                        Err(error) => json!({"error": error.to_string()}),
                    }
                }
                Err(error) => json!({"error": format!("invalid click request: {error}")}),
            }
        } else {
            match serde_json::from_value::<QueryRequest>(value) {
                Ok(request) => {
                    let launcher = launcher.clone();
                    match tokio::task::spawn_blocking(move || launcher.query(&request.query)).await
                    {
                        Ok(Ok(response)) => serde_json::to_value(response)?,
                        Ok(Err(error)) => json!({"error": error.to_string()}),
                        Err(error) => json!({"error": error.to_string()}),
                    }
                }
                Err(error) => json!({"error": format!("invalid query request: {error}")}),
            }
        };
        writer
            .write_all(serde_json::to_string(&response)?.as_bytes())
            .await?;
        writer.write_all(b"\n").await?;
    }
    Ok(())
}
