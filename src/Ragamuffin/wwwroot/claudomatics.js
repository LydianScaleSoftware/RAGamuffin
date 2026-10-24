"use strict";

// The Claudomatics page: chunks Claude chose, grouped by day and document, with a dialog to read one in full, fix its
// title or summary, delete it, and look at the PDF page it came from.

let items = [];
let current = null;          // the chunk open in the dialog
let pageRange = null;        // { docId, first, last, page } for the source-page pane
let pendingDelete = null;    // the document the delete dialog is about

// Source groups (one document on one day) start collapsed; the ones the user opens are remembered for the session.
// While a filter is typed in, groups start open so the matches show.
const OPEN_KEY = "claudomatics.open";
const openGroups = new Set(JSON.parse(sessionGet(OPEN_KEY) ?? "[]"));
const closedWhileFiltering = new Set();
const groupKey = (d, docId) => `${d}|${docId}`;
const filtering = () => $("filter").value.trim() !== "";
const isOpen = (key) => filtering() ? !closedWhileFiltering.has(key) : openGroups.has(key);
function sessionGet(key) { try { return sessionStorage.getItem(key); } catch { return null; } }
function saveOpen() { try { sessionStorage.setItem(OPEN_KEY, JSON.stringify([...openGroups])); } catch { /* storage blocked */ } }
function setOpen(key, open) {
  if (filtering()) { if (open) closedWhileFiltering.delete(key); else closedWhileFiltering.add(key); }
  else { if (open) openGroups.add(key); else openGroups.delete(key); saveOpen(); }
}

const day = (d) => new Date(d).toLocaleDateString(undefined, { weekday: "short", year: "numeric", month: "short", day: "numeric" });
const pagesText = (c) => c.page == null ? "" : c.pageEnd != null && c.pageEnd !== c.page ? `pp. ${c.page}–${c.pageEnd}` : `p. ${c.page}`;
const linesText = (c) => c.startLine == null ? "" : `lines ${c.startLine}–${c.endLine}`;
const encodedBadge = (enc) => enc
  ? ` <span class="badge encoded" title="A binary file stored as text (${esc(enc)} dump: offset, bytes and ASCII per line). The original bytes are kept.">⬢ Encoded · ${esc(enc)}</span>`
  : "";

async function load() {
  const params = new URLSearchParams();
  if ($("filter").value.trim()) params.set("q", $("filter").value.trim());
  if ($("collection").value) params.set("collection", $("collection").value);
  const r = await api(`/api/claudomatics?${params}`);
  items = r.items;
  $("count").textContent = r.total === items.length
    ? `${r.total.toLocaleString()} Claudomatic${r.total === 1 ? "" : "s"}`
    : `showing ${items.length.toLocaleString()} of ${r.total.toLocaleString()}`;
  render();
}

function render() {
  if (!items.length) {
    $("groups").innerHTML = `<p class="muted">No Claudomatics${$("filter").value || $("collection").value ? " match" : " yet. Ask Claude to convert a manual (rag-convert-pdf) or re-chunk a document"}.</p>`;
    return;
  }
  // Day → document → chunks, in the order the API returns them (newest first).
  const days = new Map();
  for (const c of items) {
    const d = day(c.createdAt);
    if (!days.has(d)) days.set(d, new Map());
    const docs = days.get(d);
    if (!docs.has(c.documentId)) docs.set(c.documentId, []);
    docs.get(c.documentId).push(c);
  }
  $("groups").innerHTML = [...days].map(([d, docs]) => `
    <h3 class="claudo-day">${esc(d)}</h3>
    ${[...docs.values()].map((chunks) => {
      const first = chunks[0];
      chunks.sort((a, b) => a.ordinal - b.ordinal);
      const key = groupKey(d, first.documentId);
      return `
      <div class="claudo-doc${isOpen(key) ? " open" : ""}" data-key="${esc(key)}">
        <div class="row spread claudo-doc-head" role="button" tabindex="0" aria-expanded="${isOpen(key)}" title="Show or hide this document's Claudomatics">
          <div>
            <span class="caret" aria-hidden="true">▸</span>
            <strong>${esc(first.documentName)}</strong>
            <span class="muted">· ${esc(collectionLabel(first.collection))}</span>${encodedBadge(first.encoding)}
            ${first.sourceName ? `<span class="badge muted-badge" title="Converted from ${esc(first.sourceName)}">Source…</span>` : ""}
            <span class="muted small">${chunks.length} Claudomatic${chunks.length === 1 ? "" : "s"}</span>
          </div>
          <div class="row">
            <a class="button secondary small" href="/api/documents/${first.documentId}/export" title="Download the document's text (${first.encoding ? "the hex dump" : "e.g. the Markdown conversion"})">Export</a>
            ${first.encoding ? `<a class="button secondary small" href="/api/documents/${first.documentId}/file" title="Download the original binary file">Original bytes</a>` : ""}
            <button class="secondary small danger" data-delete-doc="${first.documentId}" data-name="${esc(first.documentName)}">Delete document</button>
          </div>
        </div>
        <div class="claudo-doc-body">
        ${chunks.map((c) => `
          <div class="claudo-row" data-id="${c.id}" tabindex="0">
            <div class="claudo-row-head">
              <strong>${esc(c.title ?? `Chunk ${c.ordinal + 1}`)}</strong>
              ${c.kind ? `<span class="badge muted-badge">${esc(c.kind)}${c.symbol ? ` ${esc(c.symbol)}` : ""}</span>` : ""}
              <span class="muted small">${[linesText(c), pagesText(c) && "from " + pagesText(c), `${c.chars.toLocaleString()} chars`].filter(Boolean).join(" · ")}</span>
            </div>
            ${c.summary ? `<div class="chunk-summary">${esc(c.summary)}</div>` : ""}
            <div class="claudo-snippet">${esc(c.snippet)}${c.chars > c.snippet.length ? "…" : ""}</div>
          </div>`).join("")}
        </div>
      </div>`;
    }).join("")}`).join("");
}

