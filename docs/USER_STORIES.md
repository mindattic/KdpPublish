---
codex: 1
project: KdpPublish
code: KP
layer: stories
status: living
updated: 2026-10-12
---

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
| KP-T8 | 2026-10-15 | chore | closed | Remove duplicate command copies (/quickload, /quicksave) now centralized at the user level (~/.claude/commands) | WF-T14 |
