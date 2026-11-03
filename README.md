<p align="center">
  <img src="assets/logo.svg" alt="Jetpack logo" width="160">
</p>

<h1 align="center">Jetpack</h1>

<p align="center"><b>An offline JetBrains Marketplace for air-gapped networks.</b></p>

Jetpack implements the parts of the `plugins.jetbrains.com` API that
IntelliJ-based IDEs (IntelliJ IDEA, WebStorm, PyCharm, Rider, GoLand, CLion,
DataGrip, Android Studio, ...) use. Point `plugins.jetbrains.com` at Jetpack in
your internal DNS, and the IDE's built-in **Marketplace** tab, plugin updates
and dependency installation work with no IDE configuration.

```
 developer IDE ──https://plugins.jetbrains.com──▶ (internal DNS) ──▶ Jetpack ──▶ disk or S3/MinIO
```

* Marketplace tab: listing, search, sorting, tag/vendor filters, icons.
* "Plugin updates available", including update checks of plugins installed earlier.
* Automatic installation of required plugin dependencies.
* Compatibility filtering with the IDE's own `since-build`/`until-build` rules
  and version ordering, so every IDE version gets the right plugin version.
* Platform-specific plugins (separate builds per OS/CPU, such as GitHub
  Copilot): each IDE gets the build for its own OS and CPU.
* Mirrored Marketplace plugins are stored byte-for-byte, so JetBrains plugin
  signatures still verify.
* Supported IDE versions: 2022.3 through 2026.x (both update-check protocols).
* Storage: a plain directory (default) or S3-compatible object storage
  (MinIO, SeaweedFS, Ceph, AWS).
* A web UI (light and dark themes) and a REST API for browsing, uploading and
  deleting plugins. The UI is entirely self-contained, so it works with no
  internet access.

## Contents

