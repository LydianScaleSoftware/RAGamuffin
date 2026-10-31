"use strict";

const size = (b) => b < 1024 ? `${b} B` : b < 1048576 ? `${(b / 1024).toFixed(1)} KB` : `${(b / 1048576).toFixed(1)} MB`;
const pages_ = (c) => c.page == null ? "" : c.pageEnd != null && c.pageEnd !== c.page ? `pages ${c.page}–${c.pageEnd}` : `page ${c.page}`;
const location_ = (h) => h.startLine != null
  ? `lines ${h.startLine}–${h.endLine}${h.page != null ? ` · from ${pages_(h)}` : ""}`
  : pages_(h);

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
    const current = id === "filterCollection" && pendingCollectionFilter !== null ? pendingCollectionFilter : sel.value;
    sel.innerHTML = `<option value="">All collections</option>` + options(true);
    sel.value = cols.some((c) => c.name === current) ? current : "";
  }
  if (pendingCollectionFilter !== null) {
    pendingCollectionFilter = null;
    saveFilters();   // also drops a remembered collection that no longer exists
    renderDocs();
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
// Claude Code jobs (see the Claude jobs section below).
let claudeStatus = { available: false };
let claudeJobs = [];
let claudeTimer = null;
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

/** Notes, muted items or a notice from the last run: an ⓘ that opens the health dialog, where they can be muted or unmuted. */
function infoButton(d) {
  const items = [];
  if (d.error) items.push(d.noticeMuted ? "a muted notice" : "a notice from the last run");
  if (d.notePages) items.push(`${d.notePages} page note${d.notePages > 1 ? "s" : ""}`);
  if (d.mutedPages) items.push(`${d.mutedPages} muted page${d.mutedPages > 1 ? "s" : ""}`);
  return items.length
    ? ` <button type="button" class="info-btn" data-health="${d.id}" title="${esc(items.join(", "))}: click for details" aria-label="Details">ⓘ</button>`
    : "";
}

/** A ready PDF's page health: problems, verified, or no problems found. Opens the health dialog. */
function healthBadge(d) {
  if (d.contentType !== "pdf") return "";
  const textPages = d.pageCount - d.blankPages;
  const [cls, label, tip] = d.problemPages > 0
    ? ["health-bad", `⚠ ${d.problemPages} problem${d.problemPages > 1 ? "s" : ""}`, "Pages whose text is probably wrong or missing"]
    : d.verifiedAt || (textPages > 0 && d.verifiedPages >= textPages)
      ? ["health-ok", "✓ Verified", d.verifiedAt ? `Verified: ${d.verifiedNote ?? ""}` : "Every page checked"]
      : ["muted-badge", "No problems", "Nothing points to a fault; the text hasn't been checked against the page images"];
  return ` <button type="button" class="badge ${cls}" data-health="${d.id}" title="${esc(tip)}; click for page health">${label}</button>`;
}

function statusCell(d) {
  const notice = infoButton(d);
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
      if (d.claudePages > 0) {
        const all = d.claudePages === d.pageCount;
        badge += ` <span class="badge claude" title="Text checked or transcribed by Claude from the page images: ${d.claudePages} of ${d.pageCount} pages">Claude${all ? "" : ` ${d.claudePages}/${d.pageCount}`}</span>`;
      }
      if (d.chunkSource === "claude")
        badge += ` <a class="badge claude" href="/claudomatics.html?q=${encodeURIComponent(d.name)}" title="Claude chose the chunk boundaries (sections, tables, listings or whole functions kept together). Open them on the Claudomatics page.">Claudomatics</a>`;
      if (d.encoding)
        badge += ` <span class="badge encoded" title="A binary file stored as text (${esc(d.encoding)} dump: offset, bytes and ASCII per line). The original bytes are kept; Inspect → Original downloads them.">⬢ Encoded · ${esc(d.encoding)}</span>`;
      if (d.sourceName)
        badge += ` <button type="button" class="badge muted-badge" data-source-details="${d.id}" title="Converted from another document; click for details">Source…</button>`;
      if (d.convertedTo?.length)
        badge += ` <button type="button" class="badge muted-badge" data-conversion-details="${d.id}" title="Some pages are searched through a conversion; click for details">Conversion${d.convertedTo.length > 1 ? "s" : ""}…</button>`;
      const job = claudeJobFor(d.id);
      if (job) badge = ` <span class="badge claude-busy" title="${esc(job.label)}: ${esc(job.last ?? job.status)}">Claude ${job.status === "queued" ? "queued" : "working…"}</span>` + badge;
      const flair = healthBadge(d) + badge;
      return `<span class="status-ready">ready</span>${flair ? ` <span class="badges">${flair}</span>` : ""}${notice}`;
    }
  }
}

