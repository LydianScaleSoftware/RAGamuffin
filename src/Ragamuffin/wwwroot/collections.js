"use strict";

const params = new URLSearchParams(location.search);
// Arrived from the documents page's "New collection…" button: focus the form and return afterwards.
const returnAfterCreate = params.get("return") === "1";

let cols = [];

async function load() {
  cols = await api("/api/collections");
  render();
}

function render() {
  $("colCount").textContent = `(${cols.length})`;
  $("cols").innerHTML = cols.map((c) => `
    <tr data-name="${esc(c.name)}">
      <td class="check">${c.isDefault ? "" : `<input type="checkbox" class="pick">`}</td>
      <td>${c.isDefault
        ? `<span class="muted" title="Built-in collection; cannot be renamed or removed">(default)</span>`
        : `<input class="rename" value="${esc(c.name)}" data-original="${esc(c.name)}" maxlength="100" spellcheck="false">`}</td>
      <td><textarea class="desc" rows="2" maxlength="1000" placeholder="What this collection covers…" data-original="${esc(c.description ?? "")}">${esc(c.description ?? "")}</textarea></td>
      <td><textarea class="keywords" rows="2" placeholder="comma, separated, terms" spellcheck="false" data-original="${esc(c.keywords.join(", "))}">${esc(c.keywords.join(", "))}</textarea></td>
      <td><button class="small secondary suggest" ${c.documents ? "" : "disabled title=\"Add documents first\""}>Suggest</button></td>
      <td class="num">${c.documents}</td>
      <td class="num">${c.chunks}</td>
      <td>${when(c.createdAt)}</td>
    </tr>`).join("");
  $("selectAll").checked = false;
  updateButtons();
}

const renameInputs = () => [...document.querySelectorAll("input.rename")];
const pendingRenames = () => renameInputs()
  .filter((i) => i.value.trim() !== i.dataset.original)
  .map((i) => ({ from: i.dataset.original, to: i.value.trim() }));
const selected = () => [...document.querySelectorAll("input.pick:checked")].map((i) => i.closest("tr").dataset.name);

// Description and keyword edits, keyed by the collection's current (saved) name.
const normKeywords = (text) => text.split(/[,;\n]/).map((k) => k.trim()).filter(Boolean).join(", ");
const detailFields = () => [...document.querySelectorAll("#cols tr")].map((row) => ({
  row, name: row.dataset.name, desc: row.querySelector("textarea.desc"), keywords: row.querySelector("textarea.keywords"),
}));
const detailChanged = (f) => f.desc.value.trim() !== f.desc.dataset.original || normKeywords(f.keywords.value) !== normKeywords(f.keywords.dataset.original);
const pendingDetails = () => detailFields().filter(detailChanged).map((f) => ({
  name: f.name, description: f.desc.value.trim() || null, keywords: normKeywords(f.keywords.value).split(", ").filter(Boolean),
}));

function updateButtons() {
  const renames = pendingRenames().length;
  const details = pendingDetails().length;
  const changes = renames + details;
  $("saveBtn").disabled = changes === 0;
  $("discardBtn").disabled = changes === 0;
  $("saveBtn").textContent = changes > 1 ? `Save ${changes} changes` : "Save changes";
  detailFields().forEach((f) => {
    f.desc.classList.toggle("changed", f.desc.value.trim() !== f.desc.dataset.original);
    f.keywords.classList.toggle("changed", normKeywords(f.keywords.value) !== normKeywords(f.keywords.dataset.original));
  });
  const picks = selected().length;
  $("removeBtn").disabled = picks === 0;
  $("removeBtn").textContent = picks > 1 ? `Remove ${picks} selected…` : "Remove selected…";
  renameInputs().forEach((i) => i.classList.toggle("changed", i.value.trim() !== i.dataset.original));
}

