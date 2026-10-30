# RAGamuffin - A document library for Claude Code

A local document library that Claude Code searches and builds through MCP. You add documents in the web UI, or Claude adds them: it checks PDF text against the page images, converts manuals to Markdown, and chooses its own chunks, called **Claudomatics** (Claude + automatic): a section, a table, a listing, a whole function. Chunks are embedded with `qwen3-embedding` on the Ollama box (`tb`, 10.10.10.2) and stored in Postgres with pgvector; search combines meaning and exact words.

```
web UI ──upload──▶ RAGamuffin ──/api/embed──▶ Ollama (tb) qwen3-embedding
                     │  ▲
                     ▼  │ hybrid search (vector + full-text, RRF)
              Postgres 18 + pgvector (Docker)
                        ▲
Claude Code ──MCP /mcp──┘  (search, read, check pages, add Markdown as Claudomatics)
```

| What | Where |
|---|---|
| Web UI | http://localhost:5077 |
| MCP endpoint | http://localhost:5077/mcp |
| Health | http://localhost:5077/api/health |
| Database | Docker container `ragamuffin-postgres`, `localhost:5432`, db/user/password `rag` |
| Layout service | Docker container `ragamuffin-layout`, `localhost:5002` (first start downloads its models, a few minutes) |
| Uploaded originals | `data/files/` |

## Requirements

### Software