// The PDF dialog needs page statistics; a PDF that has never been analyzed page by page (e.g. one that failed
// before OCR existed) just gets a plain re-index, which analyzes it.
const usesReindexDialog = (d) => d.contentType === "pdf" && d.pageCount > 0;

// Icons for the row action buttons.
const actionIcons = {
  reindex: `<ellipse cx="7" cy="3.5" rx="5" ry="2"/><path d="M2 3.5v8c0 1.1 2.2 2 5 2M12 3.5V7M2 7.5c0 1.1 2.2 2 5 2"/><path d="M14.5 10.5a2.8 2.8 0 1 1-.8-2l.8.8M14.5 7.5v1.8h-1.8"/>`,
  claude: Array.from({ length: 12 }, (_, i) => {
    const a = (i * 30 + 8) * Math.PI / 180, r = [6.5, 5, 6, 4.8][i % 4];
    return `<path d="M8 8L${(8 + r * Math.cos(a)).toFixed(2)} ${(8 + r * Math.sin(a)).toFixed(2)}"/>`;
  }).join(""),
  delete: `<path d="M2.5 4h11M6.5 4V2.5h3V4M4 4l.7 9.5h6.6L12 4M6.8 6.5v4.5M9.2 6.5v4.5"/>`,
};
const iconButton = (kind, attrs, title, extra = "") =>
  `<button class="small secondary icon-button ${kind}-button" ${attrs} title="${esc(title)}" aria-label="${esc(title)}" ${extra}><svg viewBox="0 0 16 16" aria-hidden="true">${actionIcons[kind]}</svg></button>`;

function actionButtons(d) {
  const del = iconButton("delete", `data-delete="${d.id}"`, "Delete");
  if (d.status === "ocr")
    return `<button class="small secondary" data-cancel-ocr="${d.id}">Cancel OCR</button>${del}`;
  if (d.status === "needs_ocr") {
    const tip = ocrStatus.available ? `Read the scanned pages with ${ocrStatus.model}` : ocrStatus.reason;
    return `<button class="small" data-run-ocr="${d.id}" title="${esc(tip)}" ${ocrStatus.available ? "" : "disabled"}>Run OCR</button>${del}`;
  }
  const label = usesReindexDialog(d) ? "Re-index…" : "Re-index";
  const claude = claudeStatus.available && d.status === "ready"
    ? iconButton("claude", `data-claude="${d.id}"`, "Claude… (have Claude Code check or correct this document)") : "";
  return `${iconButton("reindex", `data-reindex="${d.id}"`, label, d.status === "pending" ? "disabled" : "")}${claude}${del}`;
}

// ---------- drill-down filters (from the Stats page) ----------
// /?fc=<collection>&filter=<kind>:<value>&q=<name>. "fc" pre-selects the collection filter; "filter" narrows further.
// The collection, name and drill-down filters are remembered per browser (like the sort), so leaving the page and coming
// back keeps them. A link from the Stats page replaces them.
const FILTERS_KEY = "ragamuffin.docFilters";
const pageParams = new URLSearchParams(location.search);
const fromLink = ["fc", "filter", "q"].some((k) => pageParams.has(k));
const savedFilters = (() => { try { return JSON.parse(storageGet(FILTERS_KEY)) ?? {}; } catch { return {}; } })();
let pendingCollectionFilter = fromLink ? pageParams.get("fc") ?? "" : savedFilters.fc ?? null;
let drillFilter = fromLink ? pageParams.get("filter") : savedFilters.filter ?? null;
$("filterName").value = (fromLink ? pageParams.get("q") : savedFilters.q) ?? "";
if (fromLink) history.replaceState(null, "", location.pathname);

