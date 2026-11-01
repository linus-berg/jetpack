using System.Net;
using System.Text;
using Jetpack.Api.Configuration;
using Jetpack.Api.Endpoints;
using Jetpack.Api.Plugins;
using Microsoft.Extensions.Options;

namespace Jetpack.Api.Web;

/// <summary>Small server-rendered UI: catalog overview, upload and setup help.</summary>
public static class WebUi {
  private const string C_CSP_ =
    "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
    "script-src 'self' 'unsafe-inline'; frame-src 'self'; object-src 'none'";

  public static void MapWebUi(this IEndpointRouteBuilder app) {
    app.MapGet("/", Index);
    // Target of the "Plugin homepage" link inside the IDE.
    app.MapGet("/plugin/index", PluginPage);
  }

  private static IResult Index(HttpContext context, PluginCatalog catalog,
                               DownloadStatistics statistics,
                               IOptions<JetpackOptions> options) {
    string server = (options.Value.PublicUrl ??
                     $"{context.Request.Scheme}://{context.Request.Host}")
      .TrimEnd('/');
    StringBuilder rows = new();
    foreach (PluginEntry plugin in catalog.Snapshot.Plugins) {
      ReleaseEntry latest = plugin.Latest;
      rows.Append("<tr data-search=\"")
          .Append(
            E(
              $"{plugin.Name} {plugin.Id} {plugin.Vendor} " +
              string.Join(' ', plugin.Tags)
            ).ToLowerInvariant()
          )
          .Append("\"><td class=\"icon\">");
      if (plugin.Releases.Any(r => r.Release.HasIcon)) {
        rows.Append("<img alt=\"\" src=\"/api/icon?pluginId=")
            .Append(E(Uri.EscapeDataString(plugin.Id)))
            .Append("\">");
      }

      rows.Append("</td><td><a href=\"/plugin/index?xmlId=")
          .Append(E(Uri.EscapeDataString(plugin.Id)))
          .Append("\"><b>")
          .Append(E(plugin.Name))
          .Append("</b></a><div class=\"muted\">")
          .Append(E(plugin.Id))
          .Append("</div></td><td>")
          .Append(E(plugin.Vendor ?? ""))
          .Append("</td><td>")
          .Append(E(latest.Release.Version))
          .Append("</td><td>")
          .Append(plugin.Releases.Count)
          .Append("</td><td>")
          .Append(statistics.Get(plugin.Id))
          .Append("</td></tr>");
    }

    string body = $$"""
      <h1>Jetpack <span class="muted">JetBrains plugin repository</span></h1>
      <p>{{catalog.Snapshot.Plugins.Count}} plugins, {{catalog.Snapshot.ReleaseCount}} versions.</p>
      <details>
        <summary><b>Connect an IDE</b></summary>
        <ol>
          <li><b>Zero configuration (recommended):</b> resolve
            <code>plugins.jetbrains.com</code> and <code>downloads.marketplace.jetbrains.com</code>
            to this server in your DNS and install the CA that signed this server's certificate
            in the operating system trust store. The IDE's built-in Marketplace then just works.</li>
          <li><b>Per IDE:</b> add <code>-Didea.plugins.host={{E(server)}}</code> via
            <i>Help | Edit Custom VM Options</i>.</li>
          <li><b>Custom repository:</b> add <code>{{E(server)}}/updatePlugins.xml</code> under
            <i>Settings | Plugins | ⚙ | Manage Plugin Repositories</i>.</li>
        </ol>
      </details>
      <details>
        <summary><b>Upload plugins</b></summary>
        <form id="upload">
          <input type="file" id="files" multiple accept=".zip,.jar" required>
          <input type="password" id="key" placeholder="API key" autocomplete="current-password" required>
          <label><input type="checkbox" id="force"> replace existing versions</label>
          <button type="submit">Upload</button>
        </form>
        <pre id="result"></pre>
        <p class="muted">Or: <code>curl -H "X-Api-Key: $KEY" --data-binary @plugin.zip
          "{{E(server)}}/api/plugins/upload?filename=plugin.zip"</code></p>
      </details>
      <input id="filter" type="search" placeholder="Filter plugins..." autofocus>
      <table>
        <thead><tr><th></th><th>Plugin</th><th>Vendor</th><th>Latest</th><th>Versions</th><th>Downloads</th></tr></thead>
        <tbody id="plugins">{{rows}}</tbody>
      </table>
      <script>
        const filter = document.getElementById('filter');
        filter.addEventListener('input', () => {
          const q = filter.value.toLowerCase();
          for (const row of document.querySelectorAll('#plugins tr')) {
            row.hidden = q && !row.dataset.search.includes(q);
          }
        });
        document.getElementById('upload').addEventListener('submit', async e => {
          e.preventDefault();
          const out = document.getElementById('result');
          const data = new FormData();
          for (const f of document.getElementById('files').files) data.append('files', f, f.name);
          const force = document.getElementById('force').checked ? '?force=true' : '';
          out.textContent = 'Uploading...';
          const res = await fetch('/api/plugins/upload' + force, {
            method: 'POST', body: data,
            headers: {'X-Api-Key': document.getElementById('key').value}
          });
          const json = await res.json().catch(() => ({}));
          const results = json.results || [];
          out.textContent = results.length
            ? results.map(r => `${r.status}: ${r.uploadedFileName}${r.plugin ? ` -> ${r.plugin.id} ${r.plugin.version}` : ''}${r.error ? ` (${r.error})` : ''}`).join('\n')
            : `${res.status} ${json.title || ''} ${json.detail || ''}`;
          if (res.ok) setTimeout(() => location.reload(), 1500);
        });
      </script>
      """;
    return Page("Jetpack", body);
  }

