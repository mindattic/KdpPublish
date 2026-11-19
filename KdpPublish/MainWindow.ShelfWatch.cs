using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Prose.Core.Services;
using Prose.Core.Services.Operator;

namespace KdpPublish;

/// <summary>
/// Keeps the panel honest about what actually happened on KDP, and tells the person (KP-T29):
/// <list type="bullet">
/// <item>Once a minute, while no run or shelf scan is going, the bookshelf is re-read in the
///   background (a fetch through the DevTools protocol; the KDP pane is never navigated) and the
///   table is refreshed whenever anything on it changed.</item>
/// <item>Every change in a book's KDP status (Draft -> In review -> Live, an ASIN assigned) and
///   every book's outcome in a run (published, or not and why) is written to the panel log and
///   sent as a push notification through MindAttic.Psst (<c>psst test</c>: a sound at the desk
///   and a text to the owner's phone). Set KDPPUBLISH_NO_PSST=1 to keep it to the log.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer shelfTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private string? lastShelfSignature;

    /// <summary>Each tracked book's last-seen KDP status and ASIN, by Code; null until the first
    /// bookshelf read after the manifest loaded (that read only seeds it, so startup is silent).</summary>
    private Dictionary<string, (string? Label, string? Asin)>? lastShelfStateByCode;

    /// <summary>Bookshelf rows by normalized title: the only way to place a book whose row has no
    /// edit links yet (In review, Publishing), which carry no titleId.</summary>
    private ILookup<string, ShelfRow> shelfByTitle = Array.Empty<ShelfRow>().ToLookup(r => r.Title);

    /// <summary>
    /// Parses KDP's bookshelf from a document — the live page or one fetched in the background
    /// (so textContent, never innerText, which needs layout). One entry per ebook: the title
    /// cell of each title set, widened to the card holding exactly one "Kindle eBook" row.
    /// </summary>
    private const string ShelfParseFunction = """
        function parseShelf(doc) {
            function txt(el) { return (el.textContent || '').replace(/\s+/g, ' '); }
            function count(el) { return (txt(el).match(/Kindle eBook/g) || []).length; }
            var out = [], seen = [];
            var titles = doc.querySelectorAll('[id^="zme-indie-bookshelf-dual-itemset-itemset-metadata-title-"]');
            for (var i = 0; i < titles.length; i++) {
                var card = titles[i];
                while (card.parentElement && count(card) < 1) card = card.parentElement;
                while (card.parentElement && count(card.parentElement) === 1) card = card.parentElement;
                if (count(card) !== 1 || seen.indexOf(card) !== -1) continue;
                seen.push(card);
                var text = txt(card);
                var m = text.match(/Kindle eBook (Live Updates publishing|Live Updates in review|Live|Draft|In [Rr]eview|Publishing|Blocked|Unpublished)/);
                var a = text.match(/ASIN:\s*([A-Z0-9]{10})/);
                var p = {};
                var links = card.querySelectorAll('[data-link-parameters]');
                for (var j = 0; j < links.length; j++) {
                    try {
                        var q = JSON.parse(links[j].getAttribute('data-link-parameters'));
                        if (q && q.titleId && /digital/.test((q.action || '') + (q.id || ''))) { p = q; break; }
                    } catch (e) { }
                }
                out.push({ titleId: p.titleId || '', asin: p.asin || (a ? a[1] : null), title: txt(titles[i]).trim(),
                           label: m ? m[1] : null, stage: p.stage || null, liveState: p.liveState || null });
            }
            return out;
        }
        """;

    private const string LiveShelfScript = "(function () {\n" + ShelfParseFunction + "\nreturn JSON.stringify(parseShelf(document));\n})()";

    /// <summary>
    /// The bookshelf table's own refresh call, asking for 50 rows: a plain GET of /bookshelf
    /// renders KDP's default 10 per page, so the background read would lose every book past the
    /// tenth. Captured live 2026-11-19 from the page-size dropdown (POST /bookshelf/refresh with
    /// this JSON). The rows come back inside &lt;script type="text/html"&gt; templates, which a
    /// DOMParser leaves as text, so those wrappers are stripped before parsing.
    /// </summary>
    private const string FetchShelfScript =
        "fetch('/bookshelf/refresh/ref=xx_xx_cont_xx', { method: 'POST', credentials: 'include'," +
        " headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }," +
        " body: JSON.stringify({ action: 'RECORDS_PER_PAGE_CHANGED', pageNumber: 1, recordsPerPage: 50, sortOrder: 'DESCENDING'," +
        " tableId: 'refreshedbookshelftable', filters: [], clientState: { viewVal: 'UNARCHIVED' } }) })" +
        ".then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.text(); })" +
        ".then(function (t) {\n" + ShelfParseFunction +
        "\nt = t.replace(/<script type=\"text\\/html\">/g, '').replace(/<\\/script>/g, '');" +
        "\nreturn JSON.stringify(parseShelf(new DOMParser().parseFromString(t, 'text/html'))); })";

    /// <summary>Started once the KDP pane exists, for the normal interactive/auto-run window only.</summary>
    private void StartShelfWatch()
    {
        if (scanBookshelf || crawlCategories != null || probeCategoriesNodeCode != null || diagnoseRequest != null) return;
        shelfTimer.Tick += async (_, _) => await BackgroundShelfRefreshAsync();
        shelfTimer.Start();
    }

    private async Task BackgroundShelfRefreshAsync()
    {
        if (kdpBrowser == null || runCts != null || shelfScanBusy) return;
        if (!(KdpBrowser.CoreWebView2?.Source ?? "").StartsWith("https://kdp.amazon.com/", StringComparison.OrdinalIgnoreCase)) return;
        shelfScanBusy = true;
        try
        {
            var args = JsonSerializer.Serialize(new { expression = FetchShelfScript, awaitPromise = true, returnByValue = true });
            var resultJson = await KdpBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty("exceptionDetails", out _)) return;   // signed out, offline: try next minute
            var value = doc.RootElement.GetProperty("result").GetProperty("value").GetString() ?? "[]";
            var rows = JsonSerializer.Deserialize<List<ShelfRow>>(value, JsonOpts) ?? [];
            if (rows.Count == 0 || runCts != null) return;

            var changed = ApplyShelfRows(rows);
            await SetShelfCheckedAsync();
            if (changed) await RefreshManifestAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ background bookshelf read failed: {ex.Message}");
        }
        finally { shelfScanBusy = false; }
    }

    /// <summary>Reads the live bookshelf page (the pane is on it). Returns the parsed rows.</summary>
    private async Task<List<ShelfRow>> ReadLiveShelfAsync()
    {
        var json = await kdpBrowser!.EvalAsync(LiveShelfScript, CancellationToken.None);
        // EvalAsync hands back the script's string; tolerate it arriving JSON-quoted as well.
        if (json.StartsWith('"')) json = JsonDocument.Parse(json).RootElement.GetString() ?? "[]";
        return JsonSerializer.Deserialize<List<ShelfRow>>(json, JsonOpts) ?? [];
    }

    /// <summary>Takes a fresh bookshelf read as the table's ground truth. True when it differs
    /// from the previous read (so the table needs re-rendering).</summary>
    private bool ApplyShelfRows(List<ShelfRow> rows)
    {
        var signature = JsonSerializer.Serialize(rows.OrderBy(r => r.Title, StringComparer.Ordinal).ThenBy(r => r.TitleId, StringComparer.Ordinal));
        var changed = signature != lastShelfSignature;
        lastShelfSignature = signature;

        shelfByTitleId = rows.Where(r => !string.IsNullOrEmpty(r.TitleId))
            .GroupBy(r => r.TitleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        shelfByAsin = rows.Where(r => !string.IsNullOrEmpty(r.Asin)).GroupBy(r => r.Asin!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        shelfByTitle = rows.ToLookup(r => TitleKey(r.Title));
        return changed;
    }

    /// <summary>"Neon & Rust: A GLMZ Novella | Rook #2" and "Neon & Rust" are the same book.</summary>
    private static string TitleKey(string title) =>
        title.Split(':', '|')[0].Trim().ToLowerInvariant();

    /// <summary>A same-titled row, preferring the most-published one (KDP keeps old drafts of a
    /// title alongside its live listing).</summary>
    private ShelfRow? ShelfRowByTitle(string title) =>
        shelfByTitle[TitleKey(title)].OrderBy(r => r.Label switch
        {
            "Live" or "Live Updates publishing" or "Live Updates in review" => 0,
            "In review" or "In Review" or "Publishing" => 1,
            _ => 2,
        }).FirstOrDefault();

    private Task SetShelfCheckedAsync() => TryExecuteScriptAsync(ControlPanel.CoreWebView2,
        $"window.ssPanel.onShelfChecked && window.ssPanel.onShelfChecked({JsonSerializer.Serialize($"KDP checked {DateTime.Now:HH:mm}")})");

    /// <summary>
    /// After every table refresh: compares each book's KDP status and ASIN with the last read and
    /// announces what changed. The first read after start-up only seeds the comparison.
    /// </summary>
    private async Task AnnounceShelfChangesAsync()
    {
        if (lastManifest.Count == 0 || shelfByTitleId.Count + shelfByAsin.Count == 0) return;
        var now = new Dictionary<string, (string? Label, string? Asin)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in lastManifest)
        {
            var row = ShelfRowFor(e);
            now[e.Code] = (row?.Label, row?.Asin ?? e.Asin);
        }

        var previous = lastShelfStateByCode;
        lastShelfStateByCode = now;
        if (previous == null) return;

        var lines = new List<string>();
        foreach (var e in lastManifest)
        {
            var (label, asin) = now[e.Code];
            if (!previous.TryGetValue(e.Code, out var was)) continue;
            var parts = new List<string>();
            if (label != null && !string.Equals(label, was.Label, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{was.Label ?? "not on the bookshelf"} -> {label}");
            if (!string.IsNullOrEmpty(asin) && string.IsNullOrEmpty(was.Asin))
                parts.Add($"ASIN {asin} amazon.com/dp/{asin}");
            if (parts.Count > 0) lines.Add($"{e.Title}: {string.Join(", ", parts)}");
        }
        if (lines.Count > 0) await NotifyAsync("KDP: " + string.Join("; ", lines));
    }

    /// <summary>What a run did to one book, gathered from its operator events.</summary>
    private sealed class BookOutcome
    {
        public bool Attempted;
        public bool Published;
        public string? Problem;
        public string? LastText;
    }

    private static void Track(Dictionary<string, BookOutcome> outcomes, string code, OperatorEvent evt)
    {
        if (!outcomes.TryGetValue(code, out var o)) outcomes[code] = o = new BookOutcome();
        switch (evt)
        {
            case OperatorEvent.ToolStarted:
                o.Attempted = true;
                break;
            case OperatorEvent.ToolCompleted c when c.Name == "mark_published" && !c.IsError
                                                   && c.ResultJson.Contains("\"Ok\":true", StringComparison.OrdinalIgnoreCase):
                o.Published = true;
                break;
            case OperatorEvent.Error err:
                o.Problem = err.Message;
                break;
            case OperatorEvent.AssistantText t:
                o.LastText = t.Text;
                break;
        }
    }

    /// <summary>One notification for the whole run: which books went to KDP and which did not, and why.</summary>
    private async Task AnnounceRunOutcomesAsync(Dictionary<string, BookOutcome> outcomes, IReadOnlyList<KdpManifestEntry> roster, bool cancelled)
    {
        var lines = new List<string>();
        foreach (var (code, o) in outcomes)
        {
            if (!o.Attempted && o.Problem == null) continue;   // confirmed or skipped: nothing happened to it
            var title = roster.FirstOrDefault(e => e.Code == code)?.Title ?? code;
            lines.Add(o.Published
                ? $"{title} submitted to KDP"
                : $"{title} NOT published: {Shorten(o.Problem ?? o.LastText ?? "stopped without publishing", 140)}");
        }
        if (cancelled) lines.Add("(pass cancelled)");
        if (lines.Count > 0) await NotifyAsync("KDP run: " + string.Join("; ", lines));
    }

    private static string Shorten(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "...";
    }

    /// <summary>The panel log always; plus a Psst push (sound + text) unless KDPPUBLISH_NO_PSST=1.</summary>
    private async Task NotifyAsync(string message)
    {
        await PostLogAsync("🔔 " + message);
        if (Environment.GetEnvironmentVariable("KDPPUBLISH_NO_PSST") == "1") return;
        try
        {
            var psi = new ProcessStartInfo("psst") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("test");
            psi.ArgumentList.Add(Shorten(message, 600));
            using var p = Process.Start(psi);
            if (p == null) { await PostLogAsync("⚠ Push notification not sent: psst did not start."); return; }
            var stderr = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } await PostLogAsync("⚠ Push notification timed out (psst)."); return; }
            if (p.ExitCode != 0) await PostLogAsync($"⚠ Push notification failed (psst exit {p.ExitCode}): {Shorten(await stderr, 200)}");
        }
        catch (Exception ex)
        {
            await PostLogAsync($"⚠ Push notification not sent: {ex.Message} (is MindAttic.Psst on PATH?)");
        }
    }
}