async function openChunk(id) {
  current = await api(`/api/chunks/${id}`);
  $("chunkHeading").textContent = current.title ?? `Chunk ${current.ordinal + 1}`;
  $("chunkMeta").innerHTML = [
    `${esc(current.documentName)} · ${esc(collectionLabel(current.collection))}`,
    current.headingPath && `§ ${esc(current.headingPath)}`,
    linesText(current),
    current.sourceName && pagesText(current) && `from ${esc(current.sourceName.split("/").pop())} ${pagesText(current)}`,
    current.kind && esc(current.kind + (current.symbol ? " " + current.symbol : "")),
    `written ${when(current.createdAt)}`,
    `chunk ${current.id}`,
  ].filter(Boolean).join(" · ") + encodedBadge(current.encoding);
  $("chunkTitle").value = current.title ?? "";
  $("chunkSummary").value = current.summary ?? "";
  $("chunkContent").textContent = current.content;
  $("chunkMessage").textContent = "";
  const hasPage = current.sourceDocumentId && current.page != null;
  $("chunkPageBtn").hidden = !hasPage;
  $("pagePane").hidden = true;
  $("chunkDialog").classList.remove("with-page");
  $("chunkDialog").showModal();
}

function showPage(page) {
  pageRange.page = Math.min(Math.max(page, pageRange.first), pageRange.last);
  $("pageImage").src = `/api/documents/${pageRange.docId}/pages/${pageRange.page}/image`;
  $("pageLabel").textContent = `${current.sourceName.split("/").pop()} · page ${pageRange.page}`;
  $("pagePrev").disabled = pageRange.page <= pageRange.first;
  $("pageNext").disabled = pageRange.page >= pageRange.last;
}

$("chunkPageBtn").addEventListener("click", () => {
  pageRange = { docId: current.sourceDocumentId, first: current.page, last: current.pageEnd ?? current.page };
  $("pagePane").hidden = false;
  $("chunkDialog").classList.add("with-page");
  showPage(pageRange.first);
});
$("pageImage").addEventListener("error", () => { $("pageLabel").textContent += " — the page image couldn't be rendered"; });
$("pagePrev").addEventListener("click", () => showPage(pageRange.page - 1));
$("pageNext").addEventListener("click", () => showPage(pageRange.page + 1));

$("chunkSave").addEventListener("click", async () => {
  $("chunkMessage").textContent = "Saving and re-embedding…";
  try {
    await api(`/api/chunks/${current.id}`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: $("chunkTitle").value, summary: $("chunkSummary").value }),
    });
    $("chunkMessage").textContent = "Saved.";
    $("chunkHeading").textContent = $("chunkTitle").value || `Chunk ${current.ordinal + 1}`;
    await load();
  } catch (e) {
    $("chunkMessage").textContent = e.message;
  }
});

$("chunkDelete").addEventListener("click", async () => {
  if (!confirm(`Delete this Claudomatic ("${current.title ?? "untitled"}")? Its lines stop being searchable until Claude re-chunks ${current.documentName}.`)) return;
  await api(`/api/chunks/${current.id}`, { method: "DELETE" });
  $("chunkDialog").close();
  await load();
});

