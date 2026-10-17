"use strict";

const size = (b) => b < 1024 ? `${b} B` : b < 1048576 ? `${(b / 1024).toFixed(1)} KB` : `${(b / 1048576).toFixed(1)} MB`;
const location_ = (h) => h.startLine != null ? `lines ${h.startLine}–${h.endLine}` : h.page != null ? `page ${h.page}` : "";

let docs = [];
let pollTimer = null;

// ---------- collections ----------
const TARGET_KEY = "ragamuffin.uploadCollection";

// A collection passed back from the collections page (?collection=name) wins over the remembered one.
const requestedTarget = new URLSearchParams(location.search).get("collection");
if (requestedTarget) {
  storageSet(TARGET_KEY, requestedTarget);
  history.replaceState(null, "", location.pathname);
}

async function loadCollections() {
  const cols = await api("/api/collections");
  const options = (withCounts) => cols.map((c) =>
    `<option value="${esc(c.name)}">${esc(collectionLabel(c.name))}${withCounts ? ` (${c.documents})` : ""}</option>`).join("");

  const target = $("collection");
  const wanted = target.value || storageGet(TARGET_KEY) || DEFAULT_COLLECTION;
  target.innerHTML = options(false);
  target.value = cols.some((c) => c.name === wanted) ? wanted : DEFAULT_COLLECTION;
  updateDropTarget();

  for (const id of ["filterCollection", "searchCollection"]) {
    const sel = $(id);
    const current = sel.value;
    sel.innerHTML = `<option value="">All collections</option>` + options(true);
    sel.value = cols.some((c) => c.name === current) ? current : "";
  }
}

function updateDropTarget() {
  $("dropTarget").textContent = collectionLabel($("collection").value || DEFAULT_COLLECTION);
}

$("collection").addEventListener("change", () => {
  storageSet(TARGET_KEY, $("collection").value);
  updateDropTarget();
});

// ---------- documents ----------
let ocrStatus = { available: false, model: "", reason: "checking…" };
let layoutStatus = { available: false, reason: "checking…" };
const selected = new Set();
const busy = (d) => d.status === "pending" || d.status === "ocr";
const textPages = (d) => Math.max(0, d.pageCount - d.emptyPages);

async function loadOcrStatus() {
  const [o, l] = await Promise.all([
    api("/api/ocr").catch(() => ({ available: false, model: "", reason: "Server unreachable" })),
    api("/api/layout").catch(() => ({ available: false, reason: "Server unreachable" })),
  ]);
  ocrStatus = o;
  layoutStatus = l;
}

/** What an analysis job is doing, for the status column. */
function jobLabel(d) {
  if (d.ocrScope && d.layoutMode) return "Analyzing";
  if (d.layoutMode) return "Layout";
  return "OCR";
}

async function loadDocs() {
  docs = await api("/api/documents");
  renderDocs();
  clearTimeout(pollTimer);
  if (docs.some(busy)) pollTimer = setTimeout(refresh, 2000);
}

