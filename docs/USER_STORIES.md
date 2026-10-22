---
codex: 1
project: KdpPublish
code: KP
layer: stories
status: living
updated: 2026-10-12
---
<!-- GENERATED from MindAttic.Tickets/data/Tickets.db by `tickets db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the tickets CLI. -->

# KdpPublish — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

## Priority backlog
_Nothing recorded yet._

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| KP-T1 | 2026-10-12 | docs | closed | Start the permanent ticket log in USER_STORIES.md | — |
| KP-T2 | 2026-10-13 | fix | closed | Repo-root deploy.ps1 (rebuild only when changed, -Force/-Launch), deploy.bat replaces launch.bat, legacy deploy files removed | deploy.ps1 |
| KP-T3 | 2026-10-13 | feature | open | Move to MindAttic.Components.Wpf 5.0.0 and turn on McWindow.DarkTitleBar for its windows | MindAttic.Components.Wpf 5.0.0 |
| KP-T4 | 2026-10-14 | chore | open | NUnit tests for bookshelf status mapping: extract MainWindow.StatusFromShelf/ShelfRowFor/heldButLive (MainWindow.xaml.cs) into a testable helper in a new KdpPublish.Tests NUnit project and cover Live->Published vs Outdated (NeedsRepublish), Live Updates publishing/In review/In Review/Publishing->Publishing, Draft vs Draft (was live) on HAS_BEEN_LIVE, null label falls back to manifest PublicationStatus, unknown label passes through, lookup by ASIN (entry Asin then LocalPublishMarker.Asin) before stale titleId, case-insensitive keys, and heldButLive only when not ReadyToPublish and effective status is Published - this drives the panel's status column and held-but-live warning | — |
| KP-T5 | 2026-10-14 | chore | open | NUnit tests for bookshelf scan parsing: extract ScanShelfAsync's JSON handling (MainWindow.xaml.cs) into a pure parser and cover a JSON-quoted string payload being unwrapped, camelCase ShelfRow deserialization, empty array returning no rows (no table refresh), duplicate ASINs keeping the first row, null/empty ASIN rows excluded from the ASIN index, and case-insensitive titleId/ASIN lookup - a bad parse silently wipes KDP ground truth from the table | — |
| KP-T6 | 2026-10-14 | chore | open | NUnit tests for command-line mode parsing: extract App.OnStartup's argument handling (App.xaml.cs) into a parser and cover --crawl-categories CODE [maxDepth] path... (numeric first arg becomes maxDepth, rest is start path), --probe-categories CODE, --diagnose CODE defaulting step to content, --scan-bookshelf, comma-separated auto-run codes trimmed with empties dropped, and too-few-args falling through to a normal launch - wrong parsing launches a live KDP automation in the wrong mode | — |
| KP-T7 | 2026-10-14 | chore | open | NUnit tests for run-log event formatting: cover MainWindow.FormatEvent/Truncate (MainWindow.xaml.cs, extract to a static helper) for each OperatorEvent kind (AssistantText, ToolStarted args truncated at 120, ToolCompleted with check/cross and result truncated at 160, Error, Info, unknown fallback) and Truncate's exact-length/no-ellipsis boundary - this text is the durable run log persisted via KdpRunLogService | — |
| KP-T8 | 2026-10-15 | chore | closed | Remove duplicate command copies (/quickload, /quicksave) now centralized at the user level (~/.claude/commands) | M.FLOW-T14 |
| KP-T9 | 2026-10-16 | chore | closed | install.ps1 adds this repo's 15 .prose/commands stubs for the shared global commands, so any LLM client finds them | mindattic-agent-standard/install.ps1 run 2026-10-16 |
| KP-T10 | 2026-10-16 | chore | open | Bug hunt: run an exhaustive /bughunt over the whole repo and fix every real bug found (each fix under its own fix ticket), then hunt again; close this only when a full pass finds nothing | user request 2026-10-16: approved repo sweep |
| KP-T11 | 2026-10-16 | chore | open | Run the full unit test suite and fix every failure (each under its own fix ticket) until it passes; put the passing run in --ref. No test project: close this wont-do saying so | user request 2026-10-16: approved repo sweep |
| KP-T12 | 2026-10-16 | chore | open | Clean up code and docs: remove dead code, stale comments and outdated or duplicated docs, and simplify where behaviour doesn't change (/simplify); tests stay green | user request 2026-10-16: approved repo sweep |
| KP-T13 | 2026-10-16 | docs | open | Bring README.md up to date with the code as it is now (what it is, setup, usage, commands, current state), following codex-standard/README-STYLE.md; README.htm regenerated if the repo has one | user request 2026-10-16: approved repo sweep |
| KP-T14 | 2026-10-16 | feature | open | Log to MindAttic.Log (3.0.0): every app/CLI entry point writes to the standalone MindAttic.Log.db Vault assigns this app (VaultPaths.LoggingDbPath, MindAttic.Vault 6.0.0) through Serilog's WriteToMindAtticLog, tagged with its Component; log startup, user actions that change data, failures and unhandled exceptions (UI thread, AppDomain, unobserved tasks); tests point the log at a temp file. Pattern: MindAttic.Workflow WorkflowLog (M.FLOW-T92), MindAttic.Orchestrator OrchestratorLog | M.FLOW-T92 |
| KP-T15 | 2026-10-16 | docs | closed | Update USER_STORIES.md | — |
| KP-T16 | 2026-10-19 | chore | wont-do | Convert tracked source files to LF line endings via .gitattributes, per HOUSE-LAW-13 (low priority) | — |
| KP-T17 | 2026-10-20 | docs | open | Publish KdpPublish docs to GitHub Pages via the shared Toolkit DocFX pipeline: run adopt-docs-site.ps1 (README.md as home page, docs/*.md as articles, API reference only where the code builds in CI), enable Pages with build_type=workflow, set the repo homepage URL, retire tools/build-readme.ps1 and README.htm, confirm the docs Actions run is green and https://mindattic.github.io/KdpPublish/ returns 200. Depends on MAT-T8. | — |
| KP-T18 | 2026-10-20 | chore | closed | Move to MindAttic.Components.Wpf 19.0.0 (Mc->Ma rename): bump the PackageReference and confirm the build; a scan found no Mc-prefixed style/class usages of its own to rename | 2c76a52 |
| KP-T19 | 2026-10-22 | docs | closed | Update USER_STORIES.md | — |
