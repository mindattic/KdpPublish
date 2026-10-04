using AutoWebNav;
using AutoWebNav.WebView2;
using Microsoft.Web.WebView2.Core;
using Prose.Core.Services.Operator;

namespace Prose.KdpPublish;

/// <summary>
/// Real implementation of <see cref="IKdpBrowser"/> against a live WebView2 pane — a thin adapter
/// over AutoWebNav's shared <see cref="WebView2BrowserSurface"/>, which now owns the mechanics this
/// class used to carry itself: the hard 30 s per-call timeout (the backstop against a renderer
/// wedged by a native dialog — the cause of the 100-minute 0%-CPU sweep freeze), trusted CDP
/// mouse clicks (KDP's React <c>role=checkbox</c> confirmation ignores <c>el.click()</c>), trusted
/// CDP keystrokes (the Pricing page's derived marketplace prices), and dialog-free file injection.
/// <para>
/// The toolkit is NOT installed: KDP's tools evaluate their own scripts and never use the
/// fingerprint resolver, so the pages stay exactly as Amazon ships them — the same as before the
/// move to AutoWebNav.
/// </para>
/// </summary>
public class WebView2KdpBrowser : IKdpBrowser
{
    private readonly IBrowserSurface surface;

    public WebView2KdpBrowser(CoreWebView2 core)
    {
        surface = new WebView2BrowserSurface(core, setZoom: null, installToolkit: false);
    }

    public string CurrentUrl => surface.CurrentUrl;

    public Task<string> EvalAsync(string script, CancellationToken ct) => surface.EvalAsync(script, ct);

    /// <summary>KDP's tools address file inputs by CSS selector; the shared surface takes an
    /// element expression, so the selector is wrapped (JSON-quoted) in a querySelector call.</summary>
    public Task InjectFileAsync(string filePath, string selector, CancellationToken ct)
        => surface.InjectFileAsync(filePath, $"document.querySelector({System.Text.Json.JsonSerializer.Serialize(selector)})", ct);

    public Task ClickAtPointAsync(double x, double y, CancellationToken ct) => surface.ClickAtPointAsync(x, y, ct);

    public Task TypeTextAsync(string text, CancellationToken ct) => surface.TypeTextAsync(text, ct);
}