function statusCell(d) {
  const notice = d.error ? ` <span class="warn" title="${esc(d.error)}">⚠</span>` : "";
  switch (d.status) {
    case "pending":
      return `<span class="status-pending">indexing…</span>`;
    case "ocr": {
      if (d.progressTotal == null) return `<span class="status-ocr">${jobLabel(d)} queued…</span>`;
      const pct = d.progressTotal ? Math.round(100 * d.progressDone / d.progressTotal) : 0;
      return `<span class="status-ocr">${jobLabel(d)} ${d.progressDone}/${d.progressTotal}</span>
        <span class="progress" title="${pct}%"><span style="width:${pct}%"></span></span>`;
    }
    case "needs_ocr":
      return `<span class="status-needs_ocr" title="${esc(d.error)}">needs OCR</span>`;
    case "failed":
      return `<span class="status-failed" title="${esc(d.error)}">failed ⚠</span>`;
    default: {
      let badge = "";
      if (d.ocrPages > 0) {
        const all = d.ocrPages === d.pageCount;
        const tip = `Text from local OCR · ${d.ocrModel ?? "unknown model"} · ${d.ocrPages} of ${d.pageCount} pages` +
          (d.ocrAt ? ` · ${when(d.ocrAt)}` : "");
        badge = ` <span class="badge ocr" title="${esc(tip)}">OCR${all ? "" : ` ${d.ocrPages}/${d.pageCount}`}</span>`;
      }
      if (d.layoutPages > 0) {
        const all = d.layoutPages >= textPages(d);
        const tip = `Layout analyzed: ${d.layoutPages} of ${textPages(d)} pages with text (reading order, tables, headers and footers removed)`;
        badge += ` <span class="badge layout" title="${esc(tip)}">Layout${all ? "" : ` ${d.layoutPages}/${textPages(d)}`}</span>`;
      }
      const empty = d.contentType === "pdf" && d.emptyPages > 0
        ? ` <span class="badge muted-badge" title="${d.emptyPages} page${d.emptyPages > 1 ? "s have" : " has"} no text; use Re-index… to OCR them">${d.emptyPages} blank</span>`
        : "";
      return `<span class="status-ready">ready</span>${badge}${empty}${notice}`;
    }
  }
}

// The PDF dialog needs page statistics; a PDF that has never been analyzed page by page (e.g. one that failed
// before OCR existed) just gets a plain re-index, which analyzes it.
const usesReindexDialog = (d) => d.contentType === "pdf" && d.pageCount > 0;

function actionButtons(d) {
  const del = `<button class="small secondary danger" data-delete="${d.id}">Delete</button>`;
  if (d.status === "ocr")
    return `<button class="small secondary" data-cancel-ocr="${d.id}">Cancel OCR</button>${del}`;
  if (d.status === "needs_ocr") {
    const tip = ocrStatus.available ? `Read the scanned pages with ${ocrStatus.model}` : ocrStatus.reason;
    return `<button class="small" data-run-ocr="${d.id}" title="${esc(tip)}" ${ocrStatus.available ? "" : "disabled"}>Run OCR</button>${del}`;
  }
  const label = usesReindexDialog(d) ? "Re-index…" : "Re-index";
  return `<button class="small secondary" data-reindex="${d.id}" ${d.status === "pending" ? "disabled" : ""}>${label}</button>${del}`;
}

// ---------- sorting ----------
// Click a column header to sort ascending, again for descending. The default (no sort) is the server's order:
// collection, then name. The choice is remembered per browser.
const SORT_KEY = "ragamuffin.docSort";
let sort = (() => { try { return JSON.parse(storageGet(SORT_KEY)) ?? null; } catch { return null; } })();

// Status order runs from "needs attention" to "done".
const STATUS_RANK = { failed: 0, needs_ocr: 1, ocr: 2, pending: 3, ready: 4 };
const SORT_VALUES = {
  name: (d) => d.name.toLowerCase(),
  collection: (d) => collectionLabel(d.collection).toLowerCase(),
  type: (d) => (d.language ?? d.contentType).toLowerCase(),
  size: (d) => d.sizeBytes,
  chunks: (d) => d.chunkCount,
  status: (d) => STATUS_RANK[d.status] ?? 9,
  updated: (d) => d.updatedAt,
};

function sorted(rows) {
  if (!sort || !SORT_VALUES[sort.column]) return rows;
  const value = SORT_VALUES[sort.column];
  const dir = sort.dir === "desc" ? -1 : 1;
  // Ties keep the default order, so equal values stay grouped by collection and name.
  return rows
    .map((d, i) => ({ d, i, v: value(d) }))
    .sort((a, b) => (a.v < b.v ? -dir : a.v > b.v ? dir : a.i - b.i))
    .map((x) => x.d);
}

function setSort(next) {
  sort = next;
  storageSet(SORT_KEY, JSON.stringify(sort));
  renderDocs();
}

