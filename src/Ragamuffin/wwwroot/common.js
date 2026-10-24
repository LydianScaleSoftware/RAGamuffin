"use strict";

// Helpers shared by the documents page (app.js) and the collections page (collections.js).

const DEFAULT_COLLECTION = "default";

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const when = (d) => new Date(d).toLocaleString(undefined, { dateStyle: "short", timeStyle: "short" });

/** The built-in collection is shown unnamed. */
const collectionLabel = (name) => name === DEFAULT_COLLECTION ? "(default)" : name;

async function api(path, init) {
  const res = await fetch(path, init);
  if (res.status === 204 || res.status === 202) return null;
  const body = res.headers.get("content-type")?.includes("json") ? await res.json() : await res.text();
  if (!res.ok && res.status !== 503) {
    const err = new Error(body?.errors?.join("\n") ?? (typeof body === "string" ? body : `${res.status}`));
    err.errors = body?.errors;
    throw err;
  }
  return body;
}

const postJson = (path, data) =>
  api(path, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(data) });

function storageGet(key) {
  try { return localStorage.getItem(key); } catch { return null; }
}

function storageSet(key, value) {
  try { localStorage.setItem(key, value); } catch { /* private mode or storage blocked */ }
}

async function checkHealth() {
  const el = $("health");
  try {
    const h = await api("/api/health");
    el.textContent = h.status === "ok" ? `ok · ${h.model} @ ${h.ollamaUrl}` : `degraded · db: ${h.database} · ollama: ${h.ollama}`;
    el.className = "pill " + (h.status === "ok" ? "ok" : "bad");
  } catch {
    el.textContent = "server unreachable";
    el.className = "pill bad";
  }
}

/**
 * The header switches: whether agents (MCP clients such as Claude Code) may use the library at all, and whether they
 * may change it. Off turns the affected tool calls into no-ops that say so.
 */
async function initAgentsSwitch() {
  const read = $("agents-enabled");
  const write = $("agents-writes");
  if (!read) return;
  const show = (s) => {
    read.checked = s.enabled;
    $("agents-label").textContent = s.enabled ? "Agents read" : "Agents off";
    read.closest("label").classList.toggle("off", !s.enabled);
    if (write) {
      write.checked = s.writes;
      $("agents-writes-label").textContent = s.writes ? "Agents write" : "Read only";
      write.closest("label").classList.toggle("off", !s.writes);
      write.disabled = !s.enabled;
    }
  };
  try {
    show(await api("/api/settings/agents"));
    read.disabled = false;
  } catch {
    $("agents-label").textContent = "Agents ?";
    return;
  }
  const change = async (box, body) => {
    box.disabled = true;
    try {
      show(await api("/api/settings/agents", {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
      }));
    } catch (e) {
      box.checked = !box.checked;
      alert(`Couldn't change the agents setting: ${e.message}`);
    } finally {
      box.disabled = box === write && !read.checked;
    }
  };
  read.addEventListener("change", () => change(read, { enabled: read.checked }));
  write?.addEventListener("change", () => change(write, { writes: write.checked }));
}

initAgentsSwitch();

/** Puts a "Clear search" button inside the right end of every input marked data-clearable. Clearing fires
 *  "input" so the page's own filter handlers run. The button hides while the placeholder shows (CSS). */
for (const input of document.querySelectorAll("input[data-clearable]")) {
  const wrap = document.createElement("span");
  wrap.className = "clearable";
  if (input.classList.contains("grow")) { input.classList.remove("grow"); wrap.classList.add("grow"); }
  input.replaceWith(wrap);
  const button = document.createElement("button");
  button.type = "button";
  button.className = "clear-input";
  button.title = button.ariaLabel = "Clear search";
  button.textContent = "✕";
  button.addEventListener("click", () => {
    input.value = "";
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.focus();
  });
  wrap.append(input, button);
}

/** A dialog for details too long for a tooltip; the text can be scrolled, selected and copied. */
function showDetails(title, html) {
  let dlg = $("detailsDialog");
  if (!dlg) {
    dlg = document.createElement("dialog");
    dlg.id = "detailsDialog";
    dlg.innerHTML = `<h2 id="detailsTitle"></h2><div id="detailsBody" class="details-body"></div>
      <div class="row end"><button id="detailsCopy" class="secondary">Copy</button><button id="detailsClose">Close</button></div>`;
    document.body.append(dlg);
    $("detailsClose").addEventListener("click", () => dlg.close());
    $("detailsCopy").addEventListener("click", async () => {
      await navigator.clipboard.writeText($("detailsBody").innerText);
      $("detailsCopy").textContent = "Copied";
      setTimeout(() => $("detailsCopy").textContent = "Copy", 1500);
    });
  }
  $("detailsTitle").textContent = title;
  $("detailsBody").innerHTML = html;
  dlg.showModal();
}

/** "a PDF", "a Markdown document"… from a document's name, for saying what was converted into what. */
function docKind(name) {
  const ext = String(name).split(".").pop().toLowerCase();
  return { pdf: "a PDF", md: "a Markdown document", markdown: "a Markdown document", txt: "a text document" }[ext] ?? "a document";
}

/** Which pages of the source a conversion covers, given the "156–174" page list the API sends. */
const coveredPages = (pages) => pages ? `pages ${esc(pages)}` : "all of its pages (the conversion has no page numbers)";

/** Details for a conversion's "Source…" badge: d has name, sourceName and sourcePages. */
function showSourceDetails(d) {
  showDetails("Converted from another document", `
    <p><strong>${esc(d.name)}</strong> is ${docKind(d.name)} that Claude made from ${docKind(d.sourceName)},
      <strong>${esc(d.sourceName)}</strong>, by transcribing ${coveredPages(d.sourcePages)} and splitting the text
      into Claudomatics (search chunks Claude chose).</p>
    <p>Search uses this document in place of those pages of the source, whose own chunks for them are left out so the
      same text isn't found twice. The source's other pages are searched as usual, and its page images stay
      available.</p>`);
}

/** Details for a source's "Conversion…" badge: d has name and convertedTo [{ name, pages }]. */
function showConversionDetails(d) {
  const many = d.convertedTo.length > 1;
  const items = d.convertedTo.map((c) =>
    `<li>${c.pages ? `Pages ${esc(c.pages)}` : "All pages"} → <strong>${esc(c.name)}</strong> (${docKind(c.name)})</li>`).join("");
  showDetails(many ? "Searched through conversions" : "Searched through a conversion", `
    <p>Claude transcribed part of this document, <strong>${esc(d.name)}</strong> (${docKind(d.name)}), into
      ${many ? "new documents" : "a new document"} and split the text into Claudomatics (search chunks Claude chose).
      Search uses ${many ? "those documents" : "that document"} for these pages:</p>
    <ul>${items}</ul>
    <p>This document's own chunks for those pages are left out of search, so the same text isn't found twice. Its other
      pages are searched as usual, and every page image stays available.</p>`);
}
