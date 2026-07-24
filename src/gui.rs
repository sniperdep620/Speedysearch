use crate::index::{EntryType, IndexEntry};
use crate::ipc::{ClickRequest, QueryRequest, QueryResponse, SearchResult};
use eframe::egui::{
    self, Align, Color32, CornerRadius, FontId, Frame, Key, Layout, Margin, RichText, Sense,
    Stroke, Vec2,
};
use std::io::{BufRead, BufReader, Write};
use std::os::unix::net::UnixStream;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::{Duration, Instant};

const ACCENT: Color32 = Color32::from_rgb(126, 231, 209);
const TEXT: Color32 = Color32::from_rgb(239, 245, 244);
const MUTED: Color32 = Color32::from_rgb(145, 158, 158);
const SURFACE: Color32 = Color32::from_rgba_premultiplied(24, 30, 32, 242);
const CARD: Color32 = Color32::from_rgba_premultiplied(39, 47, 49, 205);

#[derive(Clone, Copy, PartialEq)]
enum Filter {
    All,
    Apps,
    Files,
    Settings,
}

impl Filter {
    fn label(self) -> &'static str {
        match self {
            Self::All => "All",
            Self::Apps => "Apps",
            Self::Files => "Files",
            Self::Settings => "Settings",
        }
    }

    fn accepts(self, entry: &IndexEntry) -> bool {
        match self {
            Self::All => true,
            Self::Apps => entry.entry_type == EntryType::App,
            Self::Files => entry.entry_type == EntryType::File,
            Self::Settings => matches!(entry.entry_type, EntryType::Setting | EntryType::Command),
        }
    }
}

struct WorkerResult {
    query: String,
    result: Result<QueryResponse, String>,
}

pub struct SearchApp {
    query: String,
    sent_query: String,
    results: Vec<SearchResult>,
    filter: Filter,
    selected: usize,
    latency_ms: u128,
    index_stale: bool,
    searching: bool,
    error: Option<String>,
    status: Option<String>,
    changed_at: Instant,
    tx: Sender<String>,
    rx: Receiver<WorkerResult>,
    socket: PathBuf,
}

impl SearchApp {
    fn new(cc: &eframe::CreationContext<'_>, socket: PathBuf) -> Self {
        configure_style(&cc.egui_ctx);
        let (query_tx, query_rx) = mpsc::channel::<String>();
        let (result_tx, result_rx) = mpsc::channel::<WorkerResult>();
        let worker_socket = socket.clone();
        std::thread::spawn(move || search_worker(worker_socket, query_rx, result_tx));
        let app = Self {
            query: String::new(),
            sent_query: String::new(),
            results: Vec::new(),
            filter: Filter::All,
            selected: 0,
            latency_ms: 0,
            index_stale: false,
            searching: true,
            error: None,
            status: None,
            changed_at: Instant::now(),
            tx: query_tx,
            rx: result_rx,
            socket,
        };
        let _ = app.tx.send(String::new());
        app
    }

    fn filtered(&self) -> Vec<&SearchResult> {
        self.results
            .iter()
            .filter(|result| self.filter.accepts(&result.entry))
            .take(12)
            .collect()
    }

    fn send_query(&mut self) {
        self.sent_query.clone_from(&self.query);
        self.searching = true;
        self.error = None;
        let _ = self.tx.send(self.query.clone());
    }

    fn receive_results(&mut self) {
        while let Ok(message) = self.rx.try_recv() {
            if message.query != self.query {
                continue;
            }
            self.searching = false;
            match message.result {
                Ok(response) => {
                    self.results = response.results;
                    self.latency_ms = response.latency_ms;
                    self.index_stale = response.index_stale;
                    self.selected = self.selected.min(self.filtered().len().saturating_sub(1));
                    self.error = None;
                }
                Err(error) => {
                    self.results.clear();
                    self.error = Some(error);
                }
            }
        }
    }

    fn activate_selected(&mut self) -> bool {
        let result = self
            .filtered()
            .get(self.selected)
            .map(|result| (*result).clone());
        if let Some(result) = result {
            let entry = result.entry;
            let rank_position = self
                .results
                .iter()
                .position(|candidate| candidate.entry.id == entry.id)
                .map(|position| (position + 1) as u8)
                .unwrap_or(10);
            match open_entry(&entry) {
                Ok(()) => {
                    self.status = Some(format!("Opened {}", entry.name));
                    log_click_after_delay(
                        self.socket.clone(),
                        self.query.clone(),
                        entry.id,
                        rank_position,
                    );
                    return true;
                }
                Err(error) => self.status = Some(error),
            }
        }
        false
    }