| Component | Version | Notes |
|---|---|---|
| .NET SDK | 10 | Builds and runs the server. |
| Docker Desktop | Recent (tested on 29.4) | Runs Postgres + pgvector and the layout service. Allow it at least **8 GB of memory**; **16 GB** is comfortable during layout analysis (*Settings → Resources*). Set it to start at login. |
| Ollama | Recent (tested on 0.40.5) | Embeddings, OCR and Suggest. It can run on this machine or another one on the network; see [Ollama server](#ollama-server) below. |
| macOS | Tested on macOS 15 (Intel) | The launchd scripts are macOS-only. The server itself also runs on Linux and Windows; PDF rendering has native builds for all three. |

MCP clients are optional: Claude Code, or anything that speaks MCP over HTTP.

### Ollama models

| Model | Needed for | Size on disk | Required? | Setting |
|---|---|---|---|---|
| `qwen3-embedding:0.6b` | Embedding every chunk and every search query | 0.6 GB (2.9 GB loaded, with its 32K-token context) | **Yes** | `Rag:EmbeddingModel` |
| `glm-ocr` | OCR of scanned PDF pages | 2.7 GB | Only for OCR | `Rag:Ocr:Model` |
| `qwen3:8b`, or any small instruction-following chat model | **Suggest** on the Collections page (and the optional `ask_library` tool) | 5.2 GB | Optional | `Rag:Collections:SuggestModel`, `Rag:Mcp:AnswerModel` |

```bash
ollama pull qwen3-embedding:0.6b
ollama pull glm-ocr         # optional: batch OCR on the server; Claude can also check pages itself
ollama pull qwen3:8b        # optional, for Suggest
```

**Why qwen3-embedding.** On 30 questions about the IIgs manuals, written in different words from the text, hybrid search put the right passage in the top five 23 times with `qwen3-embedding:0.6b`, against 19 with `nomic-embed-text` and 12 with keyword search alone. It also reads 32,768 tokens per chunk where nomic-embed-text silently stops at 2,048, so a long section or whole function Claude keeps as one chunk is embedded whole. It's slower to embed (about 13 chunks a second on `tb`, against 30), which matters only when re-indexing everything.

Suggest only writes a sentence or two and a keyword list, so a small model is plenty.

Without `glm-ocr`, scanned PDFs stay at **needs OCR** and **Run OCR** is disabled with an explanation. Without the Suggest model, **Suggest** reports an error, and descriptions can still be typed by hand.

### Ollama server

- **Reachable over the network** when it runs on another machine. On that machine, set `OLLAMA_HOST=0.0.0.0` so Ollama listens beyond localhost. Point `Rag:OllamaUrl` at it (here `http://10.10.10.2:11434`).
- **Room for two models at once.** During OCR batches the indexing lane embeds with `qwen3-embedding` while the analysis lane OCRs with `glm-ocr`, so set `OLLAMA_MAX_LOADED_MODELS` to at least 2 (more if you also run other models there). Memory: `qwen3-embedding:0.6b` ≈ 2.9 GB loaded, `glm-ocr` ≈ 3–4 GB with its 16K context, `qwen3:8b` ≈ 5–6 GB while Suggest runs. Every search embeds the query, so if the embedding model has been unloaded, the first search after a while waits a few seconds for it to load.
- **No server-side tuning is required.** RAGamuffin sends its own per-request options (context size, output cap, temperature, `keep_alive`); OCR asks Ollama to unload `glm-ocr` 5 minutes after the last page.

### Disk space

| What | Size |
|---|---|
| Docker images | about 3.3 GB (layout service 2.7 GB, Postgres + pgvector 0.6 GB) |
| Layout models (Docker volume, downloaded on first start) | about 1.8 GB |
| Database | about 0.5 GB for roughly 9,000 PDF pages and 28,000 chunks |
| Uploaded originals (`data/files/`) | the size of your documents |

Every setting is listed in [Configuration.md](Configuration.md).

## Running

```bash
docker compose up -d                 # Postgres + pgvector, and the layout service (both restart with Docker Desktop)
dotnet run --project src/Ragamuffin   # applies migrations on startup
```

To run it permanently as a launchd agent that starts at login, use `scripts/install-service.sh`. It publishes to `publish/` and logs to `data/logs/ragamuffin.log`. Re-run the script after code changes. `scripts/uninstall-service.sh` removes the agent. Docker Desktop must be set to start at login for the database to come back after a reboot.

## Connecting Claude Code

```bash
claude mcp add --transport http --scope user rag http://localhost:5077/mcp
scripts/install-skills.sh      # links the rag-check-pdf and rag-convert-pdf skills into ~/.claude/skills
```

Any other client that speaks MCP over HTTP works too; answers are sized for Claude's large context.

### Reading tools

- `search_documents(query, top_k=8, collection?, collections?, exclude?)`: up to 25 cited passages, found by meaning and by exact words. `collections` searches just those (muted or not); `exclude` leaves some out; results say which muted collections weren't searched. Each shows its document, location, section, title and chunk id. Neighbouring server-made chunks are joined; near-duplicates from other editions are dropped and noted as "also in".
- `get_chunk(ids, neighbors=0)`: chunks whole, by id, optionally with the chunks around them.
- `get_document(name_or_id, pages?, lines?, section?, collection?)`: with no range, the outline (headings with line or page numbers); `pages="12-14"`, `lines="120-200"` or `section="Drawing Text"` read just that part, ending with where to continue. Partial names work.
- `list_documents(filter?, collection?)`: names, types, sizes, chunk counts, conversions (which PDF pages a Markdown conversion covers) and notices about OCR or layout problems.
- `list_collections(collection?)`: what each collection covers (description, keywords), its size and example titles.
- `list_pages(document, which?, pages?)`: a PDF's page health, then its pages with their text source and state (see [Page health](#page-health)). `which="review"` (the default) lists problem pages, then unverified pages only glm-ocr has read; `"problems"` only problem pages, with what's wrong; `"notes"` notes and muted items; `"sample"` about 12 unverified pages spread through the document (favouring tables and listings) for a sample check; `"all"` every page.
- `get_page(document, page)`: one page as an image beside every text version the server has (embedded, glm-ocr, layout, Claude's), with the one in use marked, any recorded problem, and Claude's note.
- `ask_library(question, collection?)`: optional, and only offered when `Rag:Mcp:AnswerModel` is set. A local model writes a short, cited answer from the best passages, for agents with small contexts; Claude doesn't need it.

### Tools that change the library

- `import_file(path, collection?, name?)`: adds a file from this computer as is, like an upload.
- `set_page_text(document, pages, reindex=true)`: saves Claude's text for PDF pages (from the page images) with a note on what was fixed, or confirms the text in use is right (`confirm: true`). Claude's text becomes the page's source, and a recorded OCR problem on the page no longer counts; the other versions are kept, so it can be removed again. A later glm-ocr run doesn't replace it. Saving several batches with `reindex=false` and the last with `reindex=true` re-indexes once.
- `add_document(name, chunks, path | content, collection?, source_document?)`: adds a text document, usually Markdown Claude wrote from a PDF, with **chunks Claude chose (Claudomatics)**: line ranges with a title, section, summary, kind, symbol and source pages. A chunk is one unit of any size (a section, a table, a listing with its explanation, a reference entry, a whole function) up to 60,000 characters. With `source_document`, the PDF pages the conversion covers drop out of search, so results aren't duplicated.
- `set_chunks(document, chunks)`: re-chunks a text document without re-sending it.
- `verify_document(document, note?)`: after Claude has checked a sample of a PDF's pages against their images (at least 10, or every page of a shorter one), records the whole document's text as **verified by sample**, with what the check found. An OCR or layout run that changes the text clears it.
- Binary files (ROMs, disk images, executables) given to `import_file` or `add_document` are stored **encoded** as a hex dump (offset, 16 bytes in hex, and their ASCII per line, like `xxd`), up to 1 MB, with the original bytes kept. Everything read from them is marked `⟦ENCODED BINARY · hex dump⟧`, and the web UI shows a **⬢ Encoded · hex** badge. Search finds byte patterns (`a9 00 8d`) and embedded strings; Claude can re-chunk a dump into titled routines or tables with `set_chunks`. The web upload still rejects binaries.
- `delete_document(document)`: needs the exact name.

`import_file` and `add_document` read files only from the folders in `Rag:Mcp:ImportRoots` (default: your home folder).

### Skills

The two skills in `skills/` tell Claude how to do the work well, so you can just ask ("check the OCR of the Toolbox Reference Volume 3", "convert chapter 16 of Volume 2 to Markdown"):

- **`rag-check-pdf`**: finds the pages that need review, compares each page image with its text, confirms or corrects it, and re-indexes once at the end.
- **`rag-convert-pdf`**: converts a manual chapter by chapter into Markdown with page markers, chooses chunks by meaning, and adds them with `add_document` linked to the PDF.

Page images cost roughly 1,500 tokens each, so a few hundred pages is a long session; the skills say so before starting.

### Claude jobs: corrections from the web UI

You don't have to open Claude Code and type a prompt to get corrections made. The web UI can start **Claude Code headless** on this Mac (`claude -p`) with a prompt it writes for the task, and show its progress:

- **Claude…** on a document row opens **Ask Claude**, with these tasks:
  - **Fix problem pages**: all of them, or the pages you list. These are pages with no text, garbled text, or a region glm-ocr failed on.
  - **Check specific pages**: compare them with their images and correct them.
  - **Check pages needing review**: problems, then every page only glm-ocr has read. This can be a long run.
  - **Check a sample and verify**: about 12 pages, then `verify_document` if they match.
  - **Your own instructions**, for anything else, on any document (e.g. re-chunk a listing).

  Any task can take extra instructions, which are added to the prompt.
- **The health dialog** has **Fix with Claude** on each problem page, **Fix all with Claude**, **Check a sample with Claude** and **Ask Claude…**.
- **The Claude jobs panel** above the documents shows each job's status, its latest step (e.g. `→ get_page p.42`) and cost, with **Cancel** while it runs. Click its title to collapse it. **✕** dismisses a finished job (or **Dismiss finished** for all), which moves it to the older jobs; **Show older** lists them, with **Restore**. **Details** shows the activity log, Claude's closing report, your instructions, the exact prompt, and a `claude --resume` command to continue the conversation yourself. While a job runs, its document shows a **Claude working…** badge, and the list refreshes when it finishes.

How the jobs run:
- **One at a time**, in `data/claude`. A document can't have two jobs queued.
- **Limited tools.** Claude gets only RAGamuffin's reading tools, `list_pages`/`get_page`, `set_page_text`, `verify_document` and `set_chunks`, plus the Skill tool so `rag-check-pdf` loads. It has no shell, no file access, and can't delete or import.
- **Needs the Agents read and Agents write switches on**, so that Claude can save its work.
- **Uses your Claude Code login** (subscription or API key), so it counts against your usage like any session. If the login has expired, the job fails with a message to run `claude` in a terminal and type `/login`.
- **Kept after they finish.** Finished jobs are saved in `data/claude/jobs` (the newest 100). Settings are under `Rag:Claude` ([Configuration.md](Configuration.md#claude-jobs-ragclaude)).

### Switches

The page header has two switches, stored in the database:

- **Agents read**: off turns every tool call into a one-line note saying the library is switched off, and newly connecting clients are told not to use it.
- **Agents write**: off blocks only the tools that change the library; searching and reading still work.

When a client connects, the server also sends **instructions** that are added to the model's system prompt: what the library is for, how to search and add to it, plus each collection's name, size, description and keywords, generated live from the Collections page. They're kept within 2,000 tokens (`Rag:Mcp:ContextTokens`); clients read them when they connect, so edits show up in the next session.

## Managing documents

**Documents page** (http://localhost:5077):

- Pick the target in **Add to collection**, then drop files or whole folders, or use the pickers. Folder uploads keep relative paths as document names and skip dot-folders, `node_modules`, `bin` and `obj`.
- **New collection…** opens the collections page ready to create one. After creating it, you return with the new collection selected.
- Uploading the same name into the same collection replaces the document. Identical content is skipped.
- Each document can be deleted, re-indexed or inspected. Inspecting shows its chunks, the extracted text and the original file.
- **Filter** by collection and name, and sort by any column header (**Clear sort** goes back to collection, then name). The collection, name and Stats drill-down filters are remembered in the browser between visits; **Reset filters** shows everything again. The search boxes have a **✕** button to clear them.
- **Test search** shows the passages a search returns, with an *include muted* box for muted collections.

**Status badges** follow a ready document's status, stacked up to three high before starting another column:

| Badge | Meaning |
|---|---|
| **OCR** (e.g. `OCR 187/194`) | Pages read by local OCR; the tooltip gives the model and date. |
| **Layout** | Pages arranged by layout analysis. |
| **Claude** | Pages whose text Claude checked or transcribed from the page images. |
| **Claudomatics** | Claude chose this document's chunks; links to them on the Claudomatics page. |
| **⬢ Encoded · hex** | A binary stored as a hex dump (see [Tools that change the library](#tools-that-change-the-library)). |
| **Source…** | This document is a conversion of another one. Click for a dialog naming the source and the pages it stands in for. |
| **Conversion…** / **Conversions…** | Some pages of this document are searched through a conversion. Click for a dialog listing each conversion and its pages. |
| **⚠ N problems** / **✓ Verified** / **No problems** | A PDF's page health (see [Page health](#page-health)). Click for the health dialog. |
| **ⓘ** | The document has notes, muted items or a notice from its last run. Click for the health dialog, where they can be muted or unmuted. |

The dialogs can be scrolled and have a **Copy** button.

**Claudomatics page** (http://localhost:5077/claudomatics.html): every chunk Claude chose, grouped by the day it was written and by document, each with its title, kind, location, summary and the start of its text. Each document's group starts collapsed to its header (click to open; **Expand all** and **Collapse all** do every group; while you type a filter, matching groups open). Filter by text or collection. Click a Claudomatic to read it whole, fix its title or summary (it's re-embedded), delete it, or show the PDF page it came from beside it and step through neighbouring pages. Each document can be exported (a Markdown conversion as `.md`, a binary's hex dump as text), downloaded as original bytes if it's an encoded binary, or deleted; when the document also has Claudomatics written on other days, the delete dialog lists them and asks again. The **Claudomatics** badge on the Documents page links there, filtered to that document.

**Conversions page** (http://localhost:5077/conversions.html): every document Claude made from another one (usually Markdown transcribed from a PDF), grouped by the source, with the source pages each conversion covers in search. Download a conversion in full, all of a source's conversions as a zip, or the original source file. The **Source…** and **Conversion…** badges on the Documents page open the same details in a dialog.

**Collections page** (http://localhost:5077/collections.html):

- Create several collections at once, one name per line.
- **Mute** a collection with its **Search** switch to leave it out of search, e.g. manuals for an older model or version that would contradict the ones you're working with. Nothing is deleted or re-indexed, and it takes effect on Claude's next search. A muted collection is still searched whenever it's named: tell Claude *"if you can't find it, try the System 6 Docs collection"* and it passes that name. Claude's instructions and `list_collections` show muted collections as such, search results say which ones were skipped, and **Test search** has an *include muted* box.
- Rename by editing names in the table, then **Save changes**. All edits apply together or not at all, so swapping two names works. A rename carries over to every document in that collection.
- Give each collection a **description** and **keywords**: these are what AI agents are told about the library (see [Connecting Claude Code](#connecting-claude-code)). **Suggest** drafts both with a local model (`Rag:Collections:SuggestModel`, default `qwen3:8b`) from the collection's titles and section headings; nothing is saved until **Save changes**.
- Tick collections and **Remove selected…** to remove them. You choose whether their documents move to `(default)` or are deleted. A move is refused when `(default)` already has a document with the same name.
- `(default)` is the built-in, unnamed collection. It can't be renamed or removed. In the API and MCP tools it is called `default`.

API uploads that name a collection that doesn't exist yet create it automatically.

Supported files: Markdown, plain text, source code (about 60 extensions) and PDFs, up to 500 MB each (`Rag:MaxUploadBytes`). Binary files are rejected.

**Stats page** (http://localhost:5077/stats.html):

- **Totals per collection and overall:** documents, PDF pages, chunks, characters indexed, file and database size.
- **Claude's work:** Claudomatics (with their average size), PDF pages checked by Claude, and chunks left out of search because a conversion covers their pages.
- **Breakdowns:** document types and code languages; where each document's text comes from (original text, PDF embedded, embedded plus layout, OCR, checked by Claude, mixed, no text yet); PDF pages by text source; status.
- **Work remaining:** pages needing OCR, pages not yet laid out, with a rough time estimate; problem pages (not muted or checked); verified pages and documents; pages with an old OCR text layer (re-OCR candidates, not faults); documents with notices (muted notices aren't counted).
- **Other figures:** the largest documents and which OCR models read how many pages.
- **Drill-downs:** almost every number links to the Documents page, filtered to the documents it counts (a banner shows the filter, with **Clear filter**). From there, **Select all shown** plus **Process…** or **Re-index…** fixes what the stats revealed.

## HTTP API

The web UI uses a JSON API under `/api`, on the same port. It has no authentication, which is why the server listens on `localhost` only.

| Endpoint | What it does |
|---|---|
| `GET /api/health` | Database and Ollama status, embedding model and dimensions. |
| `GET /api/documents?collection=&q=` | Every document with its status, counts, badges' data, conversions and covered pages. |
| `POST /api/documents` | Upload (multipart `files`, optional `collection`). |
| `GET /api/documents/{id}` · `DELETE /api/documents/{id}` | One document's details · remove it, its chunks and stored file. |
| `GET /api/documents/{id}/chunks` | Its chunks. |
| `GET /api/documents/{id}/file` | The original file. |
| `GET /api/documents/{id}/export` | The document's full text (`.md` for a Markdown conversion, `.txt` for a PDF's text or a hex dump). |
| `GET /api/documents/{id}/pages/{page}/image` | A PDF page as an image. |
| `POST /api/documents/{id}/reindex` · `POST /api/reindex-all` | Re-index one document · everything (e.g. after changing the embedding model). |
| `POST /api/documents/{id}/ocr` | OCR with `{"scope": "missing" \| "suspect" \| "problems" \| "all"}`. |
| `POST /api/documents/{id}/ocr/cancel` · `POST /api/documents/{id}/revert-ocr` | Cancel a run · go back to the PDF's embedded text. |
| `POST /api/documents/{id}/layout` | Layout analysis (`{"redo": true}` re-analyzes pages done before). |
| `POST /api/batch` | Queue OCR and/or layout for many PDFs (`documentIds` or `collection`; `dryRun` only previews). |
| `GET /api/queue` · `POST /api/queue/cancel` | The processing queue · cancel all of it. |
| `GET /api/search?q=&k=&collection=&includeMuted=` | Hybrid search, as **Test search** shows it. |
| `GET /api/collections` and `POST /api/collections`, `/collections/rename`, `/collections/remove`, `/collections/details`, `/collections/suggest`, `/collections/mute` | List, create, rename, remove, describe, draft a description, mute. |
| `GET /api/claudomatics?q=&collection=&limit=` | Claudomatics with snippets, for the Claudomatics page. |
| `GET /api/chunks/{id}` · `PUT /api/chunks/{id}` · `DELETE /api/chunks/{id}` | One chunk · change its title or summary (re-embedded) · delete it. |
| `GET /api/documents/{id}/claudomatic-dates` | When each of a document's Claudomatics was written (the delete dialog uses it to warn about other days). |
| `GET /api/conversions` · `GET /api/conversions/{sourceId}/zip` | Conversions grouped by source · all of one source's conversions as a zip. |
| `GET /api/documents/{id}/health` | A PDF's page health: counts per state, the pages with problems, notes, mutes or blanks, the document's verification and its notice. |
| `POST /api/documents/{id}/pages/mute` · `/pages/verify` | `{"pages": [17, 31], "muted": true}` mutes (or unmutes) those pages' problems or notes · `{"pages": […], "verified": true}` marks their text correct. No pages means every page. |
| `POST /api/documents/{id}/verify` · `/notice/mute` | `{"verified": true, "note": "…"}` marks the whole document correct (or clears it) · `{"muted": true}` mutes its notice. |
| `GET /api/claude` | Whether Claude jobs can run (command found, agents may write). |
| `POST /api/documents/{id}/claude` | Start a Claude job: `{"task": "problems" \| "pages" \| "review" \| "sample" \| "custom", "pages": "12, 40-45", "instructions": "…"}`. |
| `GET /api/claude/jobs?documentId=` · `GET /api/claude/jobs/{id}` · `/prompt` · `POST …/cancel` | Jobs, newest first · one job with its activity log · the prompt it was given · stop it. |
| `GET /api/stats` | Everything the Stats page shows. |
| `GET /api/ocr` · `GET /api/layout` | Whether OCR and the layout service are available. |
| `GET /api/settings/agents` · `PUT /api/settings/agents` | The **Agents read** / **Agents write** switches (`{"enabled": false}`, `{"writes": false}`). |

## OCR and layout analysis for PDFs

PDFs go through two optional, slower steps on top of normal indexing. Both run in the background analysis lane, one page at a time, and suit overnight batches.

- **Layout analysis** uses the layout service (`layout-service/`, PaddleOCR PP-StructureV3, run by `docker compose`). It finds each page's orientation, its regions (titles, text, tables, figures, captions, headers and footers) and their reading order.
- **OCR** reads pages without text with a vision model on the Ollama server (default `glm-ocr`; set `Rag:Ocr:Model` to change it).

How pages are read:

| Page | With the layout service | Without it |
|---|---|---|
| Scanned (no text) | Turned upright; each region read by glm-ocr with a prompt suited to it (text, table, formula) | Whole page read by glm-ocr |
| Digital (has text) | The PDF's own words placed into the regions: no re-OCR, so no new errors | The PDF's text in its stored order |

Claude's text, when it has checked a page (see `set_page_text`), takes precedence over all of these.

Either way, running headers, footers and page numbers are dropped (only when they're short, so a long note at the foot of a page is kept). Headings become Markdown and carry over between pages as section breadcrumbs. Tables become Markdown tables, and text inside figures (diagram labels) is kept as figure text. If glm-ocr fails on one region, that region uses the layout service's own reading. Each page records what went wrong in its last run, and that feeds its health (below). glm-ocr's failures repeat on a re-run, since it reads the same image the same way, so having Claude check those pages is usually the fix.

For a page with its own text, the PDF's letters show which way the text runs. When it runs upright, the layout service isn't asked to guess the page's orientation: its classifier mistakes some figure-heavy pages for upside down.

### Page health

Every PDF page has one state, so a document whose pages are fine can be seen to be fine:

| State | Meaning |
|---|---|
| **Problem** | The text is probably wrong or missing: glm-ocr failed on a region, layout failed on a scan, a page with content has no text, or the text is garbled. |
| **Note** | Something was skipped or unusual, but the text is intact (e.g. layout skipped on a page the PDF itself rotates). |
| **Muted** | A problem or note you set aside in the health dialog. A new run that records a new issue unmutes it. |
| **Blank** | No text, and the page image is blank too (checked from a small rendering, ignoring dark scan edges and bindings). |
| **Verified** | Claude checked it against the image (`set_page_text`), or you marked it correct. Your mark lapses if the page's text changes. |
| **Unverified** | Machine-read text nobody has checked, with nothing pointing to a fault. That's normal. |

A document's badge shows **⚠ N problems** while any page is a problem, **✓ Verified** when every page with text is verified or the whole document was verified, and **No problems** otherwise. A PDF that came with its own OCR text layer isn't a problem because of that; check a sample instead. In the health dialog you can **Mark document correct**, or have Claude check a sample of pages and call `verify_document` (**Check a sample with Claude**). Either way it's cleared when an OCR or layout run, or a new upload, changes the text.

The **health dialog** opens from the badge or the **ⓘ**. It shows the counts for each state, the document's verification, and the notice from the last run (with **Mute**). It lists problem pages (each with **Fix with Claude**, **Mute**, **Mark correct** and a link to the page image), notes (**Mute**), muted items (**Unmute**) and blank pages; each list has a bulk button too. None of these change the text, so nothing is re-indexed. Claude sees the same states in `list_documents`, `get_document` and `list_pages`.

**In the UI:**
- **Status badges.** **OCR** marks pages read by local OCR, and **Layout** marks pages arranged by layout analysis. Their tooltips give counts, model and date. MCP results mark OCR'd passages too.
- **Scans** stop at **needs OCR** after upload. **Run OCR** reads them.
- **Re-index… on a PDF** offers:
  - *re-index*;
  - *replace text with local OCR* (pages with problems in the last run, pages without text, suspected poor old OCR, or all pages; pages Claude has checked keep Claude's text);
  - *analyze layout* (optionally re-analyzing pages done before);
  - *revert to embedded text*.
- **Process…** queues many PDFs at once: the ticked documents, or every PDF in the current collection filter. Choose OCR, layout or both. A preview shows how many documents and pages are involved and a rough duration before you queue.
- **The processing queue panel** shows the running job, what's queued, and **Cancel all**. Cancelling keeps finished pages.
- **Nothing is thrown away.** Each page keeps its embedded text, OCR text and layout text side by side, so results can be reverted or re-run (e.g. with a better model). Work interrupted by a restart resumes where it stopped.

**Timing on this Mac:** layout takes about 25–40 seconds per page. glm-ocr on `tb` adds a few seconds per region on scans.

**Settings** are under `Rag:Ocr` and `Rag:Layout` (see [Configuration.md](Configuration.md#ocr-ragocr)). Docker needs enough memory for the layout service, about 4–6 GB; 16 GB for Docker Desktop is comfortable.

"Old OCR text layer" (invisible text over a scanned image, or OCR software named in the PDF metadata) only marks pages as candidates for *replace text with local OCR*; it never triggers OCR by itself and isn't a problem. Garbled text is, and it's checked first. Ideas for later are in [Future Plans.md](Future%20Plans.md).

## How it works

- **Chunking** (`Ingestion/Chunker.cs`): chunks target about 1,200 characters, with a hard cap of 1,800 and about 150 characters of overlap.
  - Markdown splits on sections and paragraphs and keeps a heading breadcrumb. Fenced code blocks stay intact.
  - Code splits on blank-line groups and records line ranges.
  - PDFs are chunked per page.
  - Documents Claude adds with `add_document` keep its Claudomatics instead, and re-indexing keeps them.
  - Tiny sections merge with their neighbours instead of becoming useless one-line chunks.
- **Embedding** (`Embeddings/OllamaEmbedder.cs`): calls `POST /api/embed` in batches of 32. Queries get the task instruction qwen3-embedding expects. Each chunk is embedded with a header first (file name, language, section, page, and for Claude's chunks the title, symbol and summary), then its text.
- **Search** (`Search/SearchService.cs`): runs pgvector cosine search (HNSW index) and Postgres full-text search (`websearch_to_tsquery`) and merges them with reciprocal rank fusion. Exact identifiers such as `CB-12` or class names are found even when embeddings miss them. Muted collections are skipped unless named, and PDF pages covered by a conversion are skipped so the same text isn't found twice. MCP results then join neighbouring server-made chunks into passages, drop near-duplicates from other editions, and excerpt long passages around the question's words.
- **Ingestion** runs in two independent lanes, each handling one job at a time: indexing (uploads and re-indexes) and OCR. Uploads finish within seconds even during a long OCR run. Uploading a new version of a document while it's being OCR'd cancels that run and indexes the new file. Search keeps working throughout, and a document being OCR'd stays searchable with its previous text.
- **Extracted text** is cleaned before storage: NUL characters, stray control characters and broken surrogates are removed, because Postgres rejects them.

Settings are in `src/Ragamuffin/appsettings.json` under `Rag`; every one is listed in [Configuration.md](Configuration.md). Switching to an embedding model with a different dimension needs a new migration, because the column is `vector(1024)`.

## Development

Test against the separate `rag_dev` database, so a dev instance doesn't pick up jobs queued in the live one:

```bash
ConnectionStrings__Rag="Host=localhost;Port=5432;Database=rag_dev;Username=rag;Password=rag" Rag__FilesDirectory=/tmp/rag-dev-files dotnet run --project src/Ragamuffin --urls http://localhost:5078
```

```bash
dotnet test                                                       # unit tests (chunking, extraction, layout, search merging, Claudomatics)
dotnet ef migrations add <Name> -p src/Ragamuffin -o Data/Migrations
```

## License

Licensed under the [Apache License, Version 2.0](LICENSE).

Third-party components keep their own licenses. That includes the NuGet packages, PaddleOCR and its models in the layout service, and the Ollama models it uses (`qwen3-embedding`, `glm-ocr`, `qwen3`), which are downloaded separately and not part of this repository.
