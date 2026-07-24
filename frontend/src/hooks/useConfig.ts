import { useCallback, useEffect, useRef, useState } from "react";
import { backend } from "../lib/backend";
import { DEFAULT_CONFIG, type UIConfig } from "../types/search";

export function useConfig() {
  const [config, setConfig] = useState<UIConfig>(DEFAULT_CONFIG);
  const [configError, setConfigError] = useState<string | null>(null);
  const saveTimer = useRef<number | null>(null);

  useEffect(() => {
    backend.loadConfig().then(setConfig).catch((cause) => {
      setConfigError(cause instanceof Error ? cause.message : String(cause));
    });
  }, []);

  useEffect(() => () => {
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
  }, []);

  const saveConfig = useCallback((next: UIConfig) => {
    setConfig(next);
    if (saveTimer.current !== null) window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(async () => {
      try {
        setConfig(await backend.saveConfig(next));
        setConfigError(null);
      } catch (cause) {
        setConfigError(cause instanceof Error ? cause.message : String(cause));
      }
    }, 150);
  }, []);

  return { config, saveConfig, configError };
}
