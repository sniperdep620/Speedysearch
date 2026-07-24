import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import "./ShortcutRecorder.css";

interface ShortcutRecorderProps {
  value: string;
  onSuspend: () => Promise<unknown>;
  onRestore: () => Promise<unknown>;
  onCommit: (shortcut: string) => Promise<string>;
}

export default function ShortcutRecorder({ value, onSuspend, onRestore, onCommit }: ShortcutRecorderProps) {
  const [recording, setRecording] = useState(false);
  const [saving, setSaving] = useState(false);
  const [preview, setPreview] = useState(value);
  const [message, setMessage] = useState<{ kind: "success" | "error"; text: string } | null>(null);
  const suspended = useRef(false);
  const defaultShortcut = useMemo(() => navigator.userAgent.includes("Mac") ? "Cmd+Space" : "Ctrl+Space", []);

  useEffect(() => { if (!recording) setPreview(value); }, [recording, value]);

  const begin = useCallback(async () => {
    setMessage(null);
    try {
      await onSuspend();
      suspended.current = true;
      setPreview("");
      setRecording(true);
    } catch (cause) {
      setMessage({ kind: "error", text: readableError(cause) });
    }
  }, [onSuspend]);

  const cancel = useCallback(async () => {
    setRecording(false);
    setPreview(value);
    try { await onRestore(); suspended.current = false; }
    catch (cause) { setMessage({ kind: "error", text: readableError(cause) }); }
  }, [onRestore, value]);

  const commit = useCallback(async (shortcut: string) => {
    setSaving(true);
    setMessage(null);
    try {
      const registered = await onCommit(shortcut);
      suspended.current = false;
      setPreview(registered);
      setRecording(false);
      setMessage({ kind: "success", text: `Shortcut changed to ${displayShortcut(registered)}.` });
    } catch (cause) {
      suspended.current = false;
      setMessage({ kind: "error", text: readableError(cause) });
      setPreview(value);
      setRecording(false);
    } finally { setSaving(false); }
  }, [onCommit, value]);

  useEffect(() => () => {
    if (suspended.current) void onRestore();
  }, [onRestore]);

  useEffect(() => {
    if (!recording || saving) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      event.preventDefault();
      event.stopImmediatePropagation();
      if (event.key === "Escape") { void cancel(); return; }
      const shortcut = shortcutFromEvent(event);
      setPreview(shortcut.preview);
      if (shortcut.complete) void commit(shortcut.complete);
    };
    window.addEventListener("keydown", handleKeyDown, true);
    return () => window.removeEventListener("keydown", handleKeyDown, true);
  }, [cancel, commit, recording, saving]);

  return (
    <div className={`shortcut-recorder ${recording ? "is-recording" : ""}`}>
      <div className="shortcut-display" aria-live="polite">
        <span className="shortcut-label">{recording ? "Press your new shortcut" : "Current shortcut"}</span>
        <div className="key-combination">{renderKeys(preview || "Waiting for keys…")}</div>
        {recording && <small>Use Ctrl, Alt, or Super with another key. Escape cancels.</small>}
      </div>
      <div className="shortcut-controls">
        {recording ? <button type="button" onClick={() => void cancel()} disabled={saving}>Cancel</button> : (
          <button type="button" className="shortcut-primary" onClick={() => void begin()}>Change shortcut</button>
        )}
        <button type="button" onClick={() => void commit(defaultShortcut)} disabled={saving || value === defaultShortcut}>Reset default</button>
      </div>
      {saving && <p className="shortcut-message">Checking shortcut availability…</p>}
      {message && <p className={`shortcut-message ${message.kind}`} role={message.kind === "error" ? "alert" : "status"}>{message.text}</p>}
    </div>
  );
}

function shortcutFromEvent(event: KeyboardEvent): { preview: string; complete?: string } {
  const modifiers: string[] = [];
  if (event.ctrlKey) modifiers.push("Ctrl");
  if (event.altKey) modifiers.push("Alt");
  if (event.shiftKey) modifiers.push("Shift");
  if (event.metaKey) modifiers.push(navigator.userAgent.includes("Mac") ? "Cmd" : "Super");
  const key = keyFromCode(event.code);
  const preview = [...modifiers, ...(key ? [key] : ["…"])].join("+");
  const hasStrongModifier = event.ctrlKey || event.altKey || event.metaKey;
  return { preview, complete: key && hasStrongModifier ? [...modifiers, key].join("+") : undefined };
}

function keyFromCode(code: string): string | null {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit[0-9]$/.test(code)) return code.slice(5);
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(code)) return code;
  const supported: Record<string, string> = {
    Space: "Space", Enter: "Enter", Tab: "Tab", ArrowUp: "ArrowUp", ArrowDown: "ArrowDown",
    ArrowLeft: "ArrowLeft", ArrowRight: "ArrowRight", Comma: "Comma", Period: "Period",
    Slash: "Slash", Semicolon: "Semicolon", Quote: "Quote", BracketLeft: "BracketLeft",
    BracketRight: "BracketRight", Backslash: "Backslash", Minus: "Minus", Equal: "Equal",
    Backquote: "Backquote", Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown",
  };
  return supported[code] ?? null;
}

function renderKeys(shortcut: string) {
  if (shortcut === "Waiting for keys…") return <span className="key-placeholder">{shortcut}</span>;
  return shortcut.split("+").map((key, index) => <span key={`${key}-${index}`} className={key === "…" ? "key pending" : "key"}>{displayKey(key)}</span>);
}

function displayShortcut(shortcut: string) { return shortcut.split("+").map(displayKey).join(" + "); }
function displayKey(key: string) {
  return ({ Ctrl: "Ctrl", Alt: "Alt", Shift: "Shift", Super: "Super", Cmd: "⌘", Space: "Space", ArrowUp: "↑", ArrowDown: "↓", ArrowLeft: "←", ArrowRight: "→" } as Record<string, string>)[key] ?? key;
}
function readableError(cause: unknown) { return cause instanceof Error ? cause.message : String(cause); }