    fn keyboard(&mut self, ctx: &egui::Context) {
        let count = self.filtered().len();
        if ctx.input(|input| input.key_pressed(Key::ArrowDown)) && count > 0 {
            self.selected = (self.selected + 1).min(count - 1);
        }
        if ctx.input(|input| input.key_pressed(Key::ArrowUp)) {
            self.selected = self.selected.saturating_sub(1);
        }
        if ctx.input(|input| input.key_pressed(Key::Enter)) && self.activate_selected() {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
        if ctx.input(|input| input.key_pressed(Key::Escape)) {
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
    }
}

impl eframe::App for SearchApp {
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        self.receive_results();
        if self.query != self.sent_query && self.changed_at.elapsed() >= Duration::from_millis(90) {
            self.send_query();
        }
        if self.searching {
            ctx.request_repaint_after(Duration::from_millis(40));
        }
        self.keyboard(&ctx);

        Frame::new()
            .fill(SURFACE)
            .corner_radius(CornerRadius::same(22))
            .stroke(Stroke::new(1.0, Color32::from_white_alpha(22)))
            .inner_margin(Margin::same(18))
            .show(ui, |ui| {
                title_bar(ui, &ctx);
                ui.add_space(12.0);
                self.search_box(ui);
                ui.add_space(12.0);
                self.filter_bar(ui);
                ui.add_space(12.0);
                self.content(ui, &ctx);
                ui.add_space(10.0);
                self.footer(ui);
            });
    }
}

impl SearchApp {
    fn search_box(&mut self, ui: &mut egui::Ui) {
        Frame::new()
            .fill(Color32::from_rgba_premultiplied(10, 14, 15, 185))
            .corner_radius(CornerRadius::same(14))
            .stroke(Stroke::new(1.0, Color32::from_white_alpha(28)))
            .inner_margin(Margin::symmetric(14, 10))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.label(RichText::new("⌕").size(25.0).color(ACCENT));
                    let response = ui.add_sized(
                        [ui.available_width() - 34.0, 30.0],
                        egui::TextEdit::singleline(&mut self.query)
                            .hint_text("Search apps, files and settings…")
                            .font(FontId::proportional(18.0))
                            .text_color(TEXT),
                    );
                    if response.changed() {
                        self.changed_at = Instant::now();
                        self.selected = 0;
                    }
                    response.request_focus();
                    if !self.query.is_empty()
                        && ui.small_button("×").on_hover_text("Clear search").clicked()
                    {
                        self.query.clear();
                        self.changed_at = Instant::now();
                        self.selected = 0;
                    }
                });
            });
    }

    fn filter_bar(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            for filter in [Filter::All, Filter::Apps, Filter::Files, Filter::Settings] {
                let active = self.filter == filter;
                let button = egui::Button::new(RichText::new(filter.label()).color(if active {
                    Color32::from_rgb(10, 35, 31)
                } else {
                    MUTED
                }))
                .fill(if active { ACCENT } else { Color32::TRANSPARENT })
                .stroke(Stroke::new(
                    1.0,
                    if active {
                        ACCENT
                    } else {
                        Color32::from_white_alpha(24)
                    },
                ))
                .corner_radius(CornerRadius::same(9))
                .min_size(Vec2::new(68.0, 30.0));
                if ui.add(button).clicked() {
                    self.filter = filter;
                    self.selected = 0;
                }
            }
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if self.searching {
                    ui.spinner();
                } else {
                    ui.label(
                        RichText::new(format!("{} results", self.filtered().len()))
                            .small()
                            .color(MUTED),
                    );
                }
            });
        });
    }

    fn content(&mut self, ui: &mut egui::Ui, ctx: &egui::Context) {
        if let Some(error) = &self.error {
            empty_state(
                ui,
                "Search is offline",
                error,
                "The daemon starts automatically. Try again in a moment.",
            );
            return;
        }
        let entries: Vec<IndexEntry> = self
            .filtered()
            .into_iter()
            .map(|result| result.entry.clone())
            .collect();
        if entries.is_empty() {
            let (title, detail) = if self.searching {
                (
                    "Warming up the index",
                    "Your files and apps will appear here shortly.",
                )
            } else if self.query.is_empty() {
                (
                    "Ready when you are",
                    "Type a name, file extension, or setting.",
                )
            } else {
                ("No matches", "Try fewer words or a different filter.")
            };
            empty_state(ui, title, detail, "Tip: fuzzy search handles small typos.");
            return;
        }
        egui::ScrollArea::vertical()
            .max_height(405.0)
            .auto_shrink([false, false])
            .show(ui, |ui| {
                for (position, entry) in entries.iter().enumerate() {
                    let selected = position == self.selected;
                    let response = result_row(ui, entry, selected);
                    if response.hovered() {
                        self.selected = position;
                    }
                    if response.clicked() {
                        self.selected = position;
                        self.activate_selected();
                    }
                    if response.secondary_clicked() {
                        ctx.copy_text(entry.path.clone());
                        self.status = Some("Location copied".into());
                    }
                    ui.add_space(5.0);
                }
            });
    }

    fn footer(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            let status = self.status.take().unwrap_or_else(|| {
                if self.index_stale {
                    "Index is refreshing".into()
                } else {
                    format!("Search completed in {} ms", self.latency_ms)
                }
            });
            ui.label(RichText::new(status).small().color(MUTED));
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.label(
                    RichText::new("↵ open   ↑↓ navigate   esc close")
                        .small()
                        .color(Color32::from_gray(105)),
                );
            });
        });
    }
}

