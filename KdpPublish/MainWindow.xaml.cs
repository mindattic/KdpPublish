using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using AutoWebNav;
using AutoWebNav.WebView2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using MindAttic.Export.Artifacts;
using Prose.Core.Kdp;
using Prose.Core.Services;
using Prose.Core.Services.Operator;

namespace KdpPublish;

/// <summary>
/// Both panes are plain WebView2 + vanilla JS. The control panel (wwwroot/panel.html) was
/// originally BlazorWebView + Razor components, but that combination had an unresolved click/
/// interactivity bug in this hosting setup — extensively diagnosed (SDK choice, package version
/// alignment, TFM/WinRT projections, stale WebView2 profile cache, window-resize "airspace",
/// running two WebView2-based controls in one window, the Razor source generator flag, and
/// finally `autostart="false"` matching the official MAUI Blazor Hybrid template — none of it
/// fixed it, and the last one broke rendering entirely). A raw HTML button with a plain
/// `onclick` in the exact same page worked instantly, proving the bug was specific to Blazor's
/// own event delegation, not the WebView2/WPF hosting. Plain WebView2 + vanilla JS is the
/// pattern already proven twice elsewhere in this app (this file's KdpBrowser pane, and the
/// browser-extension sidebar's kdp-panel.template.js) — using it here too instead of continuing
/// to chase the Blazor bug.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // Diagnostic dumps (tools/kdp/*.json): the name is computed exactly and a re-run replaces it.
    private static readonly ArtifactOptions DumpArtifact = new() { Existing = ExistingArtifact.Overwrite, SanitizeName = false };

    private static Task WriteDumpAsync(string path, string content) =>
        ArtifactWriter.WriteTextAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path), content, DumpArtifact);

    private List<KdpManifestEntry> lastManifest = new();
    private IKdpBrowser? kdpBrowser;
    /// <summary>Live Observation (AutoWebNav): a CDP port per pane, registered with this PID in
    /// %LocalAppData%\MindAttic\AutoWebNav\instances, so awn-observe (AutoWebNav/tools) can attach
    /// to this running instance and report what it is doing. See README "Observing a live run".</summary>
    private readonly LiveObservation observation = new("KdpPublish");

    /// <summary>Always-on Spectator Mode (AutoWebNav): everything that happens in the KDP pane —
    /// the person's clicks and the automation's — is saved to Downloads as
    /// KdpPublish-session-&lt;timestamp&gt;.autowebnav-recording.json, rewritten as it goes and a final
    /// time when the window closes. Replaced the old Spectator Mode button.</summary>
    private readonly SessionRecording sessionRecording = new("KdpPublish");

    /// <summary>What KDP's own bookshelf says about each ebook, by titleId and by ASIN, from the
    /// last idle visit to it — ground truth for the table's status column. See ScanShelfAsync.</summary>
    private Dictionary<string, ShelfRow> shelfByTitleId = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ShelfRow> shelfByAsin = new(StringComparer.OrdinalIgnoreCase);
    private bool shelfScanBusy;

    /// <summary>What the sequential reconciliation pass actually read off KDP's Content page for
    /// each book it visited, keyed by Code — ground truth for the panel's checkmark, same
    /// per-run-only lifetime as shelfByTitleId/shelfByAsin. See RunReconcilePassAsync.</summary>
    private readonly Dictionary<string, KdpReconcileService.GroundTruthResult> groundTruthByCode = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? runCts;
    private KdpRunLogService? runLog;
    private Guid? currentRunId;
    private readonly HashSet<string>? autoRunCodes;
    private bool autoRunTriggered;
    private readonly (string NodeCode, int? MaxDepth, string[] StartPath)? crawlCategories;
    private readonly string? probeCategoriesNodeCode;
    private readonly (string NodeCode, string Step)? diagnoseRequest;
    private readonly bool scanBookshelf;
    private bool crawlTriggered;
    private bool diagnoseTriggered;
    private bool scanBookshelfTriggered;

    /// <summary>Preferred CDP ports (LiveObservation falls back to a free one if taken, and the
    /// registry always records the real one). Distinct from JobHunt's 9366/9367 and Automata's
    /// 9370/9371.</summary>
    private const int PanelDebugPort = 9368;
    private const int BoardDebugPort = 9369;

    public MainWindow(string[]? autoRunCodes = null, (string NodeCode, int? MaxDepth, string[] StartPath)? crawlCategories = null, string? probeCategoriesNodeCode = null, (string NodeCode, string Step)? diagnoseRequest = null, bool scanBookshelf = false)
    {
        InitializeComponent();
        this.autoRunCodes = autoRunCodes is { Length: > 0 } ? autoRunCodes.ToHashSet() : null;
        this.crawlCategories = crawlCategories;
        this.probeCategoriesNodeCode = probeCategoriesNodeCode;
        this.diagnoseRequest = diagnoseRequest;
        this.scanBookshelf = scanBookshelf;

        // "KdpPublish - PID: n" — which running instance an observer is attached to, at a glance.
        Title = observation.WindowTitle;
        Closed += (_, _) =>
        {
            sessionRecording.Dispose();   // final write of the session recording to Downloads
            observation.Dispose();        // leave the instance registry
        };

#if DEBUG
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) ControlPanel.CoreWebView2?.OpenDevToolsWindow();
            if (e.Key == Key.F10) KdpBrowser.CoreWebView2?.OpenDevToolsWindow();
        };