  private static IResult PluginPage(HttpRequest request, PluginCatalog catalog,
                                    DownloadStatistics statistics) {
    string? id = request.QueryString("xmlId") ?? request.QueryString("id");
    PluginEntry? plugin = id == null ? null : catalog.Snapshot.FindPlugin(id);
    if (plugin == null) {
      return Page(
        "Not found",
        $"<p>Plugin <code>{E(id ?? "")}</code> is not available on this server.</p>" +
        "<p><a href=\"/\">Back to all plugins</a></p>",
        StatusCodes.Status404NotFound
      );
    }

    PluginRelease latest = plugin.Latest.Release;
    StringBuilder versions = new();
    foreach (ReleaseEntry entry in plugin.Releases) {
      PluginRelease release = entry.Release;
      versions.Append("<tr><td>")
              .Append(E(release.Version))
              .Append("</td><td>")
              .Append(E(release.SinceBuild ?? "any"))
              .Append(" &ndash; ")
              .Append(E(release.UntilBuild ?? "any"))
              .Append(
                entry.Platform.Label is { } platform
                  ? $" <span class=\"muted\">({E(platform)})</span>"
                  : ""
              )
              .Append("</td><td>")
              .Append(release.UploadedAt.ToString("yyyy-MM-dd HH:mm"))
              .Append("</td><td><a href=\"")
              .Append(E(HttpHelpers.FileUrl(entry)))
              .Append("\">")
              .Append(E(release.FileName))
              .Append("</a> <span class=\"muted\">")
              .Append(release.Size / 1024)
              .Append(" KiB</span></td><td><button class=\"delete\" data-version=\"")
              .Append(E(release.Version))
              .Append("\">Delete</button></td></tr>");
    }

    string icon = plugin.Releases.Any(r => r.Release.HasIcon)
                    ? $"<img class=\"logo\" alt=\"\" src=\"/api/icon?pluginId={E(Uri.EscapeDataString(plugin.Id))}\">"
                    : "";
    string dependencies = latest.Dependencies.Count == 0
                            ? "none"
                            : string.Join(", ", latest.Dependencies.Select(d => $"<code>{E(d)}</code>"));
    // Plugin descriptions are HTML written by plugin authors: render them in a
    // sandboxed frame without scripts.
    string description = WebUtility.HtmlEncode(
      $"<base target=\"_blank\"><style>body{{font-family:sans-serif;font-size:14px}}</style>{latest.Description}"
    );
    string body = $$"""
      <p><a href="/">&larr; All plugins</a></p>
      <h1>{{icon}} {{E(plugin.Name)}}</h1>
      <p><code>{{E(plugin.Id)}}</code> &middot; {{E(plugin.Vendor ?? "unknown vendor")}}
        &middot; {{statistics.Get(plugin.Id)}} downloads</p>
      <p>Requires: {{dependencies}}</p>
      <iframe sandbox="allow-popups" srcdoc="{{description}}"></iframe>
      <h2>Versions</h2>
      <table>
        <thead><tr><th>Version</th><th>Compatible builds</th><th>Uploaded</th><th>File</th><th></th></tr></thead>
        <tbody>{{versions}}</tbody>
      </table>
      <script>
        for (const button of document.querySelectorAll('button.delete')) {
          button.addEventListener('click', async () => {
            const version = button.dataset.version;
            if (!confirm(`Delete version ${version}?`)) return;
            const key = prompt('API key');
            if (!key) return;
            const id = {{System.Text.Json.JsonSerializer.Serialize(plugin.Id)}};
            const res = await fetch(`/api/plugins/${encodeURIComponent(id)}/${encodeURIComponent(version)}`,
              {method: 'DELETE', headers: {'X-Api-Key': key} });
            if (res.ok) location.reload(); else alert(`Delete failed: ${res.status}`);
          });
        }
      </script>
      """;
    return Page(plugin.Name, body);
  }

  private static IResult Page(string title, string body,
                              int status = StatusCodes.Status200OK) {
    string html = $$"""
      <!DOCTYPE html>
      <html lang="en"><head><meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>{{E(title)}}</title>
      <style>
        body { font-family: system-ui, sans-serif; max-width: 1100px; margin: 2em auto; padding: 0 1em; color: #222; }
        .muted { color: #777; font-weight: normal; font-size: .9em; }
        table { border-collapse: collapse; width: 100%; margin-top: 1em; }
        th, td { text-align: left; padding: .45em .6em; border-bottom: 1px solid #e3e3e3; vertical-align: middle; }
        td.icon img { width: 32px; height: 32px; }
        img.logo { width: 48px; height: 48px; vertical-align: middle; }
        input[type=search] { width: 100%; padding: .5em; margin-top: 1em; font-size: 1em; }
        details { margin: .8em 0; padding: .6em 1em; background: #f6f7f9; border-radius: 6px; }
        iframe { width: 100%; height: 340px; border: 1px solid #e3e3e3; border-radius: 6px; }
        code { background: #eef0f3; padding: 0 .3em; border-radius: 3px; }
        a { color: #2161d6; text-decoration: none; }
      </style></head><body>
      {{body}}
      </body></html>
      """;
    return new HtmlResult(html, status);
  }

  private static string E(string value) {
    return WebUtility.HtmlEncode(value);
  }

  private sealed class HtmlResult(string html, int status) : IResult {
    public Task ExecuteAsync(HttpContext context) {
      context.Response.StatusCode = status;
      context.Response.ContentType = "text/html; charset=utf-8";
      context.Response.Headers.ContentSecurityPolicy = C_CSP_;
      context.Response.Headers.XContentTypeOptions = "nosniff";
      return context.Response.WriteAsync(html);
    }
  }
}
