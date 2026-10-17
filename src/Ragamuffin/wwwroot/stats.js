"use strict";

const fmt = (n) => (n ?? 0).toLocaleString();
const bytes = (b) => b < 1048576 ? `${(b / 1024).toFixed(1)} KB` : b < 1073741824 ? `${(b / 1048576).toFixed(1)} MB` : `${(b / 1073741824).toFixed(2)} GB`;
const SECONDS_PER_PAGE = 45; // rough average for layout + OCR on this setup

/**
 * Link to the Documents page filtered to what a number counts. The collection travels as "fc" (the plain
 * "collection" parameter means "select this upload target" on that page).
 */
function drill(value, group, filter, display = fmt(value)) {
  if (!value) return `<span class="muted">${display}</span>`;
  const params = new URLSearchParams();
  if (group && !group.total) params.set("fc", group.name);
  if (filter) params.set("filter", filter);
  return `<a class="drill" href="/?${params}" title="Show these documents">${display}</a>`;
}

function groupLabel(g) {
  return g.total ? "<strong>All collections</strong>" : esc(collectionLabel(g.name));
}

/** A table with one row per collection and a grand-total row. columns: [{ head, cell(g) }] */
function table(id, groups, total, columns) {
  const head = `<thead><tr><th>Collection</th>${columns.map((c) => `<th class="num">${c.head}</th>`).join("")}</tr></thead>`;
  const row = (g) => `<tr${g.total ? ' class="total"' : ""}><td>${groupLabel(g)}</td>${columns.map((c) => `<td class="num">${c.cell(g)}</td>`).join("")}</tr>`;
  $(id).innerHTML = head + `<tbody>${groups.map(row).join("")}${row(total)}</tbody>`;
}

function tile(label, value, sub = "") {
  return `<div class="tile"><div class="tile-value">${value}</div><div class="tile-label">${label}</div>${sub ? `<div class="tile-sub">${sub}</div>` : ""}</div>`;
}

