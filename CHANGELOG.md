# Changelog

All notable changes to Speedysearch are documented here. This project follows
[Semantic Versioning](https://semver.org/) and the
[Keep a Changelog](https://keepachangelog.com/) format.

## [Unreleased]

### Added

- Apache-2.0 licensing and open-source community documentation.
- Deterministic one-command development setup.
- Platform-specific release downloads with stable Windows, Debian, and AppImage
  asset names plus SHA-256 checksums.
- Automated Linux and Windows validation and tag-based release workflows.
- Native Windows search backend integration with the shared Tauri/React
  launcher.

## [0.2.0] - Unreleased

This is the first public preview.

### Added

- Rust indexing, fuzzy matching, frecency, and optional local LightGBM ranking.
- Filesystem watching, bincode persistence, and Unix socket IPC.
- Native Rust launcher and React/Tauri desktop launcher.
- Local clickstream collection and offline ranker training.
- COSMIC desktop shortcut integration with reversible restoration.
