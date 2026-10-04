# MindAttic project agent entrypoint

Read the shared protocol at ..\mindattic-agent-standard\AGENTS.md and this project's README.md.
Common prompt commands are implemented by the shared runner; do not add a second copy under a
provider-specific command folder.

Project context: KdpPublish is the WPF app that republishes/publishes MindAttic books on Amazon
KDP. It was decoupled from the Prose monorepo on 2026-10-04; its publishing logic still lives in
Prose.Core, consumed as a vendored package (lib/local-packages, see NuGet.config). Browser
automation comes from AutoWebNav, also vendored there.

Watching the app while the person talks to you about it: it registers with AutoWebNav's Live
Observation. From the AutoWebNav repo, `node tools/awn-observe.mjs status kdp` (run state, panel
log, what the KDP page shows), `watch kdp` (live event stream — run it under a monitor during a
publish run), `shot kdp --pane board` (screenshot to read). Every session's actions are in
Downloads as `KdpPublish-session-*.autowebnav-recording.json`. Read, don't drive the app behind
the person; publishing is a real, external change on Amazon.
