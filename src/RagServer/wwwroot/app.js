"use strict";

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const size = (b) => b < 1024 ? `${b} B` : b < 1048576 ? `${(b / 1024).toFixed(1)} KB` : `${(b / 1048576).toFixed(1)} MB`;
const when = (d) => new Date(d).toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" });
const location_ = (h) => h.startLine != null ? `lines ${h.startLine}–${h.endLine}` : h.page != null ? `page ${h.page}` : "";

let docs = [];
let pollTimer = null;

async function api(path, init) {
  const res = await fetch(path, init);
  if (!res.ok && res.status !== 503) throw new Error(`${res.status} ${await res.text()}`);
  return res.status === 204 || res.status === 202 ? null : res.json();
}

// ---------- health ----------
async function checkHealth() {
  const el = $("health");
  try {
    const h = await api("/api/health");
    el.textContent = h.status === "ok" ? `ok · ${h.model} @ ${h.ollamaUrl}` : `degraded · db: ${h.database} · ollama: ${h.ollama}`;
    el.className = "pill " + (h.status === "ok" ? "ok" : "bad");
  } catch (e) {
    el.textContent = "server unreachable";
    el.className = "pill bad";
  }
}

// ---------- collections ----------
async function loadCollections() {
  const cols = await api("/api/collections");
  $("collectionList").innerHTML = cols.map((c) => `<option value="${esc(c.name)}">`).join("");
  for (const id of ["filterCollection", "searchCollection"]) {
    const sel = $(id);
    const current = sel.value;
    sel.innerHTML = `<option value="">All collections</option>` +
      cols.map((c) => `<option value="${esc(c.name)}">${esc(c.name)} (${c.documents})</option>`).join("");
    sel.value = cols.some((c) => c.name === current) ? current : "";
  }
}

// ---------- documents ----------
async function loadDocs() {
  docs = await api("/api/documents");
  renderDocs();
  clearTimeout(pollTimer);
  if (docs.some((d) => d.status === "pending")) pollTimer = setTimeout(refresh, 2000);
}

function renderDocs() {
  const col = $("filterCollection").value;
  const name = $("filterName").value.toLowerCase();
  const rows = docs.filter((d) => (!col || d.collection === col) && (!name || d.name.toLowerCase().includes(name)));
  $("docCount").textContent = `(${rows.length}${rows.length !== docs.length ? ` of ${docs.length}` : ""})`;
  $("docs").innerHTML = rows.length === 0
    ? `<tr><td colspan="8" class="muted">No documents yet — drop some files above.</td></tr>`
    : rows.map((d) => `
      <tr>
        <td class="name"><a data-view="${d.id}">${esc(d.name)}</a></td>
        <td>${esc(d.collection)}</td>
        <td>${esc(d.language ?? d.contentType)}</td>
        <td class="num">${size(d.sizeBytes)}</td>
        <td class="num">${d.chunkCount}</td>
        <td class="status-${d.status}" title="${esc(d.error)}">${d.status}${d.error ? " ⚠" : ""}</td>
        <td>${when(d.updatedAt)}</td>
        <td><div class="row">
          <button class="small secondary" data-reindex="${d.id}">Re-index</button>
          <button class="small secondary danger" data-delete="${d.id}">Delete</button>
        </div></td>
      </tr>`).join("");
}

$("docs").addEventListener("click", async (e) => {
  const t = e.target;
  if (t.dataset.view) return openViewer(t.dataset.view);
  if (t.dataset.reindex) {
    t.disabled = true;
    await api(`/api/documents/${t.dataset.reindex}/reindex`, { method: "POST" });
    return refresh();
  }
  if (t.dataset.delete) {
    const d = docs.find((x) => x.id === t.dataset.delete);
    if (!confirm(`Delete ${d.collection}/${d.name}?`)) return;
    await api(`/api/documents/${d.id}`, { method: "DELETE" });
    return refresh();
  }
});
$("filterCollection").addEventListener("change", renderDocs);
$("filterName").addEventListener("input", renderDocs);

async function refresh() {
  await Promise.all([loadDocs(), loadCollections()]);
}

// ---------- upload ----------
async function upload(files) {
  files = files.filter((f) => !f.name.startsWith("."));
  if (files.length === 0) return;
  const collection = $("collection").value.trim() || "default";
  const log = $("uploadLog");
  // Send in small batches so large folders show progress.
  for (let i = 0; i < files.length; i += 10) {
    const form = new FormData();
    form.append("collection", collection);
    for (const f of files.slice(i, i + 10)) form.append("files", f, f.relPath || f.webkitRelativePath || f.name);
    try {
      const results = await api("/api/documents", { method: "POST", body: form });
      for (const r of results) {
        const li = document.createElement("li");
        li.className = r.outcome === "rejected" ? "status-failed" : r.outcome === "unchanged" ? "muted" : "status-ready";
        li.textContent = `${r.outcome}: ${r.collection}/${r.name}${r.error ? " — " + r.error : ""}`;
        log.prepend(li);
      }
    } catch (e) {
      const li = document.createElement("li");
      li.className = "status-failed";
      li.textContent = `upload failed: ${e.message}`;
      log.prepend(li);
    }
    await refresh();
  }
}