function saveFilters() {
  const fc = pendingCollectionFilter ?? $("filterCollection").value;
  const q = $("filterName").value;
  storageSet(FILTERS_KEY, JSON.stringify({ fc, q, filter: drillFilter }));
  $("resetFilters").hidden = !fc && !q && !drillFilter;
}

const ORIGIN_LABELS = {
  OriginalText: "original text files", PdfEmbedded: "PDFs using embedded text", PdfEmbeddedLayout: "PDFs with layout-arranged text",
  PdfOcr: "PDFs read entirely by OCR", PdfClaude: "PDFs whose text was all checked by Claude",
  PdfMixed: "PDFs mixing embedded, OCR and Claude-checked text", PdfNoText: "PDFs with no text yet", EncodedBinary: "encoded binary files",
};
const STATUS_LABELS = { ready: "ready", needs_ocr: "needing OCR", failed: "failed", busy: "being processed" };

/** [predicate, description] for a drill-down filter like "type:pdf", or null when there's none. */
function drillPredicate(filter) {
  if (!filter) return null;
  const [kind, value] = filter.includes(":") ? filter.split(/:(.*)/s) : [filter, ""];
  const pdf = (d) => d.contentType === "pdf";
  switch (kind) {
    case "type": return [(d) => d.contentType === value, `${value} documents`];
    case "language": return [(d) => d.contentType === "code" && (d.language ?? "other") === value, `${value} code`];
    case "origin": return [(d) => d.origin === value, ORIGIN_LABELS[value] ?? value];
    case "status": return [value === "busy" ? busy : (d) => d.status === value, `documents ${STATUS_LABELS[value] ?? value}`];
    case "notices": return [(d) => d.error && !d.noticeMuted && d.status !== "failed" && d.status !== "needs_ocr", "documents with notices"];
    case "added": return [(d) => Date.now() - new Date(d.createdAt) < Number(value || 7) * 864e5, `added in the last ${value || 7} days`];
    case "pages":
      switch (value) {
        case "needs-ocr": return [(d) => pdf(d) && (d.emptyPages > 0 || d.origin === "PdfNoText"), "PDFs with pages needing OCR"];
        case "no-layout": return [(d) => pdf(d) && d.rawEmbeddedPages > 0, "PDFs with pages not yet laid out"];
        case "layout": return [(d) => pdf(d) && d.layoutPages > 0, "PDFs with layout-analyzed pages"];
        case "ocr": return [(d) => pdf(d) && d.ocrPages > 0, "PDFs with OCR'd pages"];
        case "suspect": return [(d) => pdf(d) && d.suspectPages > 0, "PDFs with pages that look like poor old OCR"];
        case "problems": return [(d) => pdf(d) && d.status === "ready" && d.problemPages > 0, "PDFs with problem pages"];
        case "claude": return [(d) => pdf(d) && d.claudePages > 0, "PDFs with pages checked by Claude"];
      }
  }
  return [() => true, `unknown filter "${filter}"`];
}

function updateFilterBanner() {
  const p = drillPredicate(drillFilter);
  $("filterBanner").hidden = !p;
  if (p) $("filterText").textContent = p[1];
}

$("clearFilter").addEventListener("click", () => {
  drillFilter = null;
  history.replaceState(null, "", location.pathname);
  saveFilters();
  renderDocs();
});

$("resetFilters").addEventListener("click", () => {
  drillFilter = null;
  $("filterCollection").value = "";
  $("filterName").value = "";
  saveFilters();
  renderDocs();
});

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

// A small icon for a document's type, with the type (and language) in its tooltip.
const typeIcons = {
  pdf: `<path d="M4 1.5h5.5L13 5v9.5H4z"/><path d="M9.5 1.5V5H13"/><text x="8.5" y="12.2" text-anchor="middle">PDF</text>`,
  markdown: `<rect x="1" y="3.5" width="14" height="9" rx="1.5"/><path d="M3.5 10.5v-5l2 2.5 2-2.5v5M11 5.5v5m-1.8-1.8L11 10.5l1.8-1.8"/>`,
  text: `<path d="M3.5 1.5h9v13h-9z"/><path d="M5.5 5h5M5.5 7.5h5M5.5 10h3.5"/>`,
  code: `<path d="M5.5 4 2 8l3.5 4M10.5 4 14 8l-3.5 4M9 3 7 13"/>`,
  other: `<path d="M3.5 1.5h6L12.5 4.5v10h-9z"/><path d="M9.5 1.5v3h3"/><circle cx="8" cy="10" r=".6"/>`,
};
function typeBadge(d) {
  const kind = typeIcons[d.contentType] ? d.contentType : "other";
  const label = d.contentType === "code" && d.language ? `code (${d.language})` : (d.language ?? d.contentType ?? "unknown");
  return `<span class="type-icon type-${kind}" title="${esc(label)}" aria-label="${esc(label)}" role="img"><svg viewBox="0 0 16 16" aria-hidden="true">${typeIcons[kind]}</svg></span>`;
}

