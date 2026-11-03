// Jetpack web UI. Plain JavaScript, no external dependencies besides the
// bundled Bootstrap. Untrusted text is only ever inserted with textContent.
(() => {
  "use strict";

  const API_KEY_STORAGE = "jetpack-api-key";

  // ---------------------------------------------------------------- helpers
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function formatBytes(bytes) {
    const units = ["B", "KB", "MB", "GB", "TB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    return unit === 0 ? `${bytes} B` : `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`;
  }

  function toast(message, kind = "success") {
    const node = $("#jp-toast");
    if (!node || !window.bootstrap) return;
    node.classList.remove("text-bg-success", "text-bg-danger", "text-bg-secondary");
    node.classList.add(kind === "error" ? "text-bg-danger" : kind === "info" ? "text-bg-secondary" : "text-bg-success");
    $("[data-jp-toast-body]", node).textContent = message;
    bootstrap.Toast.getOrCreateInstance(node, { delay: 3500 }).show();
  }

  function storedKey() {
    try {
      return sessionStorage.getItem(API_KEY_STORAGE) || "";
    } catch {
      return "";
    }
  }

  function rememberKey(key, remember) {
    try {
      if (remember && key) sessionStorage.setItem(API_KEY_STORAGE, key);
      else if (!remember) sessionStorage.removeItem(API_KEY_STORAGE);
    } catch {
      // Storage unavailable: nothing to remember.
    }
  }

  function problemMessage(status, body) {
    if (body && (body.title || body.detail)) return [body.title, body.detail].filter(Boolean).join(": ");
    if (body && body.error) return body.error;
    if (status === 401) return "Invalid or missing API key.";
    if (status === 403) return "Write access is disabled on the server.";
    return `Request failed (HTTP ${status}).`;
  }

  // ------------------------------------------------------------------ theme
  $$("[data-jp-theme-toggle]").forEach((button) => {
    button.addEventListener("click", () => {
      const root = document.documentElement;
      const next = root.getAttribute("data-bs-theme") === "dark" ? "light" : "dark";
      root.setAttribute("data-bs-theme", next);
      try {
        localStorage.setItem("jetpack-theme", next);
      } catch {
        // Not persisted.
      }
    });
  });

  // ------------------------------------------------------------------- copy
  async function copyText(text) {
    // The Clipboard API needs a secure context; plain-HTTP intranet hosts
    // fall back to a temporary textarea.
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return;
    }
    const area = element("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.className = "position-fixed top-0 start-0 opacity-0";
    document.body.appendChild(area);
    area.select();
    const ok = document.execCommand("copy");
    area.remove();
    if (!ok) throw new Error("copy failed");
  }

  $$("[data-jp-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
      const source = $(button.getAttribute("data-jp-copy"));
      if (!source) return;
      try {
        await copyText(source.textContent.trim());
        button.classList.add("jp-copied");
        setTimeout(() => button.classList.remove("jp-copied"), 1500);
        toast("Copied to clipboard");
      } catch {
        toast("Could not copy; select the text manually.", "error");
      }
    });
  });

  // ------------------------------------------------------------- API keys
  $$("[data-jp-api-key]").forEach((input) => {
    if (!input.value) input.value = storedKey();
  });
  $$("[data-jp-remember-key]").forEach((checkbox) => {
    checkbox.checked = storedKey() !== "";
  });

  // ------------------------------------------------- catalog: live filter
  const filter = $("[data-jp-filter]");
  const cards = $$("[data-jp-search-text]");
  if (filter && cards.length > 0) {
    const count = $("[data-jp-count]");
    const noMatches = $("[data-jp-no-matches]");
    const initialCount = count ? count.textContent : "";
    filter.addEventListener("input", () => {
      const terms = filter.value.toLowerCase().split(/\s+/).filter(Boolean);
      let visible = 0;
      cards.forEach((card) => {
        const text = card.getAttribute("data-jp-search-text");
        const match = terms.every((term) => text.includes(term));
        card.classList.toggle("d-none", !match);
        if (match) visible++;
      });
      if (count) count.textContent = terms.length ? `${visible} of ${cards.length} plugins` : initialCount;
      if (noMatches) noMatches.classList.toggle("d-none", visible > 0);
    });
  }

  $$("[data-jp-autosubmit]").forEach((select) => {
    select.addEventListener("change", () => select.form && select.form.submit());
  });

  // ---------------------------------------------------------------- upload
  const uploadForm = $("[data-jp-upload-form]");
  if (uploadForm) {
    const maxBytes = Number(uploadForm.getAttribute("data-jp-max-bytes")) || Infinity;
    const input = $("[data-jp-files]", uploadForm);
    const dropzone = $("[data-jp-dropzone]", uploadForm);
    const queueList = $("[data-jp-queue]", uploadForm);
    const summary = $("[data-jp-summary]", uploadForm);
    const startButton = $("[data-jp-start]", uploadForm);
    const clearButton = $("[data-jp-clear]", uploadForm);
    const keyInput = $("[data-jp-api-key]", uploadForm);
    const rememberInput = $("[data-jp-remember-key]", uploadForm);
    const forceInput = $("[data-jp-force]", uploadForm);
    const tagsInput = $("[data-jp-tags]", uploadForm);
    let queue = [];
    let busy = false;

    const statusLabels = {
      created: ["Added", "text-bg-success"],
      replaced: ["Replaced", "text-bg-success"],
      conflict: ["Already exists", "text-bg-secondary"],
      invalid: ["Rejected", "text-bg-danger"],
      too_large: ["Too large", "text-bg-danger"],
      error: ["Failed", "text-bg-danger"],
      pending: ["Waiting", "text-bg-light"],
      uploading: ["Uploading", "text-bg-primary"]
    };

    function refresh() {
      const pending = queue.filter((item) => item.state === "pending").length;
      startButton.disabled = busy || pending === 0;
      clearButton.disabled = busy || queue.length === 0;
      queueList.classList.toggle("d-none", queue.length === 0);
      const total = queue.reduce((sum, item) => sum + item.file.size, 0);
      summary.textContent = queue.length === 0
        ? "No files selected."
        : `${queue.length} file${queue.length === 1 ? "" : "s"}, ${formatBytes(total)}`;
    }

    function setState(item, state, message, link) {
      item.state = state;
      const [label, css] = statusLabels[state] || statusLabels.error;
      item.badge.className = `badge ${css}`;
      item.badge.textContent = label;
      item.message.textContent = message || "";
      item.message.classList.toggle("d-none", !message);
      item.link.classList.toggle("d-none", !link);
      if (link) item.link.href = link;
      item.progress.classList.toggle("d-none", state !== "uploading");
    }

    function addFiles(files) {
      for (const file of files) {
        const row = element("li", "list-group-item jp-queue-item");
        const top = element("div", "d-flex align-items-center gap-2");
        const name = element("span", "text-truncate fw-medium flex-grow-1 min-w-0", file.name);
        const size = element("span", "small text-body-secondary text-nowrap", formatBytes(file.size));
        const badge = element("span");
        const link = element("a", "small d-none", "View");
        const progress = element("div", "progress mt-2 d-none");
        const bar = element("div", "progress-bar");
        progress.appendChild(bar);
        const message = element("div", "small mt-1 text-body-secondary d-none");
        top.append(name, size, badge, link);
        row.append(top, progress, message);
        queueList.appendChild(row);
        const item = { file, row, badge, link, progress, bar, message, state: "pending" };
        queue.push(item);
        if (!/\.(zip|jar)$/i.test(file.name)) {
          setState(item, "invalid", "Only .zip and .jar files are plugin archives.");
        } else if (file.size > maxBytes) {
          setState(item, "too_large", `Larger than the server limit of ${formatBytes(maxBytes)}.`);
        } else {
          setState(item, "pending");
        }
      }
      refresh();
    }

    function upload(item, key) {
      return new Promise((resolve) => {
        const params = new URLSearchParams({ filename: item.file.name });
        if (forceInput.checked) params.set("force", "true");
        if (tagsInput.value.trim()) params.set("tags", tagsInput.value.trim());
        const request = new XMLHttpRequest();
        request.open("POST", `/api/plugins/upload?${params}`);
        request.setRequestHeader("Content-Type", "application/octet-stream");
        if (key) request.setRequestHeader("X-Api-Key", key);
        request.upload.addEventListener("progress", (event) => {
          if (event.lengthComputable) item.bar.style.width = `${(event.loaded / event.total) * 100}%`;
        });
        request.addEventListener("load", () => {
          let body = null;
          try {
            body = JSON.parse(request.responseText);
          } catch {
            // Not JSON.
          }
          const plugin = body && body.plugin;
          const detail = plugin ? `${plugin.name} ${plugin.version} (${plugin.id})` : "";
          const link = plugin ? `/plugin/index?xmlId=${encodeURIComponent(plugin.id)}` : null;
          if (body && body.status && statusLabels[body.status]) {
            setState(item, body.status, body.error || detail, link);
          } else {
            setState(item, "error", problemMessage(request.status, body));
          }
          resolve(request.status);
        });
        request.addEventListener("error", () => {
          setState(item, "error", "Network error while uploading.");
          resolve(0);
        });
        setState(item, "uploading");
        item.bar.style.width = "0%";
        request.send(item.file);
      });
    }

    input.addEventListener("change", () => {
      addFiles(input.files);
      input.value = "";
    });
    ["dragenter", "dragover"].forEach((type) => dropzone.addEventListener(type, (event) => {
      event.preventDefault();
      dropzone.classList.add("jp-dragover");
    }));
    ["dragleave", "drop"].forEach((type) => dropzone.addEventListener(type, (event) => {
      event.preventDefault();
      dropzone.classList.remove("jp-dragover");
    }));
    dropzone.addEventListener("drop", (event) => addFiles(event.dataTransfer.files));

    clearButton.addEventListener("click", () => {
      queue = [];
      queueList.replaceChildren();
      refresh();
    });

    uploadForm.addEventListener("submit", async (event) => {
      event.preventDefault();
      const key = keyInput.value.trim();
      if (keyInput.required && !key) {
        keyInput.classList.add("is-invalid");
        keyInput.focus();
        return;
      }
      keyInput.classList.remove("is-invalid");
      rememberKey(key, rememberInput.checked);
      busy = true;
      refresh();
      let added = 0;
      for (const item of queue.filter((entry) => entry.state === "pending")) {
        const status = await upload(item, key);
        if (status === 200 || status === 201) added++;
        if (status === 401 || status === 403) {
          // Further attempts would fail the same way.
          queue.filter((entry) => entry.state === "pending")
               .forEach((entry) => setState(entry, "pending", "Not uploaded: fix the API key and try again."));
          keyInput.classList.add("is-invalid");
          break;
        }
      }
      busy = false;
      refresh();
      if (added > 0) toast(`${added} plugin version${added === 1 ? "" : "s"} uploaded`);
    });

    refresh();
  }

  // ---------------------------------------------------------------- delete
  const dialog = $("#jp-delete-dialog");
  if (dialog && window.bootstrap) {
    const modal = bootstrap.Modal.getOrCreateInstance(dialog);
    const form = $("[data-jp-delete-form]", dialog);
    const message = $("[data-jp-delete-message]", dialog);
    const errorBox = $("[data-jp-delete-error]", dialog);
    const keyInput = $("[data-jp-api-key]", dialog);
    const rememberInput = $("[data-jp-remember-key]", dialog);
    const confirmButton = $("[data-jp-delete-confirm]", dialog);
    let target = null;

    $$("[data-jp-delete]").forEach((button) => {
      button.addEventListener("click", () => {
        target = {
          plugin: button.getAttribute("data-jp-plugin"),
          name: button.getAttribute("data-jp-name"),
          version: button.getAttribute("data-jp-version"),
          count: Number(button.getAttribute("data-jp-count") || "1")
        };
        $("#jp-delete-title").textContent = target.version ? "Delete version" : "Delete plugin";
        message.textContent = target.version
          ? `Delete version ${target.version} of ${target.name}? IDEs can no longer install or update to it.`
          : `Delete ${target.name} and all ${target.count} of its versions? IDEs can no longer install it.`;
        errorBox.classList.add("d-none");
        modal.show();
      });
    });

    dialog.addEventListener("shown.bs.modal", () => keyInput.focus());

    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      if (!target) return;
      const key = keyInput.value.trim();
      const url = `/api/plugins/${encodeURIComponent(target.plugin)}` +
        (target.version ? `/${encodeURIComponent(target.version)}` : "");
      confirmButton.disabled = true;
      try {
        const response = await fetch(url, { method: "DELETE", headers: key ? { "X-Api-Key": key } : {} });
        if (response.ok) {
          rememberKey(key, rememberInput.checked);
          const pluginGone = !target.version || target.count <= 1 ||
            document.querySelectorAll("#pane-versions tbody tr").length <= 1;
          window.location.href = pluginGone ? "/" : window.location.href;
          return;
        }
        const body = await response.json().catch(() => null);
        errorBox.textContent = problemMessage(response.status, body);
        errorBox.classList.remove("d-none");
      } catch {
        errorBox.textContent = "Network error.";
        errorBox.classList.remove("d-none");
      } finally {
        confirmButton.disabled = false;
      }
    });
  }
})();
