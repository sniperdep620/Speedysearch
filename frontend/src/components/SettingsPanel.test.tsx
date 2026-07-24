import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DEFAULT_CONFIG } from "../types/search";
import SettingsPanel from "./SettingsPanel";

describe("SettingsPanel", () => {
  it("organizes preferences into clear tabs", () => {
    render(<SettingsPanel config={DEFAULT_CONFIG} onConfigChange={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByRole("heading", { name: "Appearance" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "Keyboard shortcut" }));
    expect(screen.getByRole("heading", { name: "Universal shortcut" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Change shortcut" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("tab", { name: "behavior" }));
    expect(screen.getByRole("heading", { name: "Search behavior" })).toBeInTheDocument();
  });
});
