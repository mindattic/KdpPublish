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
dotnet build KdpPublish\KdpPublish.csproj -c Release
```

Deploy a standalone copy to `C:\Apps\KdpPublish\` (stops any running instance, clean
rebuild, publish, writes a self-redeploying `launch.bat`):

```
powershell -ExecutionPolicy Bypass -File KdpPublish\tools\deploy.ps1 -Launch
```

## Observing a live run

KdpPublish uses AutoWebNav's Live Observation and Session Recording (see the
[AutoWebNav README](https://github.com/mindattic/AutoWebNav#live-observation)), the same as
JobHunt and Automata:

- The window title shows the instance's PID (`KdpPublish - PID: <n>`), and the app registers
  itself in `%LocalAppData%\MindAttic\AutoWebNav\instances` with a local CDP port per pane:
  `panel` (preferred 9368) and `board`, the KDP site (preferred 9369).
- The control panel exposes `window.__awnObserve`: whether a run is going, a one-line summary
  (selected / tracked / counts by status), the selected codes, and the panel log.
- Follow it from the AutoWebNav repo:

  ```text
  node tools/awn-observe.mjs status kdp           both panes now: run state, summary, log tail, KDP page
  node tools/awn-observe.mjs watch kdp            live: every log line, navigation and state change
  node tools/awn-observe.mjs shot kdp --pane board
  ```

- Every session is recorded: everything done in the KDP pane, by you or by the automation, is
  saved to Downloads as `KdpPublish-session-<timestamp>.autowebnav-recording.json`, rewritten as
  it goes and a final time when the window closes. (This replaced the Spectator Mode button.)

## Status from the KDP bookshelf

Whenever the KDP pane lands on the bookshelf and no run is going, the app switches it to 50
books per page and reads each ebook row's status straight off KDP — the JSON KDP puts on each
row's action links (`titleId`, `asin`, `stage`, `liveState`) and the label you see (Live,
Draft, In review, Live Updates publishing). The table's Status column then shows that, matched
to each book by ASIN first and titleId second: Live → Published (or Outdated when a newer
version is on disk), Draft after having been live → "Draft (was live)", In review / Updates
publishing → Publishing. Hover a status for KDP's raw label. Books not on the bookshelf keep
the manifest's own status.

## One-off diagnostic launch modes

- `KdpPublish.exe --diagnose <CODE> [details|content|pricing]` — read-only DOM
  snapshot of a book's KDP page (buttons, checkboxes, dialogs, banners, iframes, body
  text), written to `tools/kdp/diagnose-<CODE>-<step>-<timestamp>.json`.
- `KdpPublish.exe --scan-bookshelf` — reads the real per-book status KDP's own
  bookshelf page displays (Live / In Review / Draft / etc.), written to
  `tools/kdp/bookshelf-scan-<timestamp>.json`. Ground truth over local bookkeeping.
- `KdpPublish.exe --crawl-categories <CODE> <level0> [level1] [...]` /
  `--probe-categories <CODE>` — one-off, read-only documentation passes over KDP's live
  Categories modal (see `CategoryTreeCrawler` in Prose.Core).
- `KdpPublish.exe <CODE1>,<CODE2>,...` — auto-starts the same sequential reconciliation
  pass the panel's "Run Sequential Pass" button triggers, scoped to just these codes, once the
  manifest loads.

All of the above never click or type anything except the auto-run codes mode, which drives
the real publish automation.