fn title_bar(ui: &mut egui::Ui, ctx: &egui::Context) {
    ui.horizontal(|ui| {
        let drag = ui.add(
            egui::Label::new(
                RichText::new("SPEEDYSEARCH")
                    .strong()
                    .color(TEXT)
                    .extra_letter_spacing(1.5),
            )
            .sense(Sense::click_and_drag()),
        );
        if drag.drag_started() {
            ctx.send_viewport_cmd(egui::ViewportCommand::StartDrag);
        }
        ui.label(
            RichText::new("LOCAL")
                .small()
                .color(ACCENT)
                .background_color(Color32::from_rgba_premultiplied(68, 113, 105, 90)),
        );
        ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
            if ui.small_button("×").on_hover_text("Close").clicked() {
                ctx.send_viewport_cmd(egui::ViewportCommand::Close);
            }
        });
    });
}

fn result_row(ui: &mut egui::Ui, entry: &IndexEntry, selected: bool) -> egui::Response {
    let fill = if selected {
        Color32::from_rgba_premultiplied(62, 78, 77, 235)
    } else {
        CARD
    };
    let response = Frame::new()
        .fill(fill)
        .corner_radius(CornerRadius::same(12))
        .stroke(Stroke::new(
            1.0,
            if selected {
                Color32::from_rgba_premultiplied(126, 231, 209, 100)
            } else {
                Color32::from_white_alpha(15)
            },
        ))
        .inner_margin(Margin::symmetric(12, 9))
        .show(ui, |ui| {
            ui.set_min_height(48.0);
            ui.horizontal(|ui| {
                let (symbol, kind) = match entry.entry_type {
                    EntryType::App => ("◈", "APP"),
                    EntryType::File if entry.metadata.is_dir => ("□", "FOLDER"),
                    EntryType::File => ("◇", "FILE"),
                    EntryType::Setting => ("⌁", "SETTING"),
                    EntryType::Command => (">_", "COMMAND"),
                };
                Frame::new()
                    .fill(Color32::from_rgba_premultiplied(126, 231, 209, 22))
                    .corner_radius(CornerRadius::same(9))
                    .inner_margin(Margin::same(9))
                    .show(ui, |ui| {
                        ui.label(RichText::new(symbol).size(19.0).color(ACCENT));
                    });
                ui.vertical(|ui| {
                    ui.label(RichText::new(&entry.name).size(16.0).color(TEXT).strong());
                    ui.label(RichText::new(display_path(entry)).small().color(MUTED));
                });
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    ui.label(RichText::new(kind).small().color(if selected {
                        ACCENT
                    } else {
                        Color32::from_gray(110)
                    }));
                });
            });
        })
        .response
        .interact(Sense::click());
    response.on_hover_text("Open · right-click to copy location")
}

fn display_path(entry: &IndexEntry) -> String {
    if matches!(entry.entry_type, EntryType::App | EntryType::Setting) {
        return entry
            .metadata
            .category
            .clone()
            .unwrap_or_else(|| entry.path.clone());
    }
    let path = Path::new(&entry.path);
    path.parent()
        .map(|parent| parent.to_string_lossy().into_owned())
        .unwrap_or_else(|| entry.path.clone())
}

