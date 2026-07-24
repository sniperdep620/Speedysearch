import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import ShortcutRecorder from "./ShortcutRecorder";

describe("ShortcutRecorder", () => {
  it("captures a modified key and commits it", async () => {
    const suspend = vi.fn(() => Promise.resolve());
    const commit = vi.fn((shortcut: string) => Promise.resolve(shortcut));
    render(<ShortcutRecorder value="Ctrl+Space" onSuspend={suspend} onRestore={() => Promise.resolve()} onCommit={commit} />);
    fireEvent.click(screen.getByRole("button", { name: "Change shortcut" }));
    await screen.findByText("Press your new shortcut");
    fireEvent.keyDown(window, { key: "k", code: "KeyK", ctrlKey: true, altKey: true });
    await waitFor(() => expect(commit).toHaveBeenCalledWith("Ctrl+Alt+K"));
    expect(await screen.findByText(/Shortcut changed to Ctrl \+ Alt \+ K/)).toBeInTheDocument();
  });

  it("restores the old shortcut when recording is cancelled", async () => {
    const restore = vi.fn(() => Promise.resolve());
    render(<ShortcutRecorder value="Ctrl+Space" onSuspend={() => Promise.resolve()} onRestore={restore} onCommit={(value) => Promise.resolve(value)} />);
    fireEvent.click(screen.getByRole("button", { name: "Change shortcut" }));
    await screen.findByText("Press your new shortcut");
    fireEvent.keyDown(window, { key: "Escape", code: "Escape" });
    await waitFor(() => expect(restore).toHaveBeenCalledOnce());
    expect(screen.getByText("Current shortcut")).toBeInTheDocument();
  });
});