function updateSortHeaders() {
  document.querySelectorAll("th.sortable").forEach((th) => {
    const active = sort?.column === th.dataset.sort;
    th.classList.toggle("sorted", active);
    th.dataset.dir = active ? sort.dir : "";
    th.setAttribute("aria-sort", active ? (sort.dir === "desc" ? "descending" : "ascending") : "none");
    th.title = active ? `Sorted ${sort.dir === "desc" ? "descending" : "ascending"}; click to reverse` : "Click to sort";
  });
  $("clearSort").hidden = !sort;
}

document.querySelectorAll("th.sortable").forEach((th) => th.addEventListener("click", () => {
  const column = th.dataset.sort;
  setSort({ column, dir: sort?.column === column && sort.dir === "asc" ? "desc" : "asc" });
}));
$("clearSort").addEventListener("click", () => setSort(null));

function renderDocs() {
  const col = $("filterCollection").value;
  const name = $("filterName").value.toLowerCase();
  const rows = sorted(docs.filter((d) => (!col || d.collection === col) && (!name || d.name.toLowerCase().includes(name))));
  updateSortHeaders();
  $("docCount").textContent = `(${rows.length}${rows.length !== docs.length ? ` of ${docs.length}` : ""})`;
  $("docs").innerHTML = rows.length === 0
    ? `<tr><td colspan="9" class="muted">No documents yet — drop some files above.</td></tr>`
    : rows.map((d) => `
      <tr>
        <td class="check">${d.contentType === "pdf" ? `<input type="checkbox" class="pick" data-id="${d.id}" ${selected.has(d.id) ? "checked" : ""}>` : ""}</td>
        <td class="name"><a data-view="${d.id}">${esc(d.name)}</a></td>
        <td>${esc(collectionLabel(d.collection))}</td>
        <td>${esc(d.language ?? d.contentType)}</td>
        <td class="num">${size(d.sizeBytes)}</td>
        <td class="num">${d.chunkCount}</td>
        <td class="status">${statusCell(d)}</td>
        <td>${when(d.updatedAt)}</td>
        <td><div class="row">${actionButtons(d)}</div></td>
      </tr>`).join("");
}

$("docs").addEventListener("click", async (e) => {
  const t = e.target.closest("[data-view],[data-reindex],[data-delete],[data-run-ocr],[data-cancel-ocr]");
  if (!t) return;
  try {
    if (t.dataset.view) return openViewer(t.dataset.view);
    if (t.dataset.reindex) {
      const d = docs.find((x) => x.id === t.dataset.reindex);
      if (usesReindexDialog(d)) return openReindexDialog(d.id);
      t.disabled = true;
      await api(`/api/documents/${d.id}/reindex`, { method: "POST" });
      return refresh();
    }
    if (t.dataset.runOcr) {
      t.disabled = true;
      await postJson(`/api/documents/${t.dataset.runOcr}/ocr`, { scope: "missing" });
      return refresh();
    }
    if (t.dataset.cancelOcr) {
      t.disabled = true;
      await api(`/api/documents/${t.dataset.cancelOcr}/ocr/cancel`, { method: "POST" });
      return refresh();
    }
    if (t.dataset.delete) {
      const d = docs.find((x) => x.id === t.dataset.delete);
      if (!confirm(`Delete ${d.name} from ${collectionLabel(d.collection)}?`)) return;
      await api(`/api/documents/${d.id}`, { method: "DELETE" });
      return refresh();
    }
  } catch (err) {
    alert(err.message);
    refresh();
  }
});
$("filterCollection").addEventListener("change", renderDocs);
$("filterName").addEventListener("input", renderDocs);

// Row selection (PDFs only) for batch processing.
$("docs").addEventListener("change", (e) => {
  if (!e.target.matches("input.pick")) return;
  if (e.target.checked) selected.add(e.target.dataset.id); else selected.delete(e.target.dataset.id);
});
$("selectAllDocs").addEventListener("change", (e) => {
  document.querySelectorAll("#docs input.pick").forEach((c) => {
    c.checked = e.target.checked;
    if (c.checked) selected.add(c.dataset.id); else selected.delete(c.dataset.id);
  });
});