async function load() {
  const r = await api("/api/stats");
  const total = { ...r.total, total: true };
  const groups = r.collections;
  const t = total;

  $("generated").textContent = `as of ${when(r.generatedAt)}`;
  $("tiles").innerHTML = [
    tile("Documents", fmt(t.documents)),
    tile("PDF pages", fmt(t.pages)),
    tile("Chunks", fmt(t.chunks), `${t.avgChunksPerDocument} per document`),
    tile("Characters indexed", fmt(t.characters), `${fmt(t.avgChunkChars)} per chunk`),
    tile("Original files", bytes(t.sizeBytes)),
    tile("Database", bytes(r.databaseBytes)),
  ].join("");
  $("facts").innerHTML = [
    `${groups.length} collection${groups.length === 1 ? "" : "s"}`,
    `embeddings: ${esc(r.embeddingModel)}`,
    t.addedLast7Days ? `${drill(t.addedLast7Days, total, "added:7")} added in the last 7 days` : "nothing added in the last 7 days",
    t.lastUpdated ? `last change ${when(t.lastUpdated)}` : "",
  ].filter(Boolean).join(" · ");

  const remaining = t.pagesNeedingOcr + t.pagesWithoutLayout;
  const hours = remaining * SECONDS_PER_PAGE / 3600;
  $("work").innerHTML = [
    tile("Pages needing OCR", drill(t.pagesNeedingOcr, total, "pages:needs-ocr")),
    tile("Pages not laid out", drill(t.pagesWithoutLayout, total, "pages:no-layout")),
    tile("Estimated time", remaining ? (hours < 1 ? `${Math.max(1, Math.round(hours * 60))} min` : `${hours.toFixed(1)} h`) : "—", "at about 45 s per page"),
    tile("Pages that look like poor old OCR", drill(t.suspectPages, total, "pages:suspect")),
    tile("Documents with notices", drill(t.documentsWithNotices, total, "notices")),
    tile("Failed", drill(t.statuses.failed ?? 0, total, "status:failed")),
  ].join("");

  table("overviewTable", groups, total, [
    { head: "Documents", cell: (g) => drill(g.documents, g, "") },
    { head: "PDF pages", cell: (g) => fmt(g.pages) },
    { head: "Chunks", cell: (g) => fmt(g.chunks) },
    { head: "Characters", cell: (g) => fmt(g.characters) },
    { head: "Size", cell: (g) => bytes(g.sizeBytes) },
  ]);

  const TYPES = [["markdown", "Markdown"], ["text", "Text"], ["code", "Code"], ["pdf", "PDF"]];
  table("typesTable", groups, total, TYPES.map(([key, head]) => ({ head, cell: (g) => drill(g.types[key] ?? 0, g, `type:${key}`) })));
  const langs = Object.entries(t.languages);
  $("languages").innerHTML = langs.length
    ? "Code by language: " + langs.map(([lang, n]) => `${esc(lang)} ${drill(n, total, `language:${lang}`)}`).join(" · ")
    : "";

  const ORIGINS = [
    ["OriginalText", "Original text"], ["PdfEmbedded", "PDF embedded"], ["PdfEmbeddedLayout", "Embedded + layout"],
    ["PdfOcr", "PDF OCR"], ["PdfMixed", "Mixed"], ["PdfNoText", "No text yet"],
  ];
  table("originTable", groups, total, ORIGINS.map(([key, head]) => ({ head, cell: (g) => drill(g.origins[key] ?? 0, g, `origin:${key}`) })));

  table("pagesTable", groups, total, [
    { head: "Embedded", cell: (g) => drill(g.pdfPages.embedded, g, "pages:no-layout") },
    { head: "Layout-arranged", cell: (g) => drill(g.pdfPages.layout, g, "pages:layout") },
    { head: "OCR (by region)", cell: (g) => drill(g.pdfPages.ocrWithLayout, g, "pages:ocr") },
    { head: "OCR (whole page)", cell: (g) => drill(g.pdfPages.ocrWholePage, g, "pages:ocr") },
    { head: "Blank / needs OCR", cell: (g) => drill(g.pdfPages.blank, g, "pages:needs-ocr") },
  ]);

  table("statusTable", groups, total, [
    { head: "Ready", cell: (g) => drill(g.statuses.ready ?? 0, g, "status:ready") },
    { head: "Needs OCR", cell: (g) => drill(g.statuses.needs_ocr ?? 0, g, "status:needs_ocr") },
    { head: "In progress", cell: (g) => drill((g.statuses.pending ?? 0) + (g.statuses.ocr ?? 0), g, "status:busy") },
    { head: "Failed", cell: (g) => drill(g.statuses.failed ?? 0, g, "status:failed") },
    { head: "With notices", cell: (g) => drill(g.documentsWithNotices, g, "notices") },
  ]);

  $("largestTable").innerHTML = `<thead><tr><th>Document</th><th class="num">Pages</th><th class="num">Chunks</th><th class="num">Size</th></tr></thead><tbody>` +
    r.largest.map((d) => {
      const params = new URLSearchParams({ fc: d.collection, q: d.name });
      return `<tr><td class="name"><a href="/?${params}">${esc(d.name.split("/").pop())}</a> <span class="muted small">${esc(collectionLabel(d.collection))}</span></td>
        <td class="num">${d.contentType === "pdf" ? fmt(d.pages) : "—"}</td><td class="num">${fmt(d.chunks)}</td><td class="num">${bytes(d.sizeBytes)}</td></tr>`;
    }).join("") + "</tbody>";

  const models = Object.entries(r.ocrModels);
  $("modelsTable").innerHTML = models.length
    ? `<thead><tr><th>Model</th><th class="num">Pages</th></tr></thead><tbody>` +
      models.map(([m, n]) => `<tr><td>${esc(m)}</td><td class="num">${fmt(n)}</td></tr>`).join("") + "</tbody>"
    : `<tbody><tr><td class="muted">No pages have been OCR'd yet.</td></tr></tbody>`;
}

$("refreshBtn").addEventListener("click", load);
checkHealth();
load().catch((err) => { $("tiles").innerHTML = `<p class="status-failed">${esc(err.message)}</p>`; });
