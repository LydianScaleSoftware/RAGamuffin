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