function renderDocs() {
  const col = $("filterCollection").value;
  const name = $("filterName").value.toLowerCase();
  const drill = drillPredicate(drillFilter)?.[0] ?? (() => true);
  const rows = sorted(docs.filter((d) => (!col || d.collection === col) && (!name || d.name.toLowerCase().includes(name)) && drill(d)));
  updateSortHeaders();
  updateFilterBanner();
  $("docCount").textContent = `(${rows.length}${rows.length !== docs.length ? ` of ${docs.length}` : ""})`;
  $("docs").innerHTML = rows.length === 0
    ? `<tr><td colspan="9" class="muted">No documents yet — drop some files above.</td></tr>`
    : rows.map((d) => `
      <tr>
        <td class="check">${d.contentType === "pdf" ? `<input type="checkbox" class="pick" data-id="${d.id}" ${selected.has(d.id) ? "checked" : ""}>` : ""}</td>
        <td class="type">${typeBadge(d)}</td>
        <td class="name"><a data-view="${d.id}">${esc(d.name)}</a></td>
        <td>${esc(collectionLabel(d.collection))}</td>
        <td class="num">${size(d.sizeBytes)}</td>
        <td class="num">${d.chunkCount}</td>
        <td class="status">${statusCell(d)}</td>
        <td>${when(d.updatedAt)}</td>
        <td><div class="row">${actionButtons(d)}</div></td>
      </tr>`).join("");
}

