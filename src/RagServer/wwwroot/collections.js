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

function updateButtons() {
  const renames = pendingRenames().length;
  $("saveBtn").disabled = renames === 0;
  $("discardBtn").disabled = renames === 0;
  $("saveBtn").textContent = renames > 1 ? `Save ${renames} renames` : "Save renames";
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
  try {
    await postJson("/api/collections/rename", { renames });
    showMessage(`Renamed ${renames.length} collection${renames.length > 1 ? "s" : ""}.`, "ok");
    await load();
  } catch (err) {
    showMessage("Nothing was renamed:\n" + (err.errors ?? [err.message]).join("\n"), "bad");
  }
});

$("discardBtn").addEventListener("click", () => {
  renameInputs().forEach((i) => { i.value = i.dataset.original; });
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
  if (pendingRenames().length > 0) e.preventDefault();
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