1. [Quick start](#quick-start)
2. [Getting plugins into the air gap](#getting-plugins-into-the-air-gap)
3. [Connecting IDEs](#connecting-ides)
4. [Configuration](#configuration)
5. [HTTP API](#http-api)
6. [Operations](#operations)
7. [Upgrading from the previous Jetpack](#upgrading-from-the-previous-jetpack)
8. [Troubleshooting](#troubleshooting)
9. [Development](#development)

## Quick start

```bash
cd deploy
./generate-certs.sh jetpack.corp.example          # or issue a cert from your own CA (see below)
echo "JETPACK_API_KEY=$(openssl rand -hex 24)" > .env
docker compose up -d
curl -k https://localhost/health
```

Then:

1. In your internal DNS, point **`plugins.jetbrains.com`** and
   **`downloads.marketplace.jetbrains.com`** at the Jetpack host
   ([examples](#1-dns-zero-configuration-recommended)).
2. Install `deploy/certs/ca.crt` in the operating-system trust store of the
   developer machines ([how](#trusting-the-certificate)).
3. Upload plugins ([next section](#getting-plugins-into-the-air-gap)).
4. Open *Settings | Plugins | Marketplace* in any JetBrains IDE.

The web UI at `https://<jetpack>/` has:

* **Plugins:** the catalog, with search and sorting. Each plugin page shows its
  description, change notes, every version (compatible builds, platform, size,
  SHA-256), dependencies, download links, and buttons to delete versions.
* **Upload:** drag-and-drop upload of several files at once, with a progress
  bar and result for each file.
* **Connect an IDE:** the setup steps for each connection method, with
  ready-to-copy snippets for this server.

Every script, stylesheet, font and icon comes from Jetpack itself. Bootstrap
5.3 is bundled, and pages send a Content-Security-Policy that allows nothing
else. Plugin descriptions (HTML written by plugin authors) are sanitised before
they're displayed.

## Getting plugins into the air gap

`scripts/jetpack-mirror.py` uses only the Python 3 standard library.

**On a machine with internet access**, download the newest version of each
plugin that is compatible with each IDE build you use. The build is shown in
*Help | About*, e.g. `Build #IU-252.25557.131`.

```bash
scripts/jetpack-mirror.py download \
    -b IU-252.25557.131 -b WS-252.25557.126 -b IU-243.26053.27 \
    --deps --broken-plugins -o bundle/ \
    org.rust.lang "String Manipulation" mobi.hsz.idea.gitignore

# or keep the list in a file (one plugin id per line, # comments allowed)
scripts/jetpack-mirror.py download -b IU-252.25557.131 -f plugins.txt -o bundle/
```

* The plugin id is the `xmlId` from the plugin's `plugin.xml`. Look it up with
  `curl "https://plugins.jetbrains.com/api/search/plugins?search=rust&max=5"`.
  Some older plugins use their name as the id (`"String Manipulation"`).
* `--deps` also fetches required plugin dependencies that are on the
  Marketplace. Dependencies bundled with the IDE are skipped.
* `--broken-plugins` fetches JetBrains' list of known-broken plugin versions.
* Some plugins publish a separate build per OS/CPU. For those, a build is
  downloaded for every platform by default: `linux-x64`, `linux-arm64`,
  `windows-x64`, `windows-arm64`, `macos-x64`, `macos-arm64`. Restrict it to
  the platforms you use with `-p linux-x64 -p windows-x64`, since these builds
  are large (GitHub Copilot is about 190 MB each). Ordinary plugins are
  unaffected and are downloaded once.
* Running the command again only downloads what is new, so it also works as
  a periodic update job.

**Inside the air gap**, upload the bundle:

```bash
scripts/jetpack-mirror.py upload bundle/ \
    --server https://plugins.jetbrains.com --ca-cert deploy/certs/ca.crt \
    --api-key "$JETPACK_API_KEY"
```

Versions that already exist are skipped (`--force` replaces them). Marketplace
tags recorded in `bundle/manifest.json` are carried over.

**In-house plugins** are uploaded the same way, through the web UI, or with curl:

```bash
curl -H "X-Api-Key: $JETPACK_API_KEY" --data-binary @build/distributions/my-plugin-1.2.0.zip \
     "https://jetpack.corp.example/api/plugins/upload?filename=my-plugin-1.2.0.zip"
```

Upload every OS/CPU build of a platform-specific plugin; each has its own
version string, such as `1.20.0-261-linux-x64`. Jetpack recognises the
platform from the required `com.intellij.modules.os.*` /
`com.intellij.modules.arch.*` dependencies in `plugin.xml`, as the IDE does.
IDEs 2025.3 and newer report their OS and CPU, and Jetpack serves them the
matching build. Older IDEs and other clients that don't report a platform get
a platform-independent version when one is compatible.

Accepted formats are exactly what the IDE accepts: a **`.jar`** with
`META-INF/plugin.xml`, or a **`.zip`** with one top-level directory containing
`lib/*.jar`. Jetpack detects which one it was given, whatever the file is
called. Invalid archives are rejected with an explanation.

## Connecting IDEs

All three methods can be used at the same time.

### 1. DNS (zero configuration, recommended)

The IDE talks to `https://plugins.jetbrains.com`. Resolve that name, and
`downloads.marketplace.jetbrains.com` for good measure, to the Jetpack host.

<details><summary>dnsmasq / Pi-hole</summary>

```
address=/plugins.jetbrains.com/10.1.2.3
address=/downloads.marketplace.jetbrains.com/10.1.2.3
```
</details>

<details><summary>Unbound</summary>

```
local-zone: "plugins.jetbrains.com." redirect
local-data: "plugins.jetbrains.com. A 10.1.2.3"
local-zone: "downloads.marketplace.jetbrains.com." redirect
local-data: "downloads.marketplace.jetbrains.com. A 10.1.2.3"
```
</details>

<details><summary>BIND</summary>

Create a zone per name (`zone "plugins.jetbrains.com" { type master; file "jetpack.zone"; };`,
same for `downloads.marketplace.jetbrains.com`) with:

```
$TTL 300
@   IN SOA ns.corp.example. hostmaster.corp.example. (1 3600 600 86400 300)
    IN NS  ns.corp.example.
@   IN A   10.1.2.3
```
</details>

<details><summary>Windows Server DNS</summary>

```powershell
Add-DnsServerPrimaryZone -Name "plugins.jetbrains.com" -ReplicationScope Forest
Add-DnsServerResourceRecordA -ZoneName "plugins.jetbrains.com" -Name "@" -IPv4Address 10.1.2.3
Add-DnsServerPrimaryZone -Name "downloads.marketplace.jetbrains.com" -ReplicationScope Forest
Add-DnsServerResourceRecordA -ZoneName "downloads.marketplace.jetbrains.com" -Name "@" -IPv4Address 10.1.2.3
```
</details>

<details><summary>Single machine (hosts file)</summary>

```
10.1.2.3  plugins.jetbrains.com downloads.marketplace.jetbrains.com
```
</details>

#### Trusting the certificate

The IDE validates TLS. Jetpack needs a certificate for
`plugins.jetbrains.com` (and `downloads.marketplace.jetbrains.com`) issued by a
CA your machines trust. Either issue one from your organisation's CA, or run
`deploy/generate-certs.sh`, which creates a CA and the certificate.

JetBrains IDEs trust the **operating system's certificate store** by default,
so install the CA certificate there:

| OS | Command |
|----|---------|
| Fedora/RHEL | `sudo cp ca.crt /etc/pki/ca-trust/source/anchors/jetpack.crt && sudo update-ca-trust` |
| Debian/Ubuntu | `sudo cp ca.crt /usr/local/share/ca-certificates/jetpack.crt && sudo update-ca-certificates` |
| macOS | `sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain ca.crt` |
| Windows | `certutil -addstore -f Root ca.crt` (or distribute via Group Policy) |

Alternatively, accept or import the certificate per user under
*Settings | Tools | Server Certificates*.

#### Plugin signatures

In DNS mode the IDE believes it is talking to the real Marketplace, so it
verifies that downloaded plugins carry JetBrains Marketplace signatures:

* **Plugins mirrored from the Marketplace** are signed and install silently.
  Jetpack never modifies them.
* **In-house (unsigned) plugins** trigger a one-time *"Plugin is not signed,
  install anyway?"* prompt. To avoid it, sign them
  ([`signPlugin`](https://plugins.jetbrains.com/docs/intellij/plugin-signing.html))
  and add your certificate under *Settings | Plugins | ⚙ | Manage Plugin
  Certificates*, or disable the check with the VM option
  `-Dmarketplace.certificate.signature.check=false`.

Method 2 below does not perform this check.

### 2. Per-IDE VM option

If you can't change DNS, add this via *Help | Edit Custom VM Options* (or to
a `.vmoptions` file you distribute):

```
-Didea.plugins.host=https://jetpack.corp.example
```

The Marketplace tab, updates and dependencies then work against Jetpack. The
certificate must match `jetpack.corp.example` and be trusted, as above.

### 3. Custom plugin repository (classic)

*Settings | Plugins | ⚙ | Manage Plugin Repositories* →
`https://jetpack.corp.example/updatePlugins.xml`. The old URL,
`/api/plugins/updatePlugins.xml`, still works. In this mode plugins show up
in the Marketplace tab only when searched for, alongside "real" Marketplace
results that will fail offline. Prefer methods 1 or 2.

## Configuration

Set values with environment variables (`Section__Key`) or `appsettings.json`
(`"Section": { "Key": ... }`).

| Setting | Default | Description |
|---------|---------|-------------|
| `Jetpack__ApiKey` | — | Key for uploads/deletes (`X-Api-Key` header or `Authorization: Bearer`). **Without it, uploads are disabled.** Legacy name: `JETPACK_API_KEY`. |
| `Jetpack__AllowAnonymousUploads` | `false` | Allow writes without a key when none is configured (test setups only). |
| `Jetpack__MaxUploadBytes` | `2147483648` | Maximum plugin archive size. |
| `Jetpack__RefreshIntervalSeconds` | `60` | How often storage is rescanned for changes made by other instances. `0` turns rescanning off. |
| `Jetpack__FeaturedPlugins__0`, `__1`, ... | — | Plugin ids shown under "Staff Picks". All plugins are shown there when empty. |
| `Jetpack__PublicUrl` | — | Only for absolute URLs in `updatePlugins.xml`. Not needed otherwise; all other URLs are relative. |
| `Jetpack__LogRequests` | `true` | Log every request with the IDE's user agent. |
| `Storage__Provider` | `FileSystem` | `FileSystem` or `S3`. |
| `Storage__Path` | `/data` (container) | Directory for the file system provider. |
| `Storage__S3__Endpoint` | — | `host:port`, no scheme. Legacy: `MINIO_ENDPOINT` (implies `S3`). |
| `Storage__S3__AccessKey` / `SecretKey` | — | Legacy: `MINIO_ACCESS_KEY` / `MINIO_SECRET_KEY`. |
| `Storage__S3__Secure` | `false` | HTTPS to the S3 endpoint. Legacy: `MINIO_SECURE`. |
| `Storage__S3__Bucket` | `jetpack` | Created if missing. Legacy: `MINIO_PLUGINS_BUCKET`. |
| `Storage__S3__Prefix` | `jetpack` | Key prefix inside the bucket. Legacy: `MINIO_OBJECT_PREFIX`. |
| `Storage__S3__Region` | — | Optional. |

Kestrel/ASP.NET settings, such as TLS:

| Setting | Example |
|---------|---------|
| `ASPNETCORE_HTTP_PORTS` / `ASPNETCORE_HTTPS_PORTS` | `8080` / `8443` |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` | `/certs/jetpack.crt` (PEM, may include the chain) |
| `ASPNETCORE_Kestrel__Certificates__Default__KeyPath` | `/certs/jetpack.key` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` (set in the image; honours `X-Forwarded-*` from a reverse proxy) |

To terminate TLS in a reverse proxy instead, see `deploy/nginx.conf.example`.

## HTTP API

**Marketplace protocol** (used by the IDE; you normally don't call these yourself):

| Endpoint | Purpose |
|----------|---------|
| `GET /api/search/plugins?search=&orderBy=&tags=&organization=&build=&max=&all=` | Marketplace tab and search |
| `GET /api/search/aggregation/{tags\|organizations}?build=` | Filter values |
| `GET /api/search/updates/compatible?build=&pluginXmlId=...&module=` | Update check (2024.2+) |
| `POST /api/search/compatibleUpdates` `{"build","pluginXMLIds"}` | Update check (≤ 2025.3) |
| `POST /api/search/updates/nearest` | Nearest update |
| `GET /files/{pluginId}/{updateId}/meta.json`, `GET /files/{pluginId}/meta.json` | Plugin details |
| `GET /files/pluginsXMLIds.json`, `/files/jbPluginsXMLIds.json` | Known plugin ids |
| `GET /files/brokenPlugins.json` | Broken plugin list (404 unless uploaded, so the IDE keeps its bundled list) |
| `GET /api/icon?pluginId=&theme=DARCULA` | Plugin icon |
| `GET /pluginManager?id=&build=` | Download, redirecting to `/files/{pluginId}/{updateId}/{file}` |
| `GET /plugin/download?updateId=` or `?pluginId=&version=` | Download a specific version |

**Management:**

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `POST /api/plugins/upload` (alias `POST /api/plugins`) | key | Upload. Raw body with `?filename=`, or `multipart/form-data` with any number of files. Options: `?force=true` (replace), `?tags=A,B`. Returns 201/200, 409 if the version exists, 400 with a reason if invalid. |
| `GET /api/plugins` | — | All plugins and versions (JSON) |
| `GET /api/plugins/{id}` | — | One plugin |
| `DELETE /api/plugins/{id}/{version}` | key | Delete one version |
| `DELETE /api/plugins/{id}` | key | Delete all versions |
| `PUT /files/brokenPlugins.json` | key | Store a mirrored broken-plugins list |
| `POST /api/catalog/refresh` | key | Rescan storage now |
| `GET /updatePlugins.xml?build=` | — | Classic custom-repository XML |
| `GET /health` | — | Liveness/readiness (`503` until storage has been read) |

## Operations

**Storage layout.** Everything is in the storage directory or bucket. Back it
up like any other data:

```
plugins/{plugin-id}/{version}/release.json     metadata; its presence publishes the version
plugins/{plugin-id}/{version}/{file}.zip|.jar  the plugin, exactly as uploaded
plugins/{plugin-id}/{version}/pluginIcon*.svg  icons extracted from the plugin
static/brokenPlugins.json                      optional
stats/downloads.json                           download counters
```

Storage is the single source of truth. The in-memory catalog is rebuilt from
it at start-up and on every refresh. This means:

* several Jetpack instances can share one S3 bucket behind a load balancer;
* you can restore a backup or copy release folders in place without
  touching the API (picked up within `RefreshIntervalSeconds`, or call
  `POST /api/catalog/refresh`).

**Resources.** Uploads are streamed to a temporary file, and downloads are
streamed from storage. Memory use doesn't grow with plugin size.

## Upgrading from the previous Jetpack

* Your environment variables (`MINIO_*`, `JETPACK_API_KEY`) keep working.
  `MINIO_METADATA_BUCKET` and `JETPACK_USE_HTTPS` are no longer needed.
* IDEs configured with `/api/plugins/updatePlugins.xml` keep working. Old
  download links (`/api/plugins/download/{id}-{version}.zip`) redirect to the
  new location.
* The storage layout changed. Re-upload the existing archives once.
  Single-jar plugins that the old version had stored as `.zip`, which the IDE
  couldn't install, are detected and fixed during the re-upload:

  ```bash
  mc cp --recursive old-minio/storage/jetpack/ old-plugins/
  rm -rf old-plugins/plugins     # in case the new version already wrote here
  scripts/jetpack-mirror.py upload old-plugins/ --server https://... --api-key ...
  ```

Problems fixed relative to the old version:

* jar plugins were served as `.zip`, so the IDE unpacked them into a broken install;
* `plugin.xml` files with a `<!DOCTYPE>` were rejected;
* absolute `http://` download URLs broke HTTPS setups (the IDE refuses
  https→http redirects);
* uploads and downloads were buffered entirely in memory;
* uploads were possible with an empty API key;
* the in-memory catalog diverged between instances;
* only the classic custom-repository XML was supported, not the Marketplace protocol.

## Troubleshooting

* **Watch the log.** Every request is logged with the IDE's user agent, e.g.
  `GET /api/search/plugins?... -> 200 (WebStorm/262.11879.105)`. If nothing
  shows up when you open the Marketplace tab, DNS or TLS isn't set up yet.
  Requests that Jetpack doesn't handle are logged as warnings
  (`No handler for ...`).
* **"PKIX path building failed" / certificate errors** in the IDE: the CA is
  not trusted on that machine, or the certificate doesn't list
  `plugins.jetbrains.com` (check with
  `openssl s_client -connect plugins.jetbrains.com:443 -servername plugins.jetbrains.com`).
* **Headless provisioning** (`idea.sh installPlugins <id> ...` in CI or image
  builds) runs before the IDE's certificate manager is initialised and uses
  only the JVM's default trust store. Pass
  `-Djavax.net.ssl.trustStore=<store with ca.crt> -Djavax.net.ssl.trustStorePassword=changeit`
  through the IDE's VM options file for those runs.
* **A plugin doesn't appear in the IDE:** no uploaded version is compatible
  with that IDE build. Check with
  `curl "https://<jetpack>/api/search/plugins?search=<name>&build=IU-252.25557.131&all=true"`
  (`nearestUpdate.isCompatible: false` means incompatible) and mirror a version
  for that build.
* **"Plugin X requires plugin Y"**: upload Y as well, or use
  `jetpack-mirror.py download --deps`.
* **A platform-specific plugin is missing on some machines:** no build for
  that OS/CPU was uploaded. `GET /api/plugins/<id>` lists each version's
  `platform` (e.g. `linux/x86_64`).
* **IDE-side detail:** add
  `-Didea.log.debug.categories=#com.intellij.ide.plugins.marketplace,#com.intellij.openapi.updateSettings.impl.PluginDownloader`
  and look at `idea.log` (*Help | Show Log in ...*).

Out of scope: paid-plugin licensing, ratings and reviews, and the other
JetBrains services an IDE contacts (IDE updates, accounts, AI), which use
other host names.

## Development

```bash
dotnet test                          # unit + in-process integration tests
dotnet run --project Jetpack.Api     # http://localhost:5132, API key "dev-key", data in Jetpack.Api/data
docker build -f Jetpack.Api/Dockerfile -t jetpack .
```

The protocol details follow `MarketplaceUrls.kt`, `MarketplaceRequests.kt` and
`PluginRepositoryObjects.kt` in
[intellij-community](https://github.com/JetBrains/intellij-community/tree/master/platform/platform-impl/src/com/intellij/ide/plugins/marketplace).
`BuildNumber` and `VersionComparator` are ports of the IDE's own classes, so
compatibility and "is newer" decisions match the IDE exactly.

The web UI uses ASP.NET Core Razor Pages, laid out like the standard
`dotnet new webapp` template:

* `Jetpack.Api/Pages/`: the pages, with `Shared/_Layout.cshtml` for the
  common frame.
* `Jetpack.Api/wwwroot/`: static files, served through `MapStaticAssets`
  with fingerprinted, pre-compressed URLs.
* Bootstrap comes from that template's `wwwroot/lib`.

Static files are only copied by `dotnet publish` (as in the Dockerfile).
`dotnet run` serves them from the project folder.