async function refresh() {
  await loadOcrStatus();
  await Promise.all([loadDocs(), loadCollections(), loadQueue()]);
}

// ---------- processing queue ----------
async function loadQueue() {
  const q = await api("/api/queue").catch(() => null);
  const panel = $("queuePanel");
  if (!q || (!q.running && q.queued.length === 0)) { panel.hidden = true; return; }
  const parts = [];
  if (q.running) parts.push(`now: ${esc(q.running.name.split("/").pop())} (${jobLabel(q.running)} ${q.running.progressDone ?? 0}/${q.running.progressTotal})`);
  if (q.queued.length) parts.push(`${q.queued.length} queued (about ${q.queuedPages.toLocaleString()} page${q.queuedPages === 1 ? "" : "s"})`);
  $("queueText").innerHTML = "· " + parts.join(" · ");
  panel.hidden = false;
}

$("queueCancel").addEventListener("click", async () => {
  if (!confirm("Cancel the running job and everything queued? Finished pages are kept.")) return;
  await api("/api/queue/cancel", { method: "POST" });
  refresh();
});

// ---------- batch processing ----------
function batchRequest(dryRun) {
  const scope = document.querySelector('input[name="batchScope"]:checked').value;
  return {
    documentIds: scope === "selected" ? [...selected] : null,
    collection: scope === "collection" ? $("filterCollection").value || null : null,
    ocr: $("batchOcr").checked,
    layout: $("batchLayout").checked,
    redoLayout: $("batchLayout").checked && $("batchRedo").checked,
    dryRun,
  };
}

async function previewBatch() {
  const out = $("batchPreview");
  $("batchRedo").disabled = !$("batchLayout").checked;
  if (!$("batchOcr").checked && !$("batchLayout").checked) {
    out.className = "message bad";
    out.textContent = "Choose OCR, layout analysis or both.";
    $("batchQueue").disabled = true;
    return;
  }
  out.className = "message";
  out.textContent = "Counting…";
  try {
    const r = await postJson("/api/batch", batchRequest(true));
    const hours = r.totalPages * 45 / 3600;
    const lines = [r.documents.length
      ? `${r.documents.length} document${r.documents.length > 1 ? "s" : ""}, about ${r.totalPages.toLocaleString()} pages — roughly ${hours < 1 ? Math.max(1, Math.round(hours * 60)) + " minutes" : hours.toFixed(1) + " hours"}.`
      : "Nothing to do: no matching documents need this."];
    if (r.skippedBusy) lines.push(`${r.skippedBusy} already being processed (skipped).`);
    if (r.ocrUnavailable) lines.push(`OCR unavailable: ${ocrStatus.reason}`);
    if (r.layoutUnavailable) lines.push(`Layout analysis unavailable: ${layoutStatus.reason}`);
    out.innerHTML = lines.map(esc).join("<br>");
    out.className = r.ocrUnavailable || r.layoutUnavailable ? "message bad" : "message";
    $("batchQueue").disabled = r.documents.length === 0;
  } catch (err) {
    out.className = "message bad";
    out.textContent = err.message;
    $("batchQueue").disabled = true;
  }
}

$("processBtn").addEventListener("click", async () => {
  await loadOcrStatus();
  const col = $("filterCollection").value;
  $("batchSelectedLabel").textContent = `Selected documents (${selected.size})`;
  $("batchCollectionLabel").textContent = col ? `All PDFs in ${collectionLabel(col)}` : "All PDFs in every collection";
  const sel = document.querySelector('input[name="batchScope"][value="selected"]');
  sel.disabled = selected.size === 0;
  document.querySelector(`input[name="batchScope"][value="${selected.size ? "selected" : "collection"}"]`).checked = true;
  $("batchDialog").returnValue = "";
  $("batchDialog").showModal();
  previewBatch();
});

