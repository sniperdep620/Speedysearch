# Repository Guidelines

## Project Structure & Module Organization

Speedysearch combines a Rust search engine with two desktop interfaces. Core indexing, ranking, IPC, and the native GUI live in `src/`; Rust integration tests and Criterion benchmarks are in `tests/` and `benches/`. The React/TypeScript interface is under `frontend/src`, with colocated `*.test.tsx` unit tests and Playwright scenarios in `frontend/tests/`. `src-tauri/` provides the thin Tauri shell and system integration. Supporting scripts live in `scripts/`, offline ranker training in `train/`, and user/developer documentation in `docs/`.

## Build, Test, and Development Commands

- `npm install` installs frontend and Tauri tooling.
- `npm run dev` starts Vite at `127.0.0.1:1420`.
- `npm run tauri dev` runs the complete Tauri application in development.
- `npm run build` type-checks TypeScript and builds the frontend.
- `cargo build --release` builds the original native launcher and daemon.
- `cargo test` runs Rust unit and integration tests.
- `npm test` runs Vitest plus the application-identity shell test.
- `npm run test:e2e` runs Playwright; install Chromium once with `npx playwright install chromium`.
- `cargo bench --release` measures search and persistence performance.

## Coding Style & Naming Conventions

Format Rust with `cargo fmt`; use four-space indentation, `snake_case` for functions/modules, and `PascalCase` for types. TypeScript is strict and uses two-space indentation, semicolons, `camelCase` identifiers, and `PascalCase` React components. Keep component styles beside components (for example, `ResultCard.tsx` and `ResultCard.css`). Prefer small modules and preserve nonblocking behavior in the query path.

## Testing Guidelines

Add Rust integration coverage to `tests/integration_tests.rs` or focused `#[cfg(test)]` modules. Name frontend tests `*.test.ts` or `*.test.tsx`; name browser flows `*.spec.ts`. Test user-visible behavior with Testing Library rather than implementation details. Run `cargo test`, `npm test`, and `npm run build` before submitting; include `npm run test:e2e` for interaction changes and benchmarks for ranking or indexing changes.

## Commit & Pull Request Guidelines

Git history is unavailable in this checkout. Use short, imperative commit subjects such as `Fix stale search result selection`, keeping each commit focused. Pull requests should explain the motivation and behavior change, list validation commands, link relevant issues, and include screenshots or recordings for UI changes. Call out configuration, persistence-format, or platform-specific impacts.

## Security & Configuration

Do not commit local clickstream, index, model, or user configuration files. Treat changes to Tauri capabilities, result-opening commands, global shortcuts, and install scripts as security-sensitive; preserve ID validation and least-privilege permissions.