$("fileInput").addEventListener("change", (e) => { upload([...e.target.files]); e.target.value = ""; });
$("folderInput").addEventListener("change", (e) => { upload([...e.target.files]); e.target.value = ""; });

const drop = $("drop");
drop.addEventListener("dragover", (e) => { e.preventDefault(); drop.classList.add("over"); });
drop.addEventListener("dragleave", () => drop.classList.remove("over"));
drop.addEventListener("drop", async (e) => {
  e.preventDefault();
  drop.classList.remove("over");
  const entries = [...e.dataTransfer.items].map((i) => i.webkitGetAsEntry?.()).filter(Boolean);
  if (entries.length === 0) return upload([...e.dataTransfer.files]);
  const files = [];
  await Promise.all(entries.map((entry) => walk(entry, "", files)));
  upload(files);
});

// Recursively collect files from dropped folders, keeping relative paths.
async function walk(entry, prefix, out) {
  if (entry.isFile) {
    const file = await new Promise((res, rej) => entry.file(res, rej));
    file.relPath = prefix + file.name;
    out.push(file);
  } else if (entry.isDirectory) {
    if (entry.name.startsWith(".") || ["node_modules", "bin", "obj"].includes(entry.name)) return;
    const reader = entry.createReader();
    let batch;
    do {
      batch = await new Promise((res, rej) => reader.readEntries(res, rej));
      await Promise.all(batch.map((child) => walk(child, `${prefix}${entry.name}/`, out)));
    } while (batch.length > 0);
  }
}

// ---------- search ----------
$("searchForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const q = $("searchQuery").value.trim();
  const params = new URLSearchParams({ q, k: $("searchK").value });
  if ($("searchCollection").value) params.set("collection", $("searchCollection").value);
  const out = $("searchResults");
  out.innerHTML = `<p class="muted">Searching…</p>`;
  try {
    const hits = await api(`/api/search?${params}`);
    out.innerHTML = hits.length === 0 ? `<p class="muted">No results.</p>` : hits.map((h, i) => `
      <div class="hit">
        <div class="hit-head">
          <strong>[${i + 1}] ${esc(h.collection)}/${esc(h.documentName)} <span class="muted">${location_(h)}</span></strong>
          <span class="muted">score ${h.score.toFixed(4)}${h.similarity != null ? ` · similarity ${h.similarity.toFixed(2)}` : ""}${h.keywordMatch ? " · keyword match" : ""}</span>
        </div>
        ${h.headingPath ? `<div class="muted">${esc(h.headingPath)}</div>` : ""}
        <pre>${esc(h.content)}</pre>
      </div>`).join("");
  } catch (err) {
    out.innerHTML = `<p class="status-failed">${esc(err.message)}</p>`;
  }
});

// ---------- viewer ----------
let viewerDoc = null;
let viewerChunks = null;

async function openViewer(id) {
  const [doc, chunks] = await Promise.all([api(`/api/documents/${id}`), api(`/api/documents/${id}/chunks`)]);
  viewerDoc = doc;
  viewerChunks = chunks;
  $("viewerTitle").textContent = `${doc.collection}/${doc.name}`;
  $("viewerFile").href = `/api/documents/${id}/file`;
  showTab("chunks");
  $("viewer").showModal();
}

function showTab(tab) {
  document.querySelectorAll(".tab").forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
  const body = $("viewerBody");
  if (viewerDoc.status !== "ready") {
    body.innerHTML = `<p class="status-${viewerDoc.status}">${esc(viewerDoc.status)}${viewerDoc.error ? ": " + esc(viewerDoc.error) : ""}</p>`;
    return;
  }
  body.innerHTML = tab === "text"
    ? `<pre>${esc(viewerDoc.text)}</pre>`
    : viewerChunks.map((c) => `
      <div class="hit">
        <div class="hit-head"><strong>#${c.ordinal + 1} <span class="muted">${location_(c)}</span></strong><span class="muted">${c.content.length} chars</span></div>
        ${c.headingPath ? `<div class="muted">${esc(c.headingPath)}</div>` : ""}
        <pre>${esc(c.content)}</pre>
      </div>`).join("");
}

document.querySelectorAll(".tab").forEach((b) => b.addEventListener("click", () => showTab(b.dataset.tab)));
$("viewerClose").addEventListener("click", () => $("viewer").close());

checkHealth();
refresh();