$("batchForm").addEventListener("change", previewBatch);

$("batchDialog").addEventListener("close", async () => {
  if ($("batchDialog").returnValue !== "confirm") return;
  try {
    const r = await postJson("/api/batch", batchRequest(false));
    selected.clear();
    $("selectAllDocs").checked = false;
    alert(`Queued ${r.queued} document${r.queued === 1 ? "" : "s"} (about ${r.totalPages.toLocaleString()} pages).`);
  } catch (err) {
    alert(err.message);
  }
  refresh();
});

// ---------- PDF re-index dialog ----------
let reindexDoc = null;

async function openReindexDialog(id) {
  await loadOcrStatus();
  const { document: d, revertablePages } = await api(`/api/documents/${id}`);
  reindexDoc = d;
  $("reindexTitle").textContent = d.name;

  const hasOcr = d.ocrPages > 0;
  $("reindexKeepLabel").textContent = hasOcr || d.layoutPages ? "Re-index, keeping OCR and layout results" : "Re-index from the PDF's embedded text";
  $("reindexKeepHint").textContent = hasOcr
    ? `Re-reads the embedded text. The ${d.ocrPages} OCR'd page${d.ocrPages > 1 ? "s keep" : " keeps"} their OCR text.`
    : "Re-reads the text stored in the PDF.";

  const ocrRadio = $("reindexForm").querySelector('input[value="ocr"]');
  ocrRadio.disabled = !ocrStatus.available;
  $("ocrScope").disabled = !ocrStatus.available;
  $("reindexOcrLabel").textContent = `Replace text with local OCR (${ocrStatus.model || "OCR model"})`;
  const suspectTotal = d.emptyPages + d.suspectPages;
  $("ocrScope").innerHTML = [
    `<option value="missing" ${d.emptyPages ? "" : "disabled"}>Pages without text (${d.emptyPages})</option>`,
    `<option value="suspect" ${suspectTotal ? "" : "disabled"}>Pages without text or that look like poor OCR (${suspectTotal})</option>`,
    `<option value="all">All pages (${d.pageCount})</option>`,
  ].join("");
  $("ocrScope").value = d.emptyPages ? "missing" : suspectTotal ? "suspect" : "all";
  const modelNote = hasOcr && d.ocrModel && ocrStatus.model && d.ocrModel !== ocrStatus.model
    ? ` Currently OCR'd with ${d.ocrModel}; this run uses ${ocrStatus.model}.` : "";
  $("reindexOcrHint").textContent = ocrStatus.available
    ? `Roughly 5–20 seconds per page on the Ollama server. Embedded text is kept and can be restored.${modelNote}`
    : `Unavailable: ${ocrStatus.reason}`;
  updateOcrEstimate();

  const layoutRadio = $("reindexForm").querySelector('input[value="layout"]');
  layoutRadio.disabled = !layoutStatus.available || textPages(d) === 0;
  $("layoutRedo").disabled = layoutRadio.disabled;
  $("layoutRedo").checked = false;
  $("reindexLayoutHint").textContent = !layoutStatus.available
    ? `Unavailable: ${layoutStatus.reason}`
    : textPages(d) === 0
      ? "No pages with embedded text."
      : `Arranges the PDF's own text by layout: reading order, real tables, headers and footers removed. ` +
        `${d.layoutPages} of ${textPages(d)} pages with text analyzed so far. Roughly 30–60 seconds per page.`;

  const revert = $("reindexRevertRow");
  revert.hidden = revertablePages === 0;
  $("reindexRevertHint").textContent =
    `${revertablePages} OCR'd page${revertablePages > 1 ? "s have" : " has"} embedded text to go back to. Pages without embedded text keep their OCR.`;

  $("reindexForm").querySelector('input[value="keep"]').checked = true;
  $("reindexDialog").returnValue = "";
  $("reindexDialog").showModal();
}