$("docs").addEventListener("click", async (e) => {
  const t = e.target.closest("[data-view],[data-reindex],[data-delete],[data-run-ocr],[data-cancel-ocr],[data-source-details],[data-conversion-details],[data-health],[data-claude]");
  if (!t) return;
  if (t.dataset.claude) return openClaudeDialog(t.dataset.claude);
  if (t.dataset.health) return openHealth(t.dataset.health);
  if (t.dataset.sourceDetails) return showSourceDetails(docs.find((x) => x.id === t.dataset.sourceDetails));
  if (t.dataset.conversionDetails) return showConversionDetails(docs.find((x) => x.id === t.dataset.conversionDetails));
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
$("filterCollection").addEventListener("change", () => { saveFilters(); renderDocs(); });
$("filterName").addEventListener("input", () => { saveFilters(); renderDocs(); });

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
  await Promise.all([loadOcrStatus(), loadClaude()]);
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
    `<option value="problems" ${d.issuePages ? "" : "disabled"}>Pages with problems in the last run (${d.issuePages ?? 0})</option>`,
    `<option value="missing" ${d.emptyPages ? "" : "disabled"}>Pages without text (${d.emptyPages})</option>`,
    `<option value="suspect" ${suspectTotal ? "" : "disabled"}>Pages without text or that look like poor OCR (${suspectTotal})</option>`,
    `<option value="all">All pages (${d.pageCount})</option>`,
  ].join("");
  $("ocrScope").value = d.issuePages ? "problems" : d.emptyPages ? "missing" : suspectTotal ? "suspect" : "all";
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
  const n = scope === "problems" ? reindexDoc.issuePages ?? 0
    : scope === "missing" ? reindexDoc.emptyPages
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
  if ($("searchMuted").checked) params.set("includeMuted", "true");
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
        <div class="hit-head"><strong>#${c.ordinal + 1}${c.title ? ` ${esc(c.title)}` : ""} <span class="muted">${location_(c)}</span>${c.ocr ? ` <span class="badge ocr">OCR</span>` : ""}${c.kind ? ` <span class="badge muted-badge">${esc(c.kind)}${c.symbol ? ` ${esc(c.symbol)}` : ""}</span>` : ""}</strong><span class="muted">${c.content.length.toLocaleString()} chars</span></div>
        ${c.headingPath ? `<div class="muted">${esc(c.headingPath)}</div>` : ""}
        ${c.summary ? `<div class="chunk-summary">${esc(c.summary)}</div>` : ""}
        <pre>${esc(c.content)}</pre>
      </div>`).join("");
}

document.querySelectorAll(".tab").forEach((b) => b.addEventListener("click", () => showTab(b.dataset.tab)));
$("viewerClose").addEventListener("click", () => $("viewer").close());

checkHealth();
refresh();

// ---------- page health dialog ----------

let healthDocId = null;
const pageList = (pages) => pages.map((p) => p.page).join(", ");

async function openHealth(id) {
  healthDocId = id;
  const h = await api(`/api/documents/${id}/health`);
  const by = (state) => h.pages.filter((p) => p.state === state);
  const problems = by("problem"), notes = by("note"), muted = by("muted"), blank = by("blank");
  const view = (n) => `<a href="/api/documents/${id}/pages/${n}/image" target="_blank" rel="noopener" title="Open the page image">p.${n}</a>`;
  const row = (p, actions) => `<li>${view(p.page)} — ${esc(p.text ?? "")} <span class="health-actions">${actions}</span></li>`;
  const button = (action, pages, label, title) =>
    `<button type="button" class="secondary small" data-health-action="${action}" data-pages="${pages}" title="${esc(title)}">${label}</button>`;
  const section = (title, items, render, bulk = "") => items.length
    ? `<h3>${title} (${items.length}) ${bulk}</h3><ul class="health-list">${items.map(render).join("")}</ul>` : "";
  const verified = h.verified
    ? `<p>✓ <strong>Verified</strong> ${h.verified.by === "user" ? "by you" : "by Claude, from a sample"} on ${when(h.verified.at)}${h.verified.note ? `: ${esc(h.verified.note)}` : ""}.
         ${button("unverify-doc", "", "Clear", "Remove the verification; unchecked pages count as unverified again")}</p>`
    : `<p>${button("verify-doc", "", "Mark document correct", "Count every unchecked page as verified (e.g. after spot-checking it yourself). Cleared automatically if an OCR or layout run changes the text.")}
         ${claudeStatus.available ? button("claude-sample", "", "Check a sample with Claude", "Claude checks about 12 pages against their images and, if they match, verifies the document")
           : `<span class="muted small">Or ask Claude to check a sample of pages and verify it (list_pages which="sample", then verify_document).</span>`}</p>`;
  const job = claudeJobFor(id);
  const claudeLine = job
    ? `<p class="claude-line">Claude: <strong>${esc(job.label)}</strong> — ${esc(job.status)}${job.last ? `: ${esc(job.last)}` : ""}</p>`
    : claudeStatus.available ? `<p>${button("claude-ask", "", "Ask Claude…", "Have Claude Code check or correct pages, or follow your own instructions")}</p>` : "";

  showDetails(`Page health: ${h.name.split("/").pop()}`, `
    <p class="health-summary"><strong>${esc(h.summary.replace(/^./, (c) => c.toUpperCase()))}</strong></p>
    <p class="muted small">${h.pageCount} pages: ${h.problemPages} problem · ${h.notePages} note · ${h.mutedPages} muted ·
      ${h.verifiedPages} verified (${h.claudePages} by Claude, ${h.userVerifiedPages} by you) · ${h.blankPages} blank · ${h.unverifiedPages} unverified.
      ${h.ocrLayerPages ? `${h.ocrLayerPages} pages use an OCR text layer that came with the PDF; that alone isn't a problem.` : ""}</p>
    ${verified}
    ${claudeLine}
    ${h.notice ? `<h3>Notice from the last run${h.noticeMuted ? " (muted)" : ""}</h3>
      <p>${esc(h.notice)} ${button(h.noticeMuted ? "unmute-notice" : "mute-notice", "", h.noticeMuted ? "Unmute" : "Mute", "Muted notices no longer show in the list")}</p>` : ""}
    ${section("Problems", problems,
      (p) => row(p, (claudeStatus.available && !job ? button("claude-problems", p.page, "Fix with Claude", "Claude transcribes this page from its image") : "") +
        button("mute", p.page, "Mute", "Stop counting this as a problem") + button("verify", p.page, "Mark correct", "The text in use is right (it stays marked until the text changes)")),
      (claudeStatus.available && !job ? button("claude-problems", "", "Fix all with Claude", "Claude transcribes every problem page from its image") : "") +
      button("mute", pageList(problems), "Mute all", "Stop counting these as problems"))}
    ${problems.length ? `<p class="muted small">To fix them: Re-index… → replace text with local OCR (${problems.some((p) => p.text?.startsWith("No text")) ? `"Pages without text" for empty pages, ` : ""}"Pages with problems" for the rest), or ${claudeStatus.available ? "Fix with Claude" : `ask Claude to check them (list_pages which="problems")`}.</p>` : ""}
    ${section("Notes", notes, (p) => row(p, button("mute", p.page, "Mute", "Hide this note")), button("mute", pageList(notes), "Mute all", "Hide these notes"))}
    ${section("Muted", muted, (p) => row(p, button("unmute", p.page, "Unmute", "Count it again")), button("unmute", pageList(muted), "Unmute all", "Count these again"))}
    ${blank.length ? `<h3>Blank pages (${blank.length})</h3><p>${blank.map((p) => view(p.page)).join(", ")}</p>` : ""}`);
}

document.addEventListener("click", async (e) => {
  const b = e.target.closest("[data-health-action]");
  if (!b || !healthDocId) return;
  const pages = String(b.dataset.pages ?? "").split(",").map((n) => parseInt(n, 10)).filter((n) => n > 0);
  const id = healthDocId;
  const post = (path, body) => api(path, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
  b.disabled = true;
  try {
    switch (b.dataset.healthAction) {
      case "mute": case "unmute": await post(`/api/documents/${id}/pages/mute`, { pages, muted: b.dataset.healthAction === "mute" }); break;
      case "verify": await post(`/api/documents/${id}/pages/verify`, { pages, verified: true }); break;
      case "verify-doc": case "unverify-doc": await post(`/api/documents/${id}/verify`, { verified: b.dataset.healthAction === "verify-doc" }); break;
      case "mute-notice": case "unmute-notice": await post(`/api/documents/${id}/notice/mute`, { muted: b.dataset.healthAction === "mute-notice" }); break;
      case "claude-problems": await startClaude(id, { task: "problems", pages: pages.join(", ") }); break;
      case "claude-sample": await startClaude(id, { task: "sample" }); break;
      case "claude-ask": $("detailsDialog").close(); return openClaudeDialog(id);
    }
    await openHealth(id);
    refresh();
  } catch (err) {
    alert(err.message);
    b.disabled = false;
  }
});

// ---------- Claude jobs ----------
// Claude Code runs headless on this Mac (claude -p) with only RAGamuffin's tools; the server builds the prompt.


const claudeJobFor = (id) => claudeJobs.find((j) => j.documentId === id && (j.status === "queued" || j.status === "running"));

async function loadClaude() {
  const [status, jobs] = await Promise.all([
    api("/api/claude").catch(() => ({ available: false })),
    api("/api/claude/jobs").catch(() => []),
  ]);
  const wasBusy = new Set(claudeJobs.filter((j) => j.status === "queued" || j.status === "running").map((j) => j.id));
  claudeStatus = status;
  claudeJobs = jobs;
  renderClaudeJobs();
  clearTimeout(claudeTimer);
  const busy = jobs.some((j) => j.status === "queued" || j.status === "running");
  if (busy) claudeTimer = setTimeout(pollClaude, 3000);
  return jobs.some((j) => wasBusy.has(j.id) && !(j.status === "queued" || j.status === "running"));
}

async function pollClaude() {
  const finished = await loadClaude();
  // A finished job changed pages: reload the documents (and an open health dialog) to show it.
  if (finished) {
    await loadDocs();
    if ($("detailsDialog")?.open && healthDocId && $("detailsTitle").textContent.startsWith("Page health")) openHealth(healthDocId);
  } else renderDocs();
}

// The panel lists jobs that haven't been dismissed; dismissed ones are the "older" jobs, shown on request. Collapsing
// and showing older jobs are remembered per browser.
const CLAUDE_PANEL_KEY = "ragamuffin.claudePanel";
const claudePanel = (() => { try { return JSON.parse(storageGet(CLAUDE_PANEL_KEY)) ?? {}; } catch { return {}; } })();
const saveClaudePanel = () => storageSet(CLAUDE_PANEL_KEY, JSON.stringify(claudePanel));
const jobLive = (j) => j.status === "queued" || j.status === "running";

function renderClaudeJobs() {
  const current = claudeJobs.filter((j) => !j.dismissed);
  const older = claudeJobs.filter((j) => j.dismissed);
  const live = current.filter(jobLive).length;
  if (!older.length) claudePanel.older = false;
  $("claudePanel").hidden = claudeJobs.length === 0;
  $("claudePanel").classList.toggle("collapsed", !!claudePanel.collapsed);
  $("claudeToggle").setAttribute("aria-expanded", String(!claudePanel.collapsed));
  $("claudeSummary").textContent = [live && `${live} running or queued`, current.length - live && `${current.length - live} finished`,
    older.length && `${older.length} older`].filter(Boolean).join(" · ");
  $("claudeDismissAll").hidden = current.length - live === 0;
  $("claudeShowOlder").hidden = older.length === 0;
  $("claudeShowOlder").textContent = claudePanel.older ? "Hide older" : `Show older (${older.length})`;
  $("claudeShowOlder").setAttribute("aria-pressed", String(!!claudePanel.older));

  const shown = claudePanel.older ? [...current, ...older] : current;
  if (!shown.length) { $("claudeJobs").innerHTML = `<li class="muted">No current jobs.</li>`; return; }
  $("claudeJobs").innerHTML = shown.map((j) => {
    const running = jobLive(j);
    const last = j.status === "failed" || j.status === "cancelled" ? j.error : running ? j.last : j.result?.split("\n").find((l) => l.trim());
    const dismiss = running ? `<button class="small secondary danger" data-claude-cancel="${j.id}">Cancel</button>`
      : j.dismissed ? `<button class="small secondary" data-claude-restore="${j.id}" title="Move back to the current jobs">Restore</button>`
      : `<button class="job-dismiss" data-claude-dismiss="${j.id}" title="Dismiss: move to the older jobs" aria-label="Dismiss">✕</button>`;
    return `<li class="${j.dismissed ? "older" : ""}">
      <div class="job-head">
        <span class="job-status ${j.status}">${j.status}</span>
        <span class="job-title" title="${esc(j.documentName)} · ${esc(j.label)}"><strong>${esc(j.documentName.split("/").pop())}</strong> · ${esc(j.label)}</span>
        <span class="job-meta muted small">${j.costUsd != null ? `$${j.costUsd.toFixed(2)} · ` : ""}${when(j.finishedAt ?? j.startedAt ?? j.createdAt)}</span>
        <span class="job-buttons"><button class="small secondary" data-claude-details="${j.id}">Details</button>${dismiss}</span>
      </div>
      ${last ? `<div class="job-last" title="${esc(last)}">${esc(last)}</div>` : ""}</li>`;
  }).join("");
}

$("claudeToggle").addEventListener("click", () => {
  claudePanel.collapsed = !claudePanel.collapsed;
  saveClaudePanel();
  renderClaudeJobs();
});

$("claudeShowOlder").addEventListener("click", () => {
  claudePanel.older = !claudePanel.older;
  saveClaudePanel();
  renderClaudeJobs();
});

async function dismissClaudeJobs(ids, dismissed = true) {
  await postJson("/api/claude/jobs/dismiss", { ids, dismissed });
  await loadClaude();
}

$("claudeDismissAll").addEventListener("click", () =>
  dismissClaudeJobs(claudeJobs.filter((j) => !j.dismissed && !jobLive(j)).map((j) => j.id)).catch((err) => alert(err.message)));

$("claudeJobs").addEventListener("click", async (e) => {
  const t = e.target.closest("[data-claude-details],[data-claude-cancel],[data-claude-dismiss],[data-claude-restore]");
  if (!t) return;
  try {
    if (t.dataset.claudeCancel) {
      if (!confirm("Stop this Claude job? Pages it already saved are kept.")) return;
      await api(`/api/claude/jobs/${t.dataset.claudeCancel}/cancel`, { method: "POST" });
      return loadClaude();
    }
    if (t.dataset.claudeDismiss) return dismissClaudeJobs([t.dataset.claudeDismiss]);
    if (t.dataset.claudeRestore) return dismissClaudeJobs([t.dataset.claudeRestore], false);
    showClaudeJob(t.dataset.claudeDetails);
  } catch (err) {
    alert(err.message);
  }
});

async function showClaudeJob(id) {
  const j = await api(`/api/claude/jobs/${id}`);
  const prompt = await api(`/api/claude/jobs/${id}/prompt`).catch(() => null);
  const facts = [
    `Status: ${j.status}`, j.model && `model ${j.model}`, j.turns && `${j.turns} turns`, j.toolCalls && `${j.toolCalls} tool calls`,
    j.costUsd != null && `$${j.costUsd.toFixed(2)}`,
  ].filter(Boolean).join(" · ");
  showDetails(`Claude: ${j.label}`, `
    <p><strong>${esc(j.documentName)}</strong><br><span class="muted small">${esc(facts)} · started ${when(j.startedAt ?? j.createdAt)}${j.finishedAt ? `, finished ${when(j.finishedAt)}` : ""}</span></p>
    ${j.error ? `<p class="status-failed">${esc(j.error)}</p>` : ""}
    ${j.result && !j.error?.startsWith(j.result.trim()) ? `<h3>Claude's report</h3><pre class="claude-log">${esc(j.result)}</pre>` : ""}
    ${j.instructions ? `<h3>Your instructions</h3><pre class="claude-log">${esc(j.instructions)}</pre>` : ""}
    <h3>Activity</h3><pre class="claude-log">${esc((j.activity ?? []).join("\n") || "(nothing yet)")}</pre>
    ${prompt ? `<details><summary>Prompt sent to Claude Code</summary><pre class="claude-log">${esc(prompt)}</pre></details>` : ""}
    ${j.sessionId ? `<p class="muted small">To continue this conversation yourself: <code>cd data/claude && claude --resume ${esc(j.sessionId)}</code></p>` : ""}`);
}

async function startClaude(id, request) {
  await postJson(`/api/documents/${id}/claude`, request);
  await loadClaude();
  renderDocs();
}

let claudeDocId = null;

async function openClaudeDialog(id) {
  const d = docs.find((x) => x.id === id);
  if (!d) return;
  claudeDocId = id;
  const pdf = d.contentType === "pdf";
  $("claudeTitle").textContent = d.name.split("/").pop();
  document.querySelectorAll("#claudeTasks .pdf-only").forEach((l) => l.hidden = !pdf);
  const pick = pdf ? (d.problemPages > 0 ? "problems" : "sample") : "custom";
  document.querySelector(`input[name="claudeTask"][value="${pick}"]`).checked = true;
  $("claudeProblemsHint").textContent = d.problemPages > 0
    ? `${d.problemPages} page${d.problemPages > 1 ? "s" : ""} with no text, garbled text, or a region the OCR model failed on.`
    : "None right now. Pages with no text, garbled text, or a region the OCR model failed on.";
  $("claudePages").value = "";
  $("claudeInstructions").value = "";
  const warn = !claudeStatus.agentsCanWrite ? "Agents read and Agents write are off, so Claude couldn't save anything. Turn them on first." : "";
  $("claudeWarning").textContent = warn;
  $("claudeWarning").hidden = !warn;
  updateClaudeDialog();
  $("claudeDialog").returnValue = "";
  $("claudeDialog").showModal();
}

function updateClaudeDialog() {
  const task = document.querySelector('input[name="claudeTask"]:checked').value;
  $("claudePagesRow").hidden = task === "sample" || task === "custom";
  $("claudePages").required = task === "pages";
  $("claudePages").placeholder = task === "pages" ? "e.g. 12, 40-45" : "e.g. 1-100 (empty: all)";
  $("claudeInstructions").required = task === "custom";
}

document.querySelectorAll('input[name="claudeTask"]').forEach((r) => r.addEventListener("change", updateClaudeDialog));

$("claudeDialog").addEventListener("close", async () => {
  if ($("claudeDialog").returnValue !== "confirm" || !claudeDocId) return;
  const task = document.querySelector('input[name="claudeTask"]:checked').value;
  try {
    await startClaude(claudeDocId, { task, pages: $("claudePagesRow").hidden ? null : $("claudePages").value, instructions: $("claudeInstructions").value });
  } catch (err) {
    alert(err.message);
  }
});
