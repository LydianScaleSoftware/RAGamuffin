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
