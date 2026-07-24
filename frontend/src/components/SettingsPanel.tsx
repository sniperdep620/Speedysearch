import { useState } from "react";
import { backend } from "../lib/backend";
import type { LayoutMode, PreviewAnchor, UIConfig } from "../types/search";
import ShortcutRecorder from "./ShortcutRecorder";
import "./SettingsPanel.css";

type SettingsTab = "appearance" | "shortcut" | "behavior";

interface SettingsPanelProps {
  config: UIConfig;
  error?: string | null;
  onConfigChange: (config: UIConfig) => void;
  onClose: () => void;
}

export default function SettingsPanel({ config, error, onConfigChange, onClose }: SettingsPanelProps) {
  const [tab, setTab] = useState<SettingsTab>("appearance");
  const commitShortcut = async (shortcut: string) => {
    const registered = await backend.setGlobalHotkey(shortcut);
    onConfigChange({ ...config, global_hotkey: registered });
    return registered;
  };

  return (
    <div className="settings-overlay" onMouseDown={onClose} role="presentation">
      <section className="settings-panel" onMouseDown={(event) => event.stopPropagation()} aria-modal="true" role="dialog" aria-labelledby="settings-title">
        <header><div><span>Preferences</span><h2 id="settings-title">Launcher settings</h2></div><button onClick={onClose} aria-label="Close settings">×</button></header>
        <nav className="settings-tabs" aria-label="Settings sections" role="tablist">
          {(["appearance", "shortcut", "behavior"] as SettingsTab[]).map((item) => <button key={item} role="tab" aria-selected={tab === item} onClick={() => setTab(item)}>{item === "shortcut" ? "Keyboard shortcut" : item}</button>)}
        </nav>

        <div className="settings-content" role="tabpanel">
          {tab === "appearance" && <>
            <div className="settings-intro"><h3>Appearance</h3><p>Choose how results and previews fit your screen.</p></div>
            <label className="setting-row"><span><strong>Layout mode</strong><small>Arrange results in one list or by category.</small></span><select value={config.layout_mode} onChange={(event) => onConfigChange({ ...config, layout_mode: event.target.value as LayoutMode })}><option value="single">Single column</option><option value="three-column">Three columns</option><option value="dynamic">Dynamic</option></select></label>
            <label className="setting-row"><span><strong>Preview position</strong><small>Where result details and actions appear.</small></span><select value={config.preview_anchor} onChange={(event) => onConfigChange({ ...config, preview_anchor: event.target.value as PreviewAnchor })}><option value="right">Right</option><option value="bottom">Bottom</option><option value="left">Left</option></select></label>
            <label className="setting-row opacity-row"><span><strong>Window opacity</strong><small>Adjust the frosted-glass surface.</small></span><div><output>{Math.round(config.opacity * 100)}%</output><input type="range" min="0.5" max="1" step="0.05" value={config.opacity} onChange={(event) => onConfigChange({ ...config, opacity: Number(event.target.value) })} /></div></label>
          </>}

          {tab === "shortcut" && <>
            <div className="settings-intro"><h3>Universal shortcut</h3><p>Open Speedysearch from anywhere, even when another application is focused.</p></div>
            <ShortcutRecorder value={config.global_hotkey} onSuspend={backend.suspendGlobalHotkey} onRestore={backend.restoreGlobalHotkey} onCommit={commitShortcut} />
            <div className="shortcut-help"><strong>Tips for a reliable shortcut</strong><ul><li>Use Ctrl, Alt, or Super plus another key.</li><li>Avoid common desktop shortcuts such as Alt+Tab.</li><li>If a shortcut is taken, your previous one stays active.</li></ul></div>
          </>}

          {tab === "behavior" && <>
            <div className="settings-intro"><h3>Search behavior</h3><p>Control the contextual information shown with results.</p></div>
            <label className="setting-row toggle-row"><span><strong>Show ranking score</strong><small>Display Stage 2 confidence beside each result.</small></span><input type="checkbox" checked={config.show_frecency} onChange={(event) => onConfigChange({ ...config, show_frecency: event.target.checked })} /></label>
            <label className="setting-row toggle-row"><span><strong>Active-app context</strong><small>Prioritize results related to the currently focused app.</small></span><input type="checkbox" checked={config.highlight_active_app} onChange={(event) => onConfigChange({ ...config, highlight_active_app: event.target.checked })} /></label>
          </>}
        </div>
        {error && <p className="settings-error" role="alert">{error}</p>}
        <footer><span>Changes save automatically</span><button onClick={onClose}>Done</button></footer>
      </section>
    </div>
  );
}