#endif

        _ = InitializeControlPanelAsync();
        _ = InitializeKdpBrowserAsync();
    }

    private async Task InitializeControlPanelAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MindAttic", "KdpPublish", "ControlPanelWebView2");
        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: observation.OptionsFor("panel", PanelDebugPort));
        await ControlPanel.EnsureCoreWebView2Async(env);

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        ControlPanel.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "kdppublish.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        ControlPanel.CoreWebView2.WebMessageReceived += OnControlPanelMessage;
        ControlPanel.CoreWebView2.Navigate("https://kdppublish.local/panel.html");
    }

    /// <summary>
    /// A dedicated user-data folder (separate from any installed Chrome/Edge profile) means the
    /// Amazon login persists across app restarts without touching the user's regular browser
    /// profile at all — first run needs a real login, every run after that doesn't.
    /// </summary>
    private async Task InitializeKdpBrowserAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MindAttic", "KdpPublish", "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: observation.OptionsFor("board", BoardDebugPort));
        await KdpBrowser.EnsureCoreWebView2Async(env);

        // Any "open in new window" request (target="_blank", window.open(), etc.) redirects
        // back into this same pane instead of spawning an untracked standalone popup window —
        // confirmed live: KDP's page opened one of these mid-run (a CreateSpace-transfer link),
        // and by default WebView2 falls back to a separate top-level msedgewebview2 window that
        // neither this app nor the operator loop has any visibility into or control over.
        KdpBrowser.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            KdpBrowser.CoreWebView2.Navigate(args.Uri);
        };

        // Any native alert()/confirm()/beforeunload dialog the KDP page raises would otherwise
        // show as an unattended modal on top of this pane with nobody to click it — and every
        // ExecuteScriptAsync/CDP call the tools issue afterward hangs forever waiting for the
        // blocked renderer, with no timeout anywhere to break it. Confirmed as the real cause of
        // a full-sweep run silently freezing at 0% CPU for 100+ minutes (still "Responding" per
        // Windows since the WPF message pump itself was never blocked, only the renderer's JS
        // thread was). Auto-accepting keeps the automation's own "fully unattended through
        // Publish" design intact instead of depending on a human happening to notice and click it.
        KdpBrowser.CoreWebView2.ScriptDialogOpening += (_, args) =>
        {
            _ = PostLogAsync($"⚠ KDP page raised a {args.Kind} dialog: \"{args.Message}\" — auto-accepting.");
            args.Accept();
        };

        // Always-on session recording of this pane (installToolkit: the KDP pane's own surface
        // doesn't install AutoWebNav's fingerprinting toolkit, and the recorder needs it).
        await sessionRecording.AttachAsync("board", KdpBrowser.CoreWebView2, installToolkit: true);

        // Every idle visit to the bookshelf: 50 rows per page, then read each ebook's real status
        // off it for the table. Never during a run — the operator searches this same page.
        KdpBrowser.CoreWebView2.NavigationCompleted += async (_, args) =>
        {
            if (args.IsSuccess && KdpBrowser.CoreWebView2.Source.Contains("/bookshelf", StringComparison.OrdinalIgnoreCase))
                await RefreshShelfAsync();
        };

        KdpBrowser.CoreWebView2.Navigate("https://kdp.amazon.com/en_US/bookshelf");

        kdpBrowser = new WebView2KdpBrowser(KdpBrowser.CoreWebView2);

        if (crawlCategories != null && !crawlTriggered)
        {
            crawlTriggered = true;
            _ = RunCrawlCategoriesAsync(crawlCategories.Value.NodeCode, crawlCategories.Value.StartPath, crawlCategories.Value.MaxDepth);
        }
        if (probeCategoriesNodeCode != null && !crawlTriggered)
        {
            crawlTriggered = true;
            _ = RunProbeCategoriesAsync(probeCategoriesNodeCode);
        }
        if (diagnoseRequest != null && !diagnoseTriggered)
        {
            diagnoseTriggered = true;
            _ = RunDiagnoseAsync(diagnoseRequest.Value.NodeCode, diagnoseRequest.Value.Step);
        }
        if (scanBookshelf && !scanBookshelfTriggered)
        {
            scanBookshelfTriggered = true;
            _ = RunScanBookshelfAsync();
        }
    }

    /// <summary>
    /// Navigates to KDP's own bookshelf page and reads, per row, the REAL status text Amazon
    /// displays (Live, In Review, Draft, Pre-order, etc.) alongside title/ASIN — ground truth,
    /// never our own DB bookkeeping. Read-only: never clicks anything. Written to
    /// tools/kdp/bookshelf-scan-&lt;timestamp&gt;.json.
    /// </summary>
    private async Task RunScanBookshelfAsync()
    {
        if (kdpBrowser == null) { Console.WriteLine("Scan aborted: KDP browser pane not ready."); return; }

        Console.WriteLine("[scan-bookshelf] Navigating to the bookshelf…");
        var navDone = new TaskCompletionSource();
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult();
        KdpBrowser.CoreWebView2.NavigationCompleted += OnNav;
        KdpBrowser.CoreWebView2.Navigate("https://kdp.amazon.com/en_US/bookshelf");
        await Task.WhenAny(navDone.Task, Task.Delay(15000));
        KdpBrowser.CoreWebView2.NavigationCompleted -= OnNav;
        await Task.Delay(3000);

        var dump = await kdpBrowser.EvalAsync(BookshelfScanScript, CancellationToken.None);

        var repoRoot = Prose.Core.Services.KdpManifestService.FindRepoRoot();
        var outPath = Path.Combine(repoRoot, "tools", "kdp", $"bookshelf-scan-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await WriteDumpAsync(outPath, dump);
        Console.WriteLine($"[scan-bookshelf] Wrote {outPath}");
        Console.WriteLine(dump);
    }

    private const string BookshelfScanScript = """
    (function() {
        function short(s, n) {
            s = (s || '').trim().replace(/\s+/g, ' ');
            return s.length > n ? s.slice(0, n) + '…' : s;
        }
        var statusWords = /\b(live|in review|draft|pre-?order|publishing|blocked|rejected|archived)\b/i;

        var links = Array.from(document.querySelectorAll('a[id^="digital_edit_content-"]'));
        var rows = links.map(function (el) {
            var raw = el.getAttribute('data-link-parameters');
            var parsed = {};
            try { parsed = JSON.parse(raw) || {}; } catch (e) {}

            // Walk up to the card/row container and pull every short, visible text node that
            // looks like a status word — the exact element/class KDP uses for this chip has
            // drifted before (see ClickButtonTool/GetPageStatusTool remarks), so match by
            // content, not by guessing a selector.
            var card = el.closest('div');
            for (var depth = 0; depth < 6 && card && card.parentElement; depth++) {
                card = card.parentElement;
            }
            var statusTexts = [];
            if (card) {
                Array.from(card.querySelectorAll('*')).forEach(function (node) {
                    var t = (node.textContent || '').trim();
                    if (t.length > 0 && t.length < 40 && statusWords.test(t) && node.children.length === 0) {
                        statusTexts.push(t);
                    }
                });
            }

            return {
                linkTitle: parsed.title || null,
                asin: parsed.asin || parsed.ASIN || null,
                statusTexts: Array.from(new Set(statusTexts)).slice(0, 5),
                cardTextExcerpt: short(card ? card.textContent : '', 300),
            };
        });

        return JSON.stringify({ url: location.href, rowCount: rows.length, rows: rows }, null, 2);
    })()
    """;

    /// <summary>
    /// One-off, read-only DOM snapshot of a book's KDP page — buttons (text/visible/disabled),
    /// checkboxes (text/checked/visible), dialog/modal and banner/alert text (visible or not,
    /// flagged either way), any iframes, and a body-text excerpt. Written to
    /// tools/kdp/diagnose-&lt;CODE&gt;-&lt;step&gt;-&lt;timestamp&gt;.json and echoed to stdout.
    /// Exists so a tool that silently stops advancing (e.g. repeated "Save and Continue" clicks
    /// that never leave the Content step) can be diagnosed against the real live DOM instead of
    /// guessed at from the operator log alone. Never clicks or types anything.
    /// </summary>
    private async Task RunDiagnoseAsync(string nodeCode, string step)
    {
        if (kdpBrowser == null) { Console.WriteLine("Diagnose aborted: KDP browser pane not ready."); return; }
        var detailsUrl = await ResolveDetailsUrlAsync(nodeCode);
        if (detailsUrl == null) return;
        var url = detailsUrl.Replace("/details", $"/{step}");

        Console.WriteLine($"[diagnose] Navigating to {url}");
        var navDone = new TaskCompletionSource();
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult();
        KdpBrowser.CoreWebView2.NavigationCompleted += OnNav;
        KdpBrowser.CoreWebView2.Navigate(url);
        await Task.WhenAny(navDone.Task, Task.Delay(15000));
        KdpBrowser.CoreWebView2.NavigationCompleted -= OnNav;
        // The nav-completed event fires on the shell document; KDP's SPA content renders after.
        await Task.Delay(3000);

        var dump = await kdpBrowser.EvalAsync(DiagnoseScript, CancellationToken.None);

        var repoRoot = Prose.Core.Services.KdpManifestService.FindRepoRoot();
        var outPath = Path.Combine(repoRoot, "tools", "kdp", $"diagnose-{nodeCode}-{step}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await WriteDumpAsync(outPath, dump);
        Console.WriteLine($"[diagnose] Wrote {outPath}");
        Console.WriteLine(dump);
    }

    private const string DiagnoseScript = """
    (function() {
        function isVisible(el) {
            var r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0;
        }
        function short(s, n) {
            s = (s || '').trim().replace(/\s+/g, ' ');
            return s.length > n ? s.slice(0, n) + '…' : s;
        }

        var buttons = Array.from(document.querySelectorAll('button, input[type=submit], input[type=button], a'))
            .map(function (el) {
                var text = el.tagName === 'INPUT'
                    ? (el.value || '')
                    : (el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '');
                return {
                    tag: el.tagName.toLowerCase(),
                    text: short(text, 80),
                    visible: isVisible(el),
                    disabled: !!el.disabled,
                    id: el.id || null,
                };
            })
            .filter(function (b) { return b.text.length > 0; });

        var checkboxes = Array.from(document.querySelectorAll('input[type=checkbox], [role=checkbox]'))
            .map(function (el) {
                var label = el.closest('label');
                return {
                    checked: el.tagName === 'INPUT' ? el.checked : el.getAttribute('aria-checked'),
                    text: short(label ? label.textContent : (el.parentElement ? el.parentElement.textContent : ''), 100),
                    visible: isVisible(el),
                };
            });

        var dialogs = Array.from(document.querySelectorAll('[role="dialog"], [class*="modal" i], [class*="popover" i]'))
            .map(function (el) { return { visible: isVisible(el), text: short(el.textContent, 300) }; });

        var banners = Array.from(document.querySelectorAll(
            '[class*="alert"], [class*="success"], [class*="error"], [class*="banner"], [role="alert"]'
        )).map(function (el) { return { visible: isVisible(el), text: short(el.textContent, 300) }; });

        var iframes = Array.from(document.querySelectorAll('iframe'))
            .map(function (el) { return { src: el.src, title: el.title, visible: isVisible(el) }; });

        var h1 = document.querySelector('h1, h2');

        // Deep-dive on any unchecked box whose nearby text mentions "confirm" — the specific
        // widget blocking Save and Continue on a republish, per live diagnosis 2026-10-04. Dumps
        // outerHTML, computed clickability, and what document.elementFromPoint actually reports
        // at the click coordinate the automation computes, to catch an overlapping element
        // intercepting the click.
        var confirmBoxes = Array.from(document.querySelectorAll('input[type=checkbox], [role=checkbox]'))
            .filter(function (el) {
                var checked = el.tagName === 'INPUT' ? el.checked : el.getAttribute('aria-checked') === 'true';
                if (checked) return false;
                var ctx = (el.closest('div') ? el.closest('div').textContent : '') || '';
                return /confirm/i.test(ctx);
            })
            .map(function (el) {
                // Mirror KdpFormHelpers.LocateNextScript exactly: scroll into view (same call,
                // same options) THEN read the rect — reproducing whatever timing bug (if any)
                // the production code has, rather than a cleaner diagnostic-only measurement.
                var beforeScroll = el.getBoundingClientRect();
                el.scrollIntoView({ block: 'center', inline: 'center' });
                var rSync = el.getBoundingClientRect(); // read immediately, same as production

                var htmlScrollBehavior = getComputedStyle(document.documentElement).scrollBehavior;
                var bodyScrollBehavior = getComputedStyle(document.body).scrollBehavior;

                var cx = rSync.left + rSync.width / 2, cy = rSync.top + rSync.height / 2;
                var atPointSync = document.elementFromPoint(cx, cy);

                // The checkbox div's OWN rect spans the full row, but an icon-only control
                // commonly renders its actual visual/hit target in a narrow child near the
                // start — check whether clicking THAT child's center actually lands on the
                // checkbox (or one of its descendants) instead of the sibling label text.
                var icon = el.querySelector('svg, [class*="icon" i]') || el.firstElementChild;
                var iconHit = null;
                if (icon) {
                    var ir = icon.getBoundingClientRect();
                    var icx = ir.left + ir.width / 2, icy = ir.top + ir.height / 2;
                    var atIcon = document.elementFromPoint(icx, icy);
                    iconHit = {
                        rect: { x: ir.x, y: ir.y, width: ir.width, height: ir.height },
                        elementAtPoint: atIcon ? {
                            tag: atIcon.tagName.toLowerCase(),
                            isSameElement: atIcon === el,
                            isDescendantOfCheckbox: el.contains(atIcon),
                        } : null,
                    };
                }

                return {
                    tag: el.tagName.toLowerCase(),
                    role: el.getAttribute('role'),
                    outerHTML: short(el.outerHTML, 500),
                    rectBeforeScroll: { x: beforeScroll.x, y: beforeScroll.y, width: beforeScroll.width, height: beforeScroll.height },
                    rectImmediatelyAfterScrollIntoView: { x: rSync.x, y: rSync.y, width: rSync.width, height: rSync.height },
                    scrollBehaviorCss: { html: htmlScrollBehavior, body: bodyScrollBehavior },
                    windowScrollY: window.scrollY,
                    visible: isVisible(el),
                    pointerEvents: getComputedStyle(el).pointerEvents,
                    elementAtClickPointImmediatelyAfterScroll: atPointSync ? {
                        tag: atPointSync.tagName.toLowerCase(),
                        isSameElement: atPointSync === el,
                        outerHTML: short(atPointSync.outerHTML, 300),
                    } : null,
                    iconChildHit: iconHit,
                    parentOuterHTML: short(el.parentElement ? el.parentElement.outerHTML : '', 800),
                };
            });

        return JSON.stringify({
            url: location.href,
            title: document.title,
            heading: h1 ? h1.textContent.trim() : null,
            buttons: buttons,
            checkboxes: checkboxes,
            confirmBoxes: confirmBoxes,
            dialogs: dialogs,
            banners: banners,
            iframes: iframes,
            bodyTextExcerpt: short(document.body.innerText, 4000),
        }, null, 2);
    })()
    """;

    private async Task RunProbeCategoriesAsync(string nodeCode)
    {
        if (kdpBrowser == null) { Console.WriteLine("Probe aborted: KDP browser pane not ready."); return; }
        var detailsUrl = await ResolveDetailsUrlAsync(nodeCode);
        if (detailsUrl == null) return;

        Console.WriteLine($"[probe-categories] Opening categories modal via {nodeCode}'s Details page…");
        var result = await Prose.Core.Services.Operator.KdpTools.CategoryTreeCrawler.ProbeAsync(kdpBrowser, detailsUrl, CancellationToken.None);
        Console.WriteLine("[probe-categories] Result:");
        Console.WriteLine(result);

        var repoRoot = Prose.Core.Services.KdpManifestService.FindRepoRoot();
        var outPath = System.IO.Path.Combine(repoRoot, "tools", "kdp", "category-probe.json");
        await WriteDumpAsync(outPath, result);
        Console.WriteLine($"[probe-categories] Wrote {outPath}");
    }

    private async Task<string?> ResolveDetailsUrlAsync(string nodeCode)
    {
        await App.StoreReady;
        var title = await App.Services.GetRequiredService<KdpStore>().GetTitleAsync(nodeCode);
        if (string.IsNullOrWhiteSpace(title?.TitleId))
        {
            Console.WriteLine($"Aborted: no titleId recorded for '{nodeCode}' in the KDP store (prose --kdp-store --code {nodeCode}).");
            return null;
        }
        return $"https://kdp.amazon.com/en_US/title-setup/kindle/{title.TitleId}/details";
    }

    /// <summary>
    /// One-off, read-only documentation pass — see <see cref="Prose.Core.Services.Operator.KdpTools.CategoryTreeCrawler"/>.
    /// Resolves the given NodeCode's titleId from the KDP store purely to have a Details page to
    /// open the Categories modal on; never touches that book's real category assignment (every
    /// visit reloads the page fresh instead of saving). The tree is saved to the KDP store and
    /// exported to tools/kdp/category-tree-&lt;slug&gt;.json (the committed reference copy).
    /// </summary>
    private async Task RunCrawlCategoriesAsync(string nodeCode, string[] startPath, int? maxDepth)
    {
        if (kdpBrowser == null) { Console.WriteLine("Crawl aborted: KDP browser pane not ready."); return; }
        var detailsUrl = await ResolveDetailsUrlAsync(nodeCode);
        if (detailsUrl == null) return;

        var repoRoot = Prose.Core.Services.KdpManifestService.FindRepoRoot();
        Console.WriteLine($"[crawl-categories] Starting: {string.Join(" > ", startPath)} (via {nodeCode}'s Details page, never saved, maxDepth={maxDepth?.ToString() ?? "unbounded"})");

        var tree = await Prose.Core.Services.Operator.KdpTools.CategoryTreeCrawler.CrawlAsync(
            kdpBrowser, detailsUrl, startPath, msg => Console.WriteLine($"[crawl-categories] {msg}"), CancellationToken.None, maxDepth);

        var slug = string.Join("-", startPath).ToLowerInvariant().Replace(" ", "-").Replace("&", "and");
        var root = System.Text.Json.JsonSerializer.Deserialize<KdpCategoryNode>(tree.ToJsonString(), KdpJsonTransfer.CategoryTreeJson)!;
        await App.Services.GetRequiredService<KdpStore>().SaveCategoryTreeAsync(new KdpCategoryTree
        {
            Slug = slug,
            StartPath = startPath.ToList(),
            Crawl = new KdpCrawlInfo { Via = nodeCode, MaxDepth = maxDepth, CrawledAt = DateTimeOffset.UtcNow },
            Tree = root,
        });
        var outPath = System.IO.Path.Combine(repoRoot, "tools", "kdp", $"{KdpJsonTransfer.CategoryTreePrefix}{slug}.json");
        await WriteDumpAsync(outPath, KdpJsonTransfer.RenderCategoryTree(root));
        Console.WriteLine($"[crawl-categories] Done. Saved '{slug}' to the KDP store and exported {outPath}");
    }

    private async void OnControlPanelMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(args.WebMessageAsJson); }
        catch { return; }
        var action = msg?["action"]?.GetValue<string>();

        try
        {
            switch (action)
            {
                case "ready":
                    await EnsureStoreAsync();
                    await RefreshManifestAsync();
                    if (autoRunCodes != null && !autoRunTriggered)
                    {
                        autoRunTriggered = true;
                        await PostLogAsync($"Auto-run requested via command line: {string.Join(", ", autoRunCodes)}");
                        // The control panel (this message) and the KDP browser pane initialize
                        // concurrently — the panel usually finishes first, so give the KDP pane
                        // (navigate to bookshelf + WebView2 env creation) a little room to catch
                        // up rather than aborting on a kdpBrowser==null race.
                        for (var i = 0; i < 50 && kdpBrowser == null; i++)
                            await Task.Delay(200);
                        _ = RunReconcilePassAsync(autoRunCodes);
                    }
                    break;
                case "run-pass":
                    _ = RunReconcilePassAsync();
                    break;
                case "mark-unpublished":
                    var unpublishCodes = msg!["codes"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
                    await MarkUnpublishedAsync(unpublishCodes);
                    break;
                case "get-cover":
                    var coverCode = msg!["code"]!.GetValue<string>();
                    await SendCoverImageAsync(coverCode);
                    break;
                case "sign-off":
                case "hold":
                    var gateCodes = msg!["codes"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
                    await SetSignOffAsync(gateCodes, ready: action == "sign-off");
                    break;
                case "import-json":
                    await ImportJsonAsync();
                    break;
                case "export-json":
                    await ExportJsonAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            await PostLogAsync($"⚠ Control panel message '{action}' failed: {ex.Message}");
        }
    }

    /// <summary>Waits for the KDP store's startup migration / first-run import (App.StoreReady)
    /// and reports what it holds, or why it failed, in the panel log.</summary>
    private async Task EnsureStoreAsync()
    {
        try
        {
            await App.StoreReady;
            var status = await App.Services.GetRequiredService<KdpStore>().GetStatusAsync();
            var firstRun = status.Imports.FirstOrDefault(i => i.Kind == KdpImportKind.FirstRun);
            await PostLogAsync($"KDP store {KdpPaths.ResolveDbPath()} — {status.Counts}." +
                               (firstRun == null ? " (First-run JSON import not done yet: tools/kdp/title-ids.json not found.)" : ""));
        }
        catch (Exception ex)
        {
            await PostLogAsync($"⚠ KDP store failed to open: {ex.Message}");
        }
    }

    /// <summary>The panel's Sign Off / Hold buttons — the human publish gate that creating or
    /// deleting a book's .publish marker file used to be.</summary>
    private async Task SetSignOffAsync(HashSet<string> codes, bool ready)
    {
        if (codes.Count == 0) return;
        var changed = await App.Services.GetRequiredService<KdpStore>().SetSignOffAsync(codes, ready, "kdppublish");
        await PostLogAsync($"{(ready ? "Signed off" : "Held back")} {changed} book(s): {string.Join(", ", codes)}");
        await RefreshManifestAsync();
    }

    /// <summary>"Import JSON": merges tools/kdp (title-ids.json, category trees, logs, and any
    /// publish-markers/ folder an export left there) into the KDP store — the same as
    /// <c>prose --kdp-import</c>.</summary>
    private async Task ImportJsonAsync()
    {
        var from = KdpPaths.ResolveToolsDir();
        var counts = await App.Services.GetRequiredService<KdpJsonTransfer>().ImportAsync(new KdpImportRequest { FromDir = from });
        await PostLogAsync($"Imported JSON from {from}: {counts}.");
        await RefreshManifestAsync();
    }

    /// <summary>"Export JSON": writes the KDP store out as JSON in the original shapes to a new
    /// folder under %LocalAppData% — the same as <c>prose --kdp-export</c>.</summary>
    private async Task ExportJsonAsync()
    {
        var to = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MindAttic", "Prose", "kdp-export", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var counts = await App.Services.GetRequiredService<KdpJsonTransfer>().ExportAsync(new KdpExportRequest { ToDir = to });
        await PostLogAsync($"Exported JSON to {to}: {counts}.");
    }

    /// <summary>One ebook row as KDP's own bookshelf shows it right now.</summary>
    private sealed record ShelfRow(string TitleId, string? Asin, string Title, string? Label, string? Stage, string? LiveState);

    /// <summary>
    /// On an idle bookshelf visit: switch to 50 rows per page (so nearly every book is on one
    /// page), then read each ebook row's real status and refresh the table from it. Skipped while a
    /// run is active — the operator searches this same page and must not have it changed under it.
    /// </summary>
    private async Task RefreshShelfAsync()
    {
        if (kdpBrowser == null || runCts != null || shelfScanBusy) return;
        shelfScanBusy = true;
        try
        {
            await Task.Delay(1500);   // the bookshelf table renders after NavigationCompleted
            if (runCts != null) return;
            if (await EnsureFiftyPerPageAsync()) await Task.Delay(3500);
            if (runCts != null) return;
            if (await ScanShelfAsync()) await RefreshManifestAsync();
        }
        catch (Exception ex)
        {
            await PostLogAsync($"⚠ Bookshelf read failed: {ex.Message}");
        }
        finally { shelfScanBusy = false; }
    }

    /// <summary>True when it changed the page size (the table then reloads). Opening the AUI
    /// dropdown first is what makes its option links clickable — confirmed live 2026-10-04.</summary>
    private async Task<bool> EnsureFiftyPerPageAsync()
    {
        // The pager renders well after NavigationCompleted on a cold start — confirmed live: at a
        // fixed 1.5 s it wasn't there yet and the step silently did nothing. Wait for it.
        for (var i = 0; i < 20; i++)
        {
            var present = await kdpBrowser!.EvalAsync(
                "document.getElementById('refreshedbookshelftable-records-per-page-dropdown') ? 'yes' : 'no'", CancellationToken.None);
            if (present.Contains("yes")) break;
            await Task.Delay(500);
        }
        // Through the dropdown's native <select> (value + change event), not by clicking the
        // popover's option link: confirmed live 2026-10-04 that this works without opening the
        // popover, while the click route silently did nothing on a cold start. Retried, and
        // verified against the button's own label, because Amazon's handler may not be bound yet.
        const string SetFifty = """
            (function () {
                var b = document.getElementById('refreshedbookshelftable-records-per-page-dropdown');
                var s = document.getElementById('refreshedbookshelftable-records-per-page-dropdown-option');
                if (!b || !s) return 'missing';
                if (/50 Per Page/.test(b.textContent)) return 'done';
                s.value = '50';
                s.dispatchEvent(new Event('change', { bubbles: true }));
                return 'set';
            })()
            """;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await kdpBrowser.EvalAsync(SetFifty, CancellationToken.None);
            if (result.Contains("done")) return attempt > 0;
            if (result.Contains("missing")) return false;
            await Task.Delay(1500);
            var now = await kdpBrowser.EvalAsync(
                "(document.getElementById('refreshedbookshelftable-records-per-page-dropdown') || {}).textContent || ''", CancellationToken.None);
            if (now.Contains("50 Per Page"))
            {
                await PostLogAsync("Bookshelf set to 50 per page.");
                return true;
            }
        }
        await PostLogAsync("⚠ Couldn't switch the bookshelf to 50 per page — reading the rows that are showing.");
        return false;
    }

    /// <summary>
    /// Reads every ebook row on the bookshelf. Each row's action links carry KDP's own JSON
    /// (data-link-parameters: titleId, asin, stage LIVE/NOT_LIVE, liveState CURRENTLY_LIVE/
    /// HAS_BEEN_LIVE/NOT_LIVE), and the row shows the status label a person sees ("Live", "Draft",
    /// "In review", "Live Updates publishing"). Print-format rows are ignored.
    /// </summary>
    private async Task<bool> ScanShelfAsync()
    {
        var json = await kdpBrowser!.EvalAsync("""
            (function () {
                var out = [];
                var els = document.querySelectorAll('[data-link-parameters]');
                for (var i = 0; i < els.length; i++) {
                    var p; try { p = JSON.parse(els[i].getAttribute('data-link-parameters')); } catch (e) { continue; }
                    if (!p || !p.titleId || !/digital/.test((p.action || '') + (p.id || ''))) continue;
                    if (out.some(function (o) { return o.titleId === p.titleId; })) continue;
                    var card = els[i];
                    while (card.parentElement && ((card.innerText || '').match(/Kindle eBook/g) || []).length < 1) card = card.parentElement;
                    while (card.parentElement && ((card.parentElement.innerText || '').match(/Kindle eBook/g) || []).length === 1) card = card.parentElement;
                    var text = (card.innerText || '').replace(/\s+/g, ' ');
                    var m = text.match(/Kindle eBook (Live Updates publishing|Live|Draft|In [Rr]eview|Publishing|Blocked|Unpublished)/);
                    var a = text.match(/ASIN:\s*([A-Z0-9]{10})/);
                    out.push({ titleId: p.titleId, asin: p.asin || (a ? a[1] : null), title: p.title || '',
                               label: m ? m[1] : null, stage: p.stage || null, liveState: p.liveState || null });
                }
                return JSON.stringify(out);
            })()
            """, CancellationToken.None);
        // EvalAsync hands back the script's string; tolerate it arriving JSON-quoted as well.
        if (json.StartsWith('"')) json = JsonDocument.Parse(json).RootElement.GetString() ?? "[]";
        var rows = JsonSerializer.Deserialize<List<ShelfRow>>(json, JsonOpts) ?? [];
        if (rows.Count == 0) return false;
        shelfByTitleId = rows.ToDictionary(r => r.TitleId, StringComparer.OrdinalIgnoreCase);
        shelfByAsin = rows.Where(r => !string.IsNullOrEmpty(r.Asin)).GroupBy(r => r.Asin!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        await PostLogAsync($"Read {rows.Count} ebook(s) off the KDP bookshelf: {rows.Count(r => r.Label == "Live")} Live, " +
                           $"{rows.Count(r => r.Label == "Draft")} Draft, {rows.Count(r => r.Label is not ("Live" or "Draft"))} other.");
        return true;
    }

    /// <summary>The book's row on the bookshelf: by ASIN first (stable for a published book), then
    /// by titleId — the KDP store's titleId has been seen to go stale (TWD/TWU/PXL, 2026-10-04).</summary>
    private ShelfRow? ShelfRowFor(KdpManifestEntry e)
    {
        var asin = e.Asin ?? e.LocalPublishMarker?.Asin;
        if (!string.IsNullOrEmpty(asin) && shelfByAsin.TryGetValue(asin, out var byAsin)) return byAsin;
        if (!string.IsNullOrEmpty(e.KdpTitleId) && shelfByTitleId.TryGetValue(e.KdpTitleId, out var byId)) return byId;
        return null;
    }

    /// <summary>The table's status from what KDP itself shows, when the book is on the bookshelf;
    /// otherwise the manifest's own computation. "Live" with a newer version on disk is still
    /// "Outdated" — live, but behind.</summary>
    private static string StatusFromShelf(ShelfRow row, KdpManifestEntry e) => row.Label switch
    {
        "Live" => e.NeedsRepublish ? "Outdated" : "Published",
        "Live Updates publishing" or "In review" or "In Review" or "Publishing" => "Publishing",
        "Draft" when row.LiveState == "HAS_BEEN_LIVE" => "Draft (was live)",
        "Draft" => "Draft",
        null => e.PublicationStatus,
        var other => other,
    };

    private async Task RefreshManifestAsync()
    {
        var manifestService = App.Services.GetRequiredService<KdpManifestService>();
        lastManifest = await manifestService.BuildAsync(KdpManifestService.FindRepoRoot());
        // The panel gets each entry plus what the bookshelf says: publicationStatus is replaced by
        // KDP's own status when the book was found there, and kdpStatus carries KDP's raw label.
        var rows = new System.Text.Json.Nodes.JsonArray();
        foreach (var e in lastManifest)
        {
            var node = JsonSerializer.SerializeToNode(e, JsonOpts)!.AsObject();
            if (ShelfRowFor(e) is { } row)
            {
                node["publicationStatus"] = StatusFromShelf(row, e);
                node["kdpStatus"] = row.Label;
                node["kdpLiveState"] = row.LiveState;
                node["shelfTitleId"] = row.TitleId;
            }

            // Ground truth from this session's sequential pass, if it's visited this book yet —
            // what drives the panel's read-only checkmark (see KdpReconcileService).
            if (groundTruthByCode.TryGetValue(e.Code, out var ground))
            {
                node["kdpGroundTruthFilename"] = ground.Filename;
                node["kdpGroundTruthLastModified"] = ground.LastModifiedText;
                node["kdpGroundTruthCheckedAt"] = ground.CheckedAt;
                node["kdpGroundTruthMatchesExpected"] = ground.MatchesExpected;
            }

            // Pure function of already-known manifest fields — no live visit needed: a book
            // that's held (never signed off, or explicitly Held) but still shows as Published is
            // exactly the case the sequential pass flags rather than acting on (see
            // KdpReconcileService's remarks — automated unpublish isn't built yet). Keyed off the
            // same computed publicationStatus the status pill already shows (not PublishUrl,
            // which Mark Unpublished deliberately never clears) so clicking Mark Unpublished after
            // a manual takedown correctly clears this flag too.
            var effectiveStatus = node["publicationStatus"]?.GetValue<string>() ?? e.PublicationStatus;
            node["heldButLive"] = !e.ReadyToPublish && effectiveStatus == "Published";

            rows.Add(node);
        }
        var json = rows.ToJsonString();
        // json becomes a JS string ARGUMENT here — encode it as a JS string literal (the page's
        // onManifest does JSON.parse on it), not inline it as a JS object literal.
        var jsArg = JsonSerializer.Serialize(json);
        await TryExecuteScriptAsync(ControlPanel.CoreWebView2, $"window.ssPanel.onManifest({jsArg})");
        await PostLogAsync($"Loaded manifest — {lastManifest.Count} tracked, {lastManifest.Count(e => e.NeedsRepublish)} outdated.");
    }

    /// <summary>
    /// A hung/orphaned WebView2 browser process (e.g. left behind by a previously force-killed
    /// run — the failure mode that motivated this) can make <c>ExecuteScriptAsync</c> block
    /// forever with zero exception and zero log output, silently freezing an entire automated
    /// run behind a "Running..." button that never clears. Every UI-update call on the run path
    /// goes through this instead of a raw <c>ExecuteScriptAsync</c> so a stuck script call can
    /// never again block progress or hide what happened — it degrades to a console warning and
    /// lets the caller continue.
    /// </summary>
    private static async Task<bool> TryExecuteScriptAsync(CoreWebView2 webView, string script, int timeoutMs = 8000)
    {
        try
        {
            var scriptTask = webView.ExecuteScriptAsync(script);
            var winner = await Task.WhenAny(scriptTask, Task.Delay(timeoutMs));
            if (winner != scriptTask)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ WebView2 ExecuteScriptAsync timed out after {timeoutMs}ms — continuing without UI update (panel display only; the run itself is unaffected).");
                return false;
            }
            await scriptTask; // observe/propagate a real script exception, if any
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ WebView2 ExecuteScriptAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// On-demand cover.jpg preview for the panel's per-row image icon. Sent as a base64 data
    /// URI rather than baked into the manifest JSON — covers run ~1-1.5MB each, and the full
    /// manifest already refreshes after every book in a run; embedding every cover eagerly
    /// would bloat that payload for books nobody ever previews.
    /// </summary>
    private async Task SendCoverImageAsync(string code)
    {
        var entry = lastManifest.FirstOrDefault(e => e.Code == code);
        string? dataUri = null;
        if (entry != null)
        {
            var coverPath = Path.Combine(entry.FolderPath, "cover.jpg");
            if (File.Exists(coverPath))
            {
                var bytes = await File.ReadAllBytesAsync(coverPath);
                dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
            }
        }
        var jsCode = JsonSerializer.Serialize(code);
        var jsData = JsonSerializer.Serialize(dataUri);
        await ControlPanel.CoreWebView2.ExecuteScriptAsync($"window.ssPanel.onCoverImage({jsCode}, {jsData})");
    }

    /// <summary>
    /// The "▶ Run Sequential Pass" action: sweeps the whole roster (or, if
    /// <paramref name="onlyCodes"/> is given — the CLI auto-run convenience — just those codes),
    /// in manifest order, via <see cref="KdpReconcileService"/>. Replaced the old subset-driven
    /// RunSelectedAsync/RunSelectedCoreAsync: there is no ephemeral "selected" concept left to
    /// pass in — every signed-off book is visited and either confirmed or fixed every pass.
    /// </summary>
    private async Task RunReconcilePassAsync(IReadOnlySet<string>? onlyCodes = null)
    {
        if (kdpBrowser == null)
        {
            await PostLogAsync("⚠ KDP browser pane isn't ready yet — wait for it to finish loading and try again.");
            return;
        }

        // One run at a time: the button only disables after a script round-trip, so a second
        // click could start a second run driving the same browser pane.
        if (runCts != null)
        {
            await PostLogAsync("⚠ A run is already in progress.");
            return;
        }
        runCts = new CancellationTokenSource();
        try
        {
            await RunReconcilePassCoreAsync(onlyCodes, runCts.Token);
        }
        catch (Exception ex)
        {
            // Fired and forgotten by its callers, so an exception here was swallowed silently.
            await PostLogAsync($"⚠ The run stopped: {ex.Message}");
        }
        finally
        {
            // Always leave the panel usable: a manifest or run-log failure used to leave it on
            // "Running…" with Start disabled until the app was restarted.
            if (currentRunId is Guid finishedRun && runLog != null)
            {
                try { await runLog.FinishRunAsync(finishedRun); } catch { /* the run log is advisory */ }
            }
            currentRunId = null;
            await SetRunningAsync(false);
            runCts.Dispose();
            runCts = null;
        }
    }

    private async Task RunReconcilePassCoreAsync(IReadOnlySet<string>? onlyCodes, CancellationToken ct)
    {
        await SetRunningAsync(true);

        var toRun = onlyCodes == null ? lastManifest : lastManifest.Where(e => onlyCodes.Contains(e.Code)).ToList();

        runLog = App.Services.GetRequiredService<KdpRunLogService>();
        currentRunId = runLog.StartRun(KdpManifestService.FindRepoRoot(), toRun.Select(e => e.Code).ToHashSet());

        var reconcileService = App.Services.GetRequiredService<KdpReconcileService>();
        // RunReconcilePassAsync refuses to start before the pane exists, so this cannot be null here.
        var ctx = new KdpOperatorContext { Browser = kdpBrowser ?? throw new InvalidOperationException("KDP browser pane is not ready.") };
        await PostLogAsync($"Starting sequential pass: {toRun.Count} book(s) in the roster.");

        try
        {
            await foreach (var (code, evt) in reconcileService.RunAsync(toRun, ctx, groundTruthByCode, ct))
                await PostLogAsync(FormatEvent(code, evt));
        }
        catch (OperationCanceledException)
        {
            await PostLogAsync("Pass cancelled.");
        }

        // Re-pull the manifest once at the end (every book was already visited in sequence above;
        // a per-book refresh isn't needed since the pass itself already reflects each confirm/
        // publish/skip as it goes via the log) so the panel's checkmarks/ground-truth columns
        // reflect this run's results.
        await RefreshManifestAsync();
        await PostLogAsync(KdpRunLogFormat.FinishedMessage);
    }

    /// <summary>
    /// The "Mark Unpublished" panel action — clears PublicationStatus/KdpPublishedAt in the DB
    /// (via <see cref="KdpMarkPublishedService.UnmarkPublishedAsync"/>) for every selected code,
    /// then refreshes the manifest so the panel immediately shows those rows as needing a
    /// republish. Lets the user force a redo of a book Start would otherwise skip via the
    /// version pre-check (e.g. to re-verify the pipeline, or recover from a bad prior publish).
    /// </summary>
    private async Task MarkUnpublishedAsync(HashSet<string> codes)
    {
        var markService = App.Services.GetRequiredService<KdpMarkPublishedService>();
        var count = await markService.UnmarkPublishedAsync(codes);
        await PostLogAsync($"Marked {count} book(s) unpublished: {string.Join(", ", codes)}");
        await RefreshManifestAsync();
    }

    private async Task PostLogAsync(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        Console.WriteLine(stamped);

        // Durable mirror FIRST, before touching the UI at all — a terminal/DB query must be able
        // to follow a run even if the WebView2 panel update below hangs or fails (see
        // TryExecuteScriptAsync remarks). Previously this ran only after the UI update returned,
        // so a stuck ExecuteScriptAsync call silently swallowed the durable log entry too, not
        // just the on-screen line.
        if (currentRunId is Guid rid && runLog != null)
            _ = runLog.LogAsync(rid, line);

        var jsArg = JsonSerializer.Serialize(stamped);
        await TryExecuteScriptAsync(ControlPanel.CoreWebView2, $"window.ssPanel.onLog({jsArg})");
    }

    private async Task SetRunningAsync(bool running)
        => await TryExecuteScriptAsync(ControlPanel.CoreWebView2, $"window.ssPanel.onRunState({(running ? "true" : "false")})");

    private static string FormatEvent(string code, OperatorEvent evt) => evt switch
    {
        OperatorEvent.AssistantText t => $"{code}: {t.Text}",
        OperatorEvent.ToolStarted s => $"{code}: → {s.Name}({Truncate(s.ArgsJson, 120)})",
        OperatorEvent.ToolCompleted c => $"{code}: {(c.IsError ? "✗" : "✓")} {c.Name} → {Truncate(c.ResultJson, 160)}",
        OperatorEvent.Error e => $"{code}: ⚠ {e.Message}",
        OperatorEvent.Info i => $"{code}: {i.Message}",
        _ => $"{code}: {evt}",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
