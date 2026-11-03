# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.0.1] - 2026-11-03

### Fixed

- The package no longer fails to compile against the registry install of `com.unity.recorder`
  (`The type or namespace name 'Labeling' does not exist in the namespace 'UnityEditor.Recorder'`).

### Changed

- `RecorderSessionBridge` now ships in this package, in `Editor/RecorderBridge/`, and an assembly definition
  reference compiles it into `Unity.Recorder.Editor`. A local clone of the Recorder and the manual copy step are
  no longer needed.
- The bridge moved to the `UnityEditor.Recorder.SessionBridge` namespace, so a clone that still contains the old
  copied file keeps compiling. That old copy is now unused and can be deleted.

## [1.0.0] - 2026-10-31

### Added

- `GameLabel`: one-line label API for game code, carrying `[Conditional("UNITY_EDITOR")]` so calls and their
  arguments are removed from player builds. Events, spans, signals, markers, chapters and session metadata.
- Automatic sidecar label file next to every Unity Recorder capture, paired by file name.
- Append-as-you-go `.labels.jsonl` stream, so an interrupted recording still leaves a usable record.
- Automatic channels needing no game code: scene, `Time.timeScale`, frame pacing and main camera motion.
- **Project Settings > Labeled Recorder** with a master enable toggle, output location, and per-channel
  switches; the same toggle on the **Tools > Labeled Recorder** menu.
- `RecorderSessionBridge`, an additive file inside the Unity Recorder package that exposes live session state
  without modifying any existing Recorder file.
- Bundled MCP server in `McpServer~/` with nine tools, two resources and two prompts for agent-driven editing.
