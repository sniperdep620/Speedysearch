import { useEffect } from "react";

interface KeyboardNavOptions {
  enabled?: boolean;
  selectedIndex: number;
  totalResults: number;
  onSelectIndex: (index: number) => void;
  onActivate: (index: number) => void;
  onEscape: () => void;
}

export function useKeyboardNav({ enabled = true, selectedIndex, totalResults, onSelectIndex, onActivate, onEscape }: KeyboardNavOptions) {
  useEffect(() => {
    if (!enabled) return;
    const select = (delta: number) => {
      if (!totalResults) return;
      onSelectIndex((selectedIndex + delta + totalResults) % totalResults);
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      switch (event.key) {
        case "ArrowDown":
        case "ArrowRight":
          event.preventDefault();
          select(1);
          break;
        case "ArrowUp":
        case "ArrowLeft":
          event.preventDefault();
          select(-1);
          break;
        case "Tab":
          event.preventDefault();
          select(event.shiftKey ? -1 : 1);
          break;
        case "Enter":
          if (totalResults) {
            event.preventDefault();
            onActivate(selectedIndex);
          }
          break;
        case "Escape":
          event.preventDefault();
          onEscape();
          break;
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [enabled, onActivate, onEscape, onSelectIndex, selectedIndex, totalResults]);
}