fn empty_state(ui: &mut egui::Ui, title: &str, detail: &str, hint: &str) {
    ui.vertical_centered(|ui| {
        ui.add_space(65.0);
        ui.label(RichText::new("✦").size(28.0).color(ACCENT));
        ui.add_space(8.0);
        ui.label(RichText::new(title).size(18.0).color(TEXT).strong());
        ui.label(RichText::new(detail).color(MUTED));
        ui.add_space(8.0);
        ui.label(RichText::new(hint).small().color(Color32::from_gray(105)));
        ui.add_space(70.0);
    });
}

fn configure_style(ctx: &egui::Context) {
    ctx.style_mut_of(egui::Theme::Dark, |style| {
        style.spacing.item_spacing = Vec2::new(8.0, 8.0);
        style.visuals.dark_mode = true;
        style.visuals.panel_fill = Color32::TRANSPARENT;
        style.visuals.window_fill = SURFACE;
        style.visuals.widgets.inactive.fg_stroke.color = MUTED;
        style.visuals.widgets.hovered.fg_stroke.color = TEXT;
        style.visuals.selection.bg_fill = Color32::from_rgba_premultiplied(126, 231, 209, 75);
    });
}

fn search_worker(socket: PathBuf, receiver: Receiver<String>, sender: Sender<WorkerResult>) {
    let mut daemon: Option<Child> = None;
    while let Ok(mut query) = receiver.recv() {
        while let Ok(newer) = receiver.try_recv() {
            query = newer;
        }
        let result = query_socket(&socket, &query).or_else(|first_error| {
            let daemon_running = daemon.as_mut().is_some_and(|child| matches!(child.try_wait(), Ok(None)));
            if !daemon_running {
                daemon = Some(start_daemon().map_err(|error| format!("{first_error}; could not start service: {error}"))?);
            }
            let mut last_error = first_error;
            for _ in 0..50 {
                std::thread::sleep(Duration::from_millis(100));
                match query_socket(&socket, &query) {
                    Ok(response) => return Ok(response),
                    Err(error) => last_error = error,
                }
                if let Some(child) = daemon.as_mut() {
                    if let Ok(Some(status)) = child.try_wait() {
                        daemon = None;
                        return Err(format!("Search service exited with {status}; last connection error: {last_error}"));
                    }
                }
            }
            Err(format!("Could not connect to the local search service: {last_error}"))
        });
        let _ = sender.send(WorkerResult { query, result });
    }
}

fn query_socket(socket: &Path, query: &str) -> Result<QueryResponse, String> {
    let mut stream = UnixStream::connect(socket).map_err(|error| error.to_string())?;
    stream
        .set_read_timeout(Some(Duration::from_secs(4)))
        .map_err(|error| error.to_string())?;
    let request = serde_json::to_string(&QueryRequest {
        query: query.into(),
    })
    .map_err(|error| error.to_string())?;
    writeln!(stream, "{request}").map_err(|error| error.to_string())?;
    let mut line = String::new();
    BufReader::new(stream)
        .read_line(&mut line)
        .map_err(|error| error.to_string())?;
    serde_json::from_str(&line).map_err(|error| format!("Invalid search response: {error}"))
}

fn log_click_after_delay(socket: PathBuf, query: String, selected_id: u64, rank_position: u8) {
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(50));
        let Ok(mut stream) = UnixStream::connect(socket) else {
            return;
        };
        let request = ClickRequest {
            query,
            selected_id,
            rank_position,
        };
        let Ok(json) = serde_json::to_string(&request) else {
            return;
        };
        let _ = writeln!(stream, "{json}");
    });
}

fn start_daemon() -> Result<Child, String> {
    let executable = std::env::current_exe().map_err(|error| error.to_string())?;
    Command::new(executable)
        .arg("--daemon")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| error.to_string())
}

fn open_entry(entry: &IndexEntry) -> Result<(), String> {
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
    command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Could not open {}: {error}", entry.name))
}

pub fn run(socket: PathBuf) -> anyhow::Result<()> {
    let viewport = egui::ViewportBuilder::default()
        .with_app_id("speedysearch")
        .with_title("Speedysearch")
        .with_inner_size([680.0, 620.0])
        .with_min_inner_size([520.0, 460.0])
        .with_transparent(true)
        .with_decorations(false)
        .with_resizable(true);
    let options = eframe::NativeOptions {
        viewport,
        centered: true,
        renderer: eframe::Renderer::Glow,
        ..Default::default()
    };
    eframe::run_native(
        "speedysearch",
        options,
        Box::new(move |cc| Ok(Box::new(SearchApp::new(cc, socket)))),
    )
    .map_err(|error| anyhow::anyhow!(error.to_string()))
}
