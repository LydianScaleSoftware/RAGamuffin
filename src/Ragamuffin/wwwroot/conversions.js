"use strict";

// The Conversions page: documents Claude made from another one, grouped by source, each downloadable in full.

let groups = [];

const size = (b) => b < 1024 ? `${b} B` : b < 1048576 ? `${(b / 1024).toFixed(1)} KB` : `${(b / 1048576).toFixed(1)} MB`;

function render() {
  const q = $("filter").value.trim().toLowerCase();
  const shown = groups
    .map((g) => ({ ...g, conversions: g.conversions.filter((c) => !q || c.name.toLowerCase().includes(q) || (g.source?.name ?? "").toLowerCase().includes(q)) }))
    .filter((g) => g.conversions.length);
  const total = shown.reduce((n, g) => n + g.conversions.length, 0);
  $("count").textContent = `${total} conversion${total === 1 ? "" : "s"} of ${shown.length} document${shown.length === 1 ? "" : "s"}`;
  if (!shown.length) {
    $("groups").innerHTML = `<p class="muted">${groups.length ? "No conversions match." : "No conversions yet. Ask Claude to convert a manual to Markdown (rag-convert-pdf)."}</p>`;
    return;
  }
  $("groups").innerHTML = shown.map((g) => {
    const s = g.source;
    const coverage = !s ? "" : g.coveredPages == null
      ? "Search uses the conversion for the whole document."
      : `Search uses the conversions for pages ${esc(g.coveredPages)}${s.pageCount ? ` (${g.coveredCount} of ${s.pageCount} pages)` : ""}; the other pages are searched from the source.`;
    return `
    <div class="claudo-doc open">
      <div class="row spread claudo-doc-head conv-head">
        <div>
          <strong>${esc(s?.name ?? "(source document removed)")}</strong>
          ${s ? `<span class="muted">· ${esc(collectionLabel(s.collection))} · ${docKind(s.name)}</span>` : ""}
          <div class="muted small">${coverage}</div>
        </div>
        <div class="row">
          ${s ? `<a class="button secondary small" href="/api/documents/${s.id}/file" title="Download the original ${esc(s.name)}">Source file</a>` : ""}
          ${s ? `<a class="button small" href="/api/conversions/${s.id}/zip" title="Download every conversion of this document, in full, as a zip">Download all (.zip)</a>` : ""}
        </div>
      </div>
      <div class="claudo-doc-body">
        <table>
          <thead><tr><th>Conversion</th><th>Source pages</th><th>Chunks</th><th>Size</th><th>Updated</th><th></th></tr></thead>
          <tbody>${g.conversions.map((c) => `
            <tr>
              <td>${esc(c.name)} <span class="muted small">(${docKind(c.name)})</span></td>
              <td>${c.pages ? esc(c.pages) : "all"}</td>
              <td>${c.claudomatics
                ? `<a href="/claudomatics.html?q=${encodeURIComponent(c.name)}" title="Open its Claudomatics">${c.chunkCount} Claudomatics</a>`
                : `${c.chunkCount}`}</td>
              <td>${size(c.sizeBytes)}</td>
              <td>${when(c.updatedAt)}</td>
              <td class="actions"><a class="button secondary small" href="/api/documents/${c.id}/export" title="Download the whole conversion">Download</a></td>
            </tr>`).join("")}
          </tbody>
        </table>
      </div>
    </div>`;
  }).join("");
}

$("filter").addEventListener("input", render);

(async () => {
  checkHealth();
  groups = await api("/api/conversions");
  render();
})();
