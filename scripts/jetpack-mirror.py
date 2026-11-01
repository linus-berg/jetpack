#!/usr/bin/env python3
"""Move JetBrains Marketplace plugins across an air gap into Jetpack.

On a machine WITH internet access, download the newest version of each plugin
that is compatible with the IDE builds you use (Help | About shows the build,
e.g. "Build #IU-252.25557.131"):

    jetpack-mirror.py download -b IU-252.25557.131 -b WS-252.25557.126 \\
        -o bundle/ --deps --broken-plugins org.rust.lang "String Manipulation"
    jetpack-mirror.py download -b IU-252.25557.131 -o bundle/ -f plugins.txt

Plugins published as separate builds per OS/CPU (e.g. GitHub Copilot) are
fetched for every platform in --platform (default: all of them).

Carry bundle/ into the offline network, then upload everything:

    jetpack-mirror.py upload --server https://plugins.jetbrains.com \\
        --api-key "$JETPACK_API_KEY" --ca-cert ca.crt bundle/

Only the Python 3 standard library is required. HTTPS_PROXY is honoured.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

USER_AGENT = "jetpack-mirror/2.0"
MANIFEST = "manifest.json"
BROKEN_PLUGINS = "brokenPlugins.json"
# --platform name -> (os, arch) as the IDE reports them to the Marketplace.
PLATFORMS = {
    "linux-x64": ("Linux", "X86_64"),
    "linux-arm64": ("Linux", "ARM64"),
    "windows-x64": ("Windows", "X86_64"),
    "windows-arm64": ("Windows", "ARM64"),
    "macos-x64": ("macOS", "X86_64"),
    "macos-arm64": ("macOS", "ARM64"),
}
PLATFORM_MODULES = ("com.intellij.modules.os.", "com.intellij.modules.arch.")


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def ssl_context(ca_cert: str | None, insecure: bool) -> ssl.SSLContext:
    context = ssl.create_default_context(cafile=ca_cert)
    if insecure:
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
    return context


def request(url: str, context: ssl.SSLContext, *, method: str = "GET",
            data=None, headers: dict | None = None, timeout: int = 120):
    all_headers = {"User-Agent": USER_AGENT}
    all_headers.update(headers or {})
    req = urllib.request.Request(url, data=data, method=method,
                                 headers=all_headers)
    return urllib.request.urlopen(req, context=context, timeout=timeout)


def get_json(url: str, context: ssl.SSLContext):
    with request(url, context, headers={"Accept": "application/json"}) as r:
        return json.load(r)


def safe_file_name(name: str) -> str:
    name = urllib.parse.unquote(name)
    name = re.sub(r"[^A-Za-z0-9._+-]", "_", name).strip("._")
    return name or "plugin.zip"


def load_manifest(directory: Path) -> dict:
    path = directory / MANIFEST
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    return {"plugins": {}}


def save_manifest(directory: Path, manifest: dict) -> None:
    path = directory / MANIFEST
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(manifest, indent=2, sort_keys=True),
                   encoding="utf-8")
    tmp.replace(path)


def read_ids(args) -> list[str]:
    ids = list(args.plugins)
    if args.file:
        for line in Path(args.file).read_text(encoding="utf-8").splitlines():
            line = line.split("#", 1)[0].strip()
            if line:
                ids.append(line)
    seen = set()
    return [i for i in ids if not (i in seen or seen.add(i))]


def platform_of(meta: dict) -> str | None:
    """'linux/x86_64' for platform-specific builds, None otherwise."""
    deps = meta.get("dependencies", [])
    os_part = [d.rsplit(".", 1)[-1] for d in deps if d.startswith(PLATFORM_MODULES[0])]
    arch_part = [d.rsplit(".", 1)[-1] for d in deps if d.startswith(PLATFORM_MODULES[1])]
    if not os_part and not arch_part:
        return None
    return f"{'+'.join(os_part) or 'any'}/{'+'.join(arch_part) or 'any'}"


def parse_platforms(values: list[str] | None) -> list[str]:
    names = []
    for value in values or ["all"]:
        for name in value.split(","):
            name = name.strip().lower()
            if name == "all":
                names.extend(PLATFORMS)
            elif name in PLATFORMS:
                names.append(name)
            elif name:
                raise SystemExit(f"Unknown platform '{name}'. Choose from: "
                                 f"all, {', '.join(PLATFORMS)}")
    return list(dict.fromkeys(names))


def compatible_update(base: str, context: ssl.SSLContext, build: str,
                      plugin_id: str, platform: str):
    os_name, arch = PLATFORMS[platform]
    query = urllib.parse.urlencode({"build": build, "os": os_name, "arch": arch,
                                    "pluginXmlId": plugin_id})
    updates = get_json(f"{base}/api/search/updates/compatible?{query}", context)
    return updates[0] if updates else None


def download(args) -> int:
    out = Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    context = ssl_context(args.ca_cert, args.insecure)
    base = args.marketplace.rstrip("/")
    manifest = load_manifest(out)
    plugins = manifest["plugins"]
    ids = read_ids(args)
    platforms = parse_platforms(args.platform)
    if not ids:
        log("No plugin ids given (positional arguments or --file).")
        return 2

    failures = 0
    for build in args.build:
        queue = [(i, None) for i in ids]
        seen: set[str] = set()
        while queue:
            plugin_id, required_by = queue.pop(0)
            if plugin_id in seen:
                continue
            seen.add(plugin_id)
            # Ask for the first platform; only plugins whose answer turns out
            # to be platform-specific are looked up for the other platforms.
            variants = []  # (update, meta)
            for index, platform in enumerate(platforms):
                try:
                    update = compatible_update(base, context, build, plugin_id,
                                               platform)
                except urllib.error.URLError as e:
                    log(f"  ! {plugin_id}: Marketplace request failed: {e}")
                    failures += 1
                    break
                if update is None or any(str(u["id"]) == str(update["id"])
                                         for u, _ in variants):
                    if index == 0 and update is None:
                        break
                    continue
                meta = {}
                try:
                    meta = get_json(f"{base}/files/{update['pluginId']}/"
                                    f"{update['id']}/meta.json", context)
                except urllib.error.URLError as e:
                    log(f"  ! {plugin_id}: could not read metadata: {e}")
                variants.append((update, meta))
                if index == 0 and platform_of(meta) is None:
                    break

            if not variants:
                if required_by:
                    log(f"  - {plugin_id} (needed by {required_by}) is not on "
                        f"the Marketplace for {build}; probably bundled")
                else:
                    log(f"  ! {plugin_id}: no version compatible with {build}")
                    failures += 1
                continue

            for update, meta in variants:
                update_id = str(update["id"])
                version = update["version"]
                platform_label = platform_of(meta)
                suffix = f" [{platform_label}]" if platform_label else ""
                entry = plugins.get(update_id)
                if entry and (out / entry["file"]).exists():
                    log(f"  = {plugin_id} {version}{suffix} for {build} "
                        f"(already have {entry['file']})")
                else:
                    try:
                        entry = fetch_artifact(base, context, out, plugin_id,
                                               version, update_id)
                    except (urllib.error.URLError, OSError, ValueError) as e:
                        log(f"  ! {plugin_id} {version}: download failed: {e}")
                        failures += 1
                        continue
                    entry["tags"] = meta.get("tags", [])
                    entry["platform"] = platform_label
                    plugins[update_id] = entry
                    log(f"  + {plugin_id} {version}{suffix} for {build} -> "
                        f"{entry['file']}")

                entry.setdefault("builds", [])
                if build not in entry["builds"]:
                    entry["builds"].append(build)
                save_manifest(out, manifest)

            if args.deps:
                for dep in variants[0][1].get("dependencies", []):
                    if not dep.startswith("com.intellij.modules."):
                        queue.append((dep, plugin_id))

    if args.broken_plugins:
        try:
            with request(f"{base}/files/{BROKEN_PLUGINS}", context) as r:
                (out / BROKEN_PLUGINS).write_bytes(r.read())
            log(f"  + {BROKEN_PLUGINS}")
        except urllib.error.URLError as e:
            log(f"  ! {BROKEN_PLUGINS}: {e}")
            failures += 1

    save_manifest(out, manifest)
    log(f"Done: {len(plugins)} plugin files in {out}"
        + (f", {failures} problem(s)" if failures else ""))
    return 1 if failures else 0


def fetch_artifact(base: str, context: ssl.SSLContext, out: Path,
                   plugin_id: str, version: str, update_id: str) -> dict:
    url = f"{base}/plugin/download?rel=true&updateId={update_id}"
    with request(url, context, timeout=600) as r:
        final_path = urllib.parse.urlparse(r.geturl()).path
        name = safe_file_name(final_path.rsplit("/", 1)[-1])
        if not name.endswith((".zip", ".jar")):
            name += ".zip"
        tmp = out / (name + ".part")
        digest = hashlib.sha256()
        with open(tmp, "wb") as f:
            while chunk := r.read(1024 * 1024):
                digest.update(chunk)
                f.write(chunk)
    with open(tmp, "rb") as f:
        if f.read(2) != b"PK":
            tmp.unlink()
            raise ValueError("response is not a zip/jar archive")
    tmp.replace(out / name)
    return {"xmlId": plugin_id, "version": version, "updateId": update_id,
            "file": name, "sha256": digest.hexdigest()}


def upload(args) -> int:
    directory = Path(args.directory)
    api_key = args.api_key or os.environ.get("JETPACK_API_KEY")
    if not api_key:
        log("An API key is required (--api-key or JETPACK_API_KEY).")
        return 2
    context = ssl_context(args.ca_cert, args.insecure)
    server = args.server.rstrip("/")
    manifest = load_manifest(directory)
    tags_by_file = {e["file"]: e.get("tags", [])
                    for e in manifest["plugins"].values()}
    files = sorted(p for p in directory.iterdir()
                   if p.suffix.lower() in (".zip", ".jar") and p.is_file())
    counts = {"uploaded": 0, "exists": 0, "failed": 0}

    for path in files:
        params = {"filename": path.name}
        if tags_by_file.get(path.name):
            params["tags"] = ",".join(tags_by_file[path.name])
        if args.force:
            params["force"] = "true"
        url = f"{server}/api/plugins/upload?{urllib.parse.urlencode(params)}"
        headers = {"X-Api-Key": api_key,
                   "Content-Type": "application/octet-stream",
                   "Content-Length": str(path.stat().st_size)}
        try:
            with open(path, "rb") as f, request(url, context, method="POST",
                                                data=f, headers=headers,
                                                timeout=1800) as r:
                body = json.load(r)
            plugin = body.get("plugin") or {}
            log(f"  + {path.name}: {plugin.get('id')} {plugin.get('version')}"
                f" ({body.get('status')})")
            counts["uploaded"] += 1
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")
            if e.code == 409:
                log(f"  = {path.name}: already on the server")
                counts["exists"] += 1
            else:
                try:
                    detail = json.loads(detail).get("error") or detail
                except ValueError:
                    pass
                log(f"  ! {path.name}: HTTP {e.code}: {detail}")
                counts["failed"] += 1
        except (urllib.error.URLError, OSError) as e:
            log(f"  ! {path.name}: {e}")
            counts["failed"] += 1

    broken = directory / BROKEN_PLUGINS
    if broken.exists():
        try:
            with request(f"{server}/files/{BROKEN_PLUGINS}", context,
                         method="PUT", data=broken.read_bytes(),
                         headers={"X-Api-Key": api_key,
                                  "Content-Type": "application/json"}):
                pass
            log(f"  + {BROKEN_PLUGINS}")
        except urllib.error.URLError as e:
            log(f"  ! {BROKEN_PLUGINS}: {e}")
            counts["failed"] += 1

    log(f"Done: {counts['uploaded']} uploaded, {counts['exists']} already "
        f"present, {counts['failed']} failed")
    return 1 if counts["failed"] else 0


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    d = sub.add_parser("download", help="download plugins from the Marketplace")
    d.add_argument("plugins", nargs="*", help="plugin ids (xmlId)")
    d.add_argument("-b", "--build", action="append", required=True,
                   help="IDE build, e.g. IU-252.25557.131 (repeatable)")
    d.add_argument("-f", "--file", help="file with one plugin id per line")
    d.add_argument("-o", "--output", default="bundle", help="output directory")
    d.add_argument("-p", "--platform", action="append",
                   help="OS/CPU variants to fetch for platform-specific "
                        f"plugins (repeatable or comma separated): all, "
                        f"{', '.join(PLATFORMS)}. Default: all")
    d.add_argument("--deps", action="store_true",
                   help="also download required plugin dependencies")
    d.add_argument("--broken-plugins", action="store_true",
                   help=f"also download the Marketplace's {BROKEN_PLUGINS}")
    d.add_argument("--marketplace", default="https://plugins.jetbrains.com")
    d.add_argument("--ca-cert", help="CA bundle for TLS verification")
    d.add_argument("--insecure", action="store_true",
                   help="do not verify TLS certificates")
    d.set_defaults(func=download)

    u = sub.add_parser("upload", help="upload a downloaded bundle to Jetpack")
    u.add_argument("directory", help="bundle directory")
    u.add_argument("--server", required=True, help="Jetpack base URL")
    u.add_argument("--api-key", help="defaults to $JETPACK_API_KEY")
    u.add_argument("--force", action="store_true",
                   help="replace versions that already exist")
    u.add_argument("--ca-cert", help="CA certificate of the Jetpack server")
    u.add_argument("--insecure", action="store_true",
                   help="do not verify TLS certificates")
    u.set_defaults(func=upload)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