function showMessage(text, kind) {
  const el = $("message");
  el.hidden = !text;
  el.className = `message ${kind ?? ""}`;
  el.innerHTML = text ? text.split("\n").map(esc).join("<br>") : "";
}

$("cols").addEventListener("input", updateButtons);
$("cols").addEventListener("change", updateButtons);
$("cols").addEventListener("keydown", (e) => {
  if (e.target.matches("input.rename") && e.key === "Enter" && !$("saveBtn").disabled) $("saveBtn").click();
  if (e.target.matches("input.rename") && e.key === "Escape") { e.target.value = e.target.dataset.original; updateButtons(); }
});
$("selectAll").addEventListener("change", (e) => {
  document.querySelectorAll("input.pick").forEach((i) => { i.checked = e.target.checked; });
  updateButtons();
});

// ---------- create ----------
$("createForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const names = $("newNames").value.split("\n").map((n) => n.trim()).filter(Boolean);
  if (names.length === 0) return;
  const log = $("createLog");
  try {
    const results = await postJson("/api/collections", { names });
    log.innerHTML = results.map((r) => `<li class="${r.outcome === "created" ? "status-ready" : r.outcome === "exists" ? "muted" : "status-failed"}">
      ${esc(r.outcome)}: ${esc(r.name || "(empty)")}${r.error ? " — " + esc(r.error) : ""}</li>`).join("");
    const created = results.filter((r) => r.outcome === "created" || r.outcome === "exists");
    if (results.every((r) => r.outcome !== "rejected")) $("newNames").value = "";
    if (returnAfterCreate && created.length > 0) {
      // Go back to the documents page with the (first) new collection selected for uploads.
      location.href = `/?collection=${encodeURIComponent(created[0].name)}`;
      return;
    }
    await load();
  } catch (err) {
    log.innerHTML = `<li class="status-failed">${esc(err.message)}</li>`;
  }
});

// ---------- rename ----------
$("saveBtn").addEventListener("click", async () => {
  const renames = pendingRenames();
  const details = pendingDetails();
  const done = [];
  try {
    // Details are saved under the current names, before any rename.
    if (details.length) {
      await postJson("/api/collections/details", { items: details });
      done.push(`Saved details for ${details.length} collection${details.length > 1 ? "s" : ""}.`);
    }
  } catch (err) {
    showMessage("Details weren't saved:\n" + (err.errors ?? [err.message]).join("\n"), "bad");
    return;
  }
  try {
    if (renames.length) {
      await postJson("/api/collections/rename", { renames });
      done.push(`Renamed ${renames.length} collection${renames.length > 1 ? "s" : ""}.`);
    }
    showMessage(done.join("\n"), "ok");
    await load();
  } catch (err) {
    showMessage([...done, "Nothing was renamed:", ...(err.errors ?? [err.message])].join("\n"), "bad");
    await load();
  }
});

// Description and keywords expand while being edited. Keywords switch to one per line, which is easier to edit
// than a long comma-separated line, and fold back to "a, b, c" afterwards (commas, semicolons and new lines all
// separate keywords, so either form saves the same).
const toLines = (text) => normKeywords(text).split(", ").filter(Boolean).join("\n");
$("cols").addEventListener("focusin", (e) => {
  const t = e.target;
  if (t.matches("textarea.keywords")) {
    t.value = toLines(t.value);
    t.rows = Math.min(14, Math.max(6, t.value.split("\n").length + 1));
    t.classList.add("editing");
    // Reflowing to one per line moves text under the caret, so put it somewhere predictable: a new line at the end.
    // (Deferred, because a mouse click positions the caret after the focus event.)
    if (t.value) t.value += "\n";
    setTimeout(() => t.setSelectionRange(t.value.length, t.value.length), 0);
  } else if (t.matches("textarea.desc")) {
    t.rows = 6;
    t.classList.add("editing");
  }
});
$("cols").addEventListener("focusout", (e) => {
  const t = e.target;
  if (t.matches("textarea.keywords")) {
    t.value = normKeywords(t.value);
    t.rows = 2;
    t.classList.remove("editing");
    updateButtons();
  } else if (t.matches("textarea.desc")) {
    t.rows = 2;
    t.classList.remove("editing");
  }
});
$("cols").addEventListener("keydown", (e) => {
  if (e.target.matches("textarea.keywords, textarea.desc") && e.key === "Escape") e.target.blur();
});