$("chunkClose").addEventListener("click", () => $("chunkDialog").close());

function toggleGroup(group) {
  const open = !group.classList.contains("open");
  group.classList.toggle("open", open);
  group.querySelector(".claudo-doc-head").setAttribute("aria-expanded", open);
  setOpen(group.dataset.key, open);
}

function setAll(open) {
  document.querySelectorAll(".claudo-doc").forEach((g) => {
    g.classList.toggle("open", open);
    g.querySelector(".claudo-doc-head").setAttribute("aria-expanded", open);
    setOpen(g.dataset.key, open);
  });
}
$("expandAll").addEventListener("click", () => setAll(true));
$("collapseAll").addEventListener("click", () => setAll(false));

// Deleting a document removes all its Claudomatics, including ones written on other days than the group it was
// clicked in, so the dialog lists every day it reaches and says so plainly.
async function confirmDelete(button) {
  const docId = button.dataset.deleteDoc;
  const groupDay = button.closest(".claudo-doc").dataset.key.split("|")[0];
  const dates = await api(`/api/documents/${docId}/claudomatic-dates`);
  const perDay = new Map();
  for (const d of dates) perDay.set(day(d), (perDay.get(day(d)) ?? 0) + 1);
  const otherDays = [...perDay].filter(([d]) => d !== groupDay);
  const others = otherDays.reduce((n, [, c]) => n + c, 0);
  const total = `${dates.length.toLocaleString()} Claudomatic${dates.length === 1 ? "" : "s"}`;

  pendingDelete = docId;
  $("deleteName").textContent = button.dataset.name;
  $("deleteTotal").textContent = `all ${total}`;
  $("deleteDays").innerHTML = [...perDay].map(([d, n]) =>
    `<li>${esc(d)}: ${n.toLocaleString()}${d === groupDay ? " <span class=\"muted\">(the group you clicked)</span>" : ""}</li>`).join("");
  $("deleteWarning").hidden = others === 0;
  $("deleteWarning").textContent = others === 0 ? "" :
    `This includes ${others.toLocaleString()} Claudomatic${others === 1 ? "" : "s"} written on ${otherDays.length === 1 ? "another day" : `${otherDays.length} other days`} (${otherDays.map(([d]) => d).join(", ")}), not only the ones under ${groupDay}.`;
  $("deleteConfirm").textContent = `Delete document and ${total}`;
  $("deleteDialog").showModal();
}
$("deleteCancel").addEventListener("click", () => $("deleteDialog").close());
$("deleteConfirm").addEventListener("click", async () => {
  $("deleteConfirm").disabled = true;
  try {
    await api(`/api/documents/${pendingDelete}`, { method: "DELETE" });
    $("deleteDialog").close();
    await load();
  } catch (e) {
    alert(`Couldn't delete: ${e.message}`);
  } finally {
    $("deleteConfirm").disabled = false;
  }
});

$("groups").addEventListener("click", async (e) => {
  const del = e.target.closest("[data-delete-doc]");
  if (del) { await confirmDelete(del); return; }
  if (e.target.closest("a, button")) return;   // Export / Original bytes links
  const row = e.target.closest(".claudo-row");
  if (row) { openChunk(row.dataset.id); return; }
  const head = e.target.closest(".claudo-doc-head");
  if (head) toggleGroup(head.closest(".claudo-doc"));
});
$("groups").addEventListener("keydown", (e) => {
  if (e.key !== "Enter" && e.key !== " ") return;
  const row = e.target.closest(".claudo-row");
  const head = e.target.closest(".claudo-doc-head");
  if (row) { e.preventDefault(); openChunk(row.dataset.id); }
  else if (head && e.target === head) { e.preventDefault(); toggleGroup(head.closest(".claudo-doc")); }
});

let filterTimer;
$("filter").addEventListener("input", () => { clearTimeout(filterTimer); closedWhileFiltering.clear(); filterTimer = setTimeout(load, 250); });
$("collection").addEventListener("change", load);

(async () => {
  checkHealth();
  // The Documents page links here with ?q=<document name>.
  const q = new URLSearchParams(location.search).get("q");
  if (q) $("filter").value = q;
  const wantedCollection = new URLSearchParams(location.search).get("collection");
  const collections = await api("/api/collections");
  for (const c of collections.collections ?? collections) {
    const name = c.name ?? c;
    $("collection").insertAdjacentHTML("beforeend", `<option value="${esc(name)}">${esc(collectionLabel(name))}</option>`);
  }
  if (wantedCollection) $("collection").value = wantedCollection;   // from the Stats page's Claudomatics column
  await load();
})();