function updateOcrEstimate() {
  if (!reindexDoc) return;
  const scope = $("ocrScope").value;
  const n = scope === "missing" ? reindexDoc.emptyPages
    : scope === "suspect" ? reindexDoc.emptyPages + reindexDoc.suspectPages
    : reindexDoc.pageCount;
  const mins = Math.max(1, Math.round(n * 12 / 60));
  $("ocrEstimate").textContent = n ? `${n} page${n > 1 ? "s" : ""}, about ${mins} minute${mins > 1 ? "s" : ""}` : "";
}

$("ocrScope").addEventListener("change", () => {
  $("reindexForm").querySelector('input[value="ocr"]').checked = true;
  updateOcrEstimate();
});

$("reindexDialog").addEventListener("close", async () => {
  if ($("reindexDialog").returnValue !== "confirm" || !reindexDoc) return;
  const choice = $("reindexForm").querySelector('input[name="reindexChoice"]:checked').value;
  const id = reindexDoc.id;
  try {
    if (choice === "keep") await api(`/api/documents/${id}/reindex`, { method: "POST" });
    else if (choice === "ocr") await postJson(`/api/documents/${id}/ocr`, { scope: $("ocrScope").value });
    else if (choice === "revert") await api(`/api/documents/${id}/revert-ocr`, { method: "POST" });
    else if (choice === "layout") await postJson(`/api/documents/${id}/layout`, { redo: $("layoutRedo").checked });
  } catch (err) {
    alert(err.message);
  }
  refresh();
});

// ---------- upload ----------
async function upload(files) {
  files = files.filter((f) => !f.name.startsWith("."));
  if (files.length === 0) return;
  const collection = $("collection").value || DEFAULT_COLLECTION;
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
        li.textContent = `${r.outcome}: ${collectionLabel(r.collection)} / ${r.name}${r.error ? " — " + r.error : ""}`;
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
          <strong>[${i + 1}] ${esc(collectionLabel(h.collection))} / ${esc(h.documentName)} <span class="muted">${location_(h)}</span>${h.ocr ? ` <span class="badge ocr" title="Read by ${esc(h.ocrModel)}">OCR</span>` : ""}</strong>
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
  const [{ document: doc }, chunks] = await Promise.all([api(`/api/documents/${id}`), api(`/api/documents/${id}/chunks`)]);
  viewerDoc = doc;
  viewerChunks = chunks;
  $("viewerTitle").textContent = `${collectionLabel(doc.collection)} / ${doc.name}`;
  $("viewerFile").href = `/api/documents/${id}/file`;
  showTab("chunks");
  $("viewer").showModal();
}

function showTab(tab) {
  document.querySelectorAll(".tab").forEach((b) => b.classList.toggle("active", b.dataset.tab === tab));
  const body = $("viewerBody");
  if (viewerDoc.status !== "ready" && !viewerChunks.length) {
    body.innerHTML = `<p class="status-${viewerDoc.status}">${esc(viewerDoc.status)}${viewerDoc.error ? ": " + esc(viewerDoc.error) : ""}</p>`;
    return;
  }
  body.innerHTML = tab === "text"
    ? `<pre>${esc(viewerDoc.text)}</pre>`
    : viewerChunks.map((c) => `
      <div class="hit">
        <div class="hit-head"><strong>#${c.ordinal + 1} <span class="muted">${location_(c)}</span>${c.ocr ? ` <span class="badge ocr">OCR</span>` : ""}</strong><span class="muted">${c.content.length} chars</span></div>
        ${c.headingPath ? `<div class="muted">${esc(c.headingPath)}</div>` : ""}
        <pre>${esc(c.content)}</pre>
      </div>`).join("");
}

document.querySelectorAll(".tab").forEach((b) => b.addEventListener("click", () => showTab(b.dataset.tab)));
$("viewerClose").addEventListener("click", () => $("viewer").close());

checkHealth();
refresh();
