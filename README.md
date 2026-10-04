# KdpPublish

A WPF desktop app that automates republishing/publishing books on Amazon KDP: a control
panel (checklist, sign-off, live log) next to a real WebView2 pane on `kdp.amazon.com`,
driven by an LLM tool-calling loop (`KdpOperatorService` + the `KdpTools/*` tool set) over
the same browser-automation toolkit ([AutoWebNav](https://github.com/mindattic/AutoWebNav))
that [JobHunt](https://github.com/mindattic/JobHunt) and
[Automata](https://github.com/mindattic/Automata) use.

Decoupled from the [Prose](https://github.com/mindattic/Prose) monorepo on 2026-10-04.
KdpPublish's actual publishing logic (the KDP store, the manifest builder, the operator
tools) still lives in `Prose.Core` — this app consumes it as a versioned package rather
than a sibling project. See `NuGet.config` for how that's wired, and bump the vendored
`Prose.Core` package under `lib/local-packages/` when Prose.Core changes in a way this
app needs.

## Build & run

```
dotnet build Prose.KdpPublish\Prose.KdpPublish.csproj -c Release
```

Deploy a standalone copy to `C:\Apps\KdpPublish\` (stops any running instance, clean
rebuild, publish, writes a self-redeploying `launch.bat`):

```
powershell -ExecutionPolicy Bypass -File Prose.KdpPublish\tools\deploy.ps1 -Launch
```

## Interactivity (CDP)

Every run opens two unauthenticated, local-only Chrome DevTools Protocol ports — one per
WebView2 browser process (same mechanism as JobHunt's `PanelDebugPort`/`BoardDebugPort`):

- `9368` — the control panel pane
- `9369` — the KDP browser pane

The window title shows the running instance's PID (`KdpPublish - PID: <n>`). An external
tool (Playwright's `chromium.connectOverCDP('http://127.0.0.1:9369')`, or anything else
that speaks CDP) can attach to the **already-running** app and read or drive its live DOM —
no relaunch, no special command-line flags needed first.

## One-off diagnostic launch modes

- `Prose.KdpPublish.exe --diagnose <CODE> [details|content|pricing]` — read-only DOM
  snapshot of a book's KDP page (buttons, checkboxes, dialogs, banners, iframes, body
  text), written to `tools/kdp/diagnose-<CODE>-<step>-<timestamp>.json`.
- `Prose.KdpPublish.exe --scan-bookshelf` — reads the real per-book status KDP's own
  bookshelf page displays (Live / In Review / Draft / etc.), written to
  `tools/kdp/bookshelf-scan-<timestamp>.json`. Ground truth over local bookkeeping.
- `Prose.KdpPublish.exe --crawl-categories <CODE> <level0> [level1] [...]` /
  `--probe-categories <CODE>` — one-off, read-only documentation passes over KDP's live
  Categories modal (see `CategoryTreeCrawler` in Prose.Core).
- `Prose.KdpPublish.exe <CODE1>,<CODE2>,...` — auto-starts the same `RunSelectedAsync` flow
  the panel's Start button triggers, once the manifest loads.

All of the above never click or type anything except the auto-run codes mode, which drives
the real publish automation.