// Suggest: a local model drafts the description and keywords; nothing is saved until "Save changes".
$("cols").addEventListener("click", async (e) => {
  const btn = e.target.closest("button.suggest");
  if (!btn) return;
  const row = btn.closest("tr");
  const original = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Thinking…";
  try {
    const s = await postJson("/api/collections/suggest", { name: row.dataset.name });
    if (s.description) row.querySelector("textarea.desc").value = s.description;
    if (s.keywords?.length) row.querySelector("textarea.keywords").value = s.keywords.join(", ");
    showMessage(`Drafted details for ${collectionLabel(row.dataset.name)}. Edit them if needed, then Save changes.`, "ok");
    updateButtons();
  } catch (err) {
    showMessage((err.errors ?? [err.message]).join("\n"), "bad");
  } finally {
    btn.disabled = false;
    btn.textContent = original;
  }
});

$("discardBtn").addEventListener("click", () => {
  renameInputs().forEach((i) => { i.value = i.dataset.original; });
  detailFields().forEach((f) => { f.desc.value = f.desc.dataset.original; f.keywords.value = f.keywords.dataset.original; });
  showMessage("");
  updateButtons();
});

// ---------- remove ----------
$("removeBtn").addEventListener("click", () => {
  const names = selected();
  const docs = cols.filter((c) => names.includes(c.name)).reduce((n, c) => n + c.documents, 0);
  $("removeCount").textContent = names.length > 1 ? `${names.length} collections` : "collection";
  $("removeList").textContent = names.join(", ");
  $("removeDocCount").textContent = docs > 0 ? `(${docs} document${docs > 1 ? "s" : ""})` : "";
  $("removeForm").querySelector('input[value="move"]').checked = true;
  $("removeDialog").returnValue = ""; // closing with Escape must not reuse a previous "confirm"
  $("removeDialog").showModal();
});

$("removeDialog").addEventListener("close", async () => {
  if ($("removeDialog").returnValue !== "confirm") return;
  const names = selected();
  const documents = $("removeForm").querySelector('input[name="docs"]:checked').value;
  try {
    const results = await postJson("/api/collections/remove", { names, documents });
    const failed = results.filter((r) => r.error);
    const ok = results.filter((r) => !r.error);
    const moved = ok.reduce((n, r) => n + r.documentsMoved, 0);
    const deleted = ok.reduce((n, r) => n + r.documentsDeleted, 0);
    const lines = [];
    if (ok.length) lines.push(`Removed ${ok.length} collection${ok.length > 1 ? "s" : ""}` +
      (moved ? `; moved ${moved} document${moved > 1 ? "s" : ""} to (default)` : "") +
      (deleted ? `; deleted ${deleted} document${deleted > 1 ? "s" : ""}` : "") + ".");
    failed.forEach((r) => lines.push(`${r.name}: ${r.error}`));
    showMessage(lines.join("\n"), failed.length ? "bad" : "ok");
    await load();
  } catch (err) {
    showMessage(err.message, "bad");
  }
});

// Warn before leaving with unsaved renames.
window.addEventListener("beforeunload", (e) => {
  if (pendingRenames().length > 0 || pendingDetails().length > 0) e.preventDefault();
});

if (params.get("new") === "1") {
  $("newNames").focus();
  if (returnAfterCreate) {
    $("backLink").hidden = false;
    $("createHint").textContent = "After creating, you'll go back to the documents page with the new collection selected.";
  }
}

checkHealth();
load();
