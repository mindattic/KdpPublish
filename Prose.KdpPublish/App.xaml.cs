using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prose.Core.Extensions;

namespace Prose.KdpPublish;

/// <summary>
/// Same DI shape as the CLI's <c>BuildCoreServices</c> (Host.CreateDefaultBuilder +
/// AddProseServices) — one registration point shared with every other
/// Prose.* front end.
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Completes once the KDP store is migrated and its first-run import has run.</summary>
    public static Task StoreReady { get; private set; } = Task.CompletedTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        // Log any unhandled exception anywhere in the app (WPF dispatcher + AppDomain-wide).
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kdppublish-error.log");
        DispatcherUnhandledException += (_, ex) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] DISPATCHER: {ex.Exception}\n\n");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] APPDOMAIN: {ex.ExceptionObject}\n\n");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}] TASK: {ex.Exception}\n\n");
            ex.SetObserved();
        };
#endif

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) => services.AddProseServices())
            .Build();

        Services = host.Services;

        // The KDP store (machine-local SQLite): apply migrations and, on the very first run, the
        // one-time import of the legacy JSON files. Started here, awaited by MainWindow before it
        // builds the first manifest, so the window can open while it runs.
        StoreReady = Services.GetRequiredService<Prose.Core.Kdp.KdpStore>().EnsureReadyAsync();

        // Optional: `--crawl-categories <NodeCode> <level0> [level1] [...]` — a one-off, read-only
        // documentation pass (see CategoryTreeCrawler) that walks every Subcategory branch and
        // Placement leaf under the given starting path on KDP's live Categories modal, using the
        // given (already-published) book's Details page purely as a place to open that modal.
        // Never saves anything on KDP. Saves the result to the KDP store and exports it to
        // tools/kdp/category-tree-<slug>.json.
        if (e.Args.Length > 1 && e.Args[0] == "--crawl-categories")
        {
            var nodeCode = e.Args[1];
            var rest = e.Args.Skip(2).ToArray();
            int? maxDepth = null;
            if (rest.Length > 0 && int.TryParse(rest[0], out var d)) { maxDepth = d; rest = rest.Skip(1).ToArray(); }
            new MainWindow(crawlCategories: (nodeCode, maxDepth, rest)).Show();
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--probe-categories")
        {
            new MainWindow(probeCategoriesNodeCode: e.Args[1]).Show();
            return;
        }

        // Optional: `--diagnose <CODE> [details|content|pricing]` — a one-off, read-only DOM
        // snapshot of a book's KDP page (buttons, checkboxes, dialogs, banners, iframes, body
        // text), dumped to tools/kdp/diagnose-<CODE>-<step>-<timestamp>.json and stdout. For
        // diagnosing a tool that silently stops advancing (e.g. Save and Continue stuck on the
        // Content step) against the real live DOM instead of guessing from the operator log.
        if (e.Args.Length > 1 && e.Args[0] == "--diagnose")
        {
            var step = e.Args.Length > 2 ? e.Args[2] : "content";
            new MainWindow(diagnoseRequest: (e.Args[1], step)).Show();
            return;
        }

        // Optional: `--scan-bookshelf` — reads the REAL per-book status KDP's own bookshelf
        // page displays (Live / In Review / Draft / etc.) for every row, straight off the live
        // DOM. Our local PublicationStatus is bookkeeping (set only when mark_published fires);
        // it can drift from reality for a book published by some other path (manually, or before
        // this tool tracked it) — confirmed live 2026-10-04: VATD showed "Unpublished" locally
        // while the real KDP dashboard showed it Live. Ground truth beats our own interpretation.
        if (e.Args.Length > 0 && e.Args[0] == "--scan-bookshelf")
        {
            new MainWindow(scanBookshelf: true).Show();
            return;
        }

        // Optional: a comma-separated NodeCode list as argv[0] auto-starts the exact same
        // RunSelectedAsync flow the panel's Start button triggers, once the manifest loads — a
        // way to exercise the real automation end-to-end without a human click, e.g.
        // `Prose.KdpPublish.exe MXG,NXR`. Not a special/shortcut code path: it drives the
        // same KdpOperatorService/KdpToolRegistry tools against the same live WebView2 pane.
        var autoRunCodes = e.Args.Length > 0 ? e.Args[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        new MainWindow(autoRunCodes).Show();
    }
}
