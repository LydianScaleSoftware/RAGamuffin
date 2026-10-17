# RAGamuffin - A RAG server for opencode

A small local retrieval service for opencode. You add documents in a web UI. They are chunked, embedded with `nomic-embed-text` on the Ollama box (`tb`, 10.10.10.2), and stored in Postgres with pgvector. opencode queries them through MCP over HTTP.

```
web UI ──upload──▶ RAGamuffin ──/api/embed──▶ Ollama (tb) nomic-embed-text
                     │  ▲
                     ▼  │ hybrid search (vector + full-text, RRF)
              Postgres 18 + pgvector (Docker)
                        ▲
opencode ──MCP /mcp─────┘
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

MCP clients are optional: opencode, Claude Code, or anything that speaks MCP over HTTP.

### Ollama models

| Model | Needed for | Size on disk | Required? | Setting |
|---|---|---|---|---|
| `nomic-embed-text` | Embedding every chunk and every search query | 0.3 GB | **Yes** | `Rag:EmbeddingModel` |
| `glm-ocr` | OCR of scanned PDF pages | 2.7 GB | Only for OCR | `Rag:Ocr:Model` |
| `qwen3:8b`, or any chat model that can answer in JSON | **Suggest** on the Collections page | 5.2 GB | Only for Suggest | `Rag:Collections:SuggestModel` |

```bash
ollama pull nomic-embed-text
ollama pull glm-ocr
ollama pull qwen3:8b      # optional, for Suggest
```

Suggest only writes a sentence or two and a keyword list, so a small model is plenty; any instruction-following chat model works.

Without `glm-ocr`, scanned PDFs stay at **needs OCR** and **Run OCR** is disabled with an explanation. Without the Suggest model, **Suggest** reports an error, and descriptions can still be typed by hand.

### Ollama server

- **Reachable over the network** when it runs on another machine. On that machine, set `OLLAMA_HOST=0.0.0.0` so Ollama listens beyond localhost. Point `Rag:OllamaUrl` at it (here `http://10.10.10.2:11434`).
- **Room for two or three models at once.** During OCR batches the indexing lane embeds with `nomic-embed-text` while the analysis lane OCRs with `glm-ocr`. If a coding model is also loaded for opencode, that's three. Set `OLLAMA_MAX_LOADED_MODELS` to at least 2, or 3 to keep the coding model loaded, and make sure memory allows it. On `tb` (32 GB), for example: a 35B coding model for opencode ≈ 22 GB, `glm-ocr` ≈ 3–4 GB with its 16K context, `nomic-embed-text` ≈ 0.3 GB, and `qwen3:8b` ≈ 5–6 GB while Suggest runs.
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

## Connecting agents (opencode, Claude Code)

**opencode** (V2 configuration): add this to `~/.config/opencode/opencode.json`:

```json
"mcp": {
  "servers": {
    "rag": { "type": "remote", "url": "http://localhost:5077/mcp", "oauth": false }
  }
}
```

**Claude Code:**

```bash
claude mcp add --transport http --scope user rag http://localhost:5077/mcp
```

Tools exposed (all read-only):

- `search_documents(query, top_k=8, collection?)`: ranked passages with file, line range or page, and section.
- `list_documents(collection?)`
- `get_document(name_or_id, collection?)`: full extracted text, truncated above 60k characters.
- `list_collections(collection?)`: what each collection covers (description, keywords), its size and example titles.

When a client connects, the server also sends **instructions** that clients add to the model's system prompt: when to use the tools, plus each collection's name, size, description and keywords. They're generated live from the Collections page, so the agent knows what the library holds without notes in `AGENTS.md` or `CLAUDE.md`. Both the instructions and the `list_collections` answer are kept within 512 tokens (`Rag:Mcp:ContextTokens`). Detail is trimmed first (keywords, then description length), and only then are the smallest collections summarized as "…and N more". Clients read instructions when they connect, so edits show up in the next session.

## Managing documents

**Documents page** (http://localhost:5077):

- Pick the target in **Add to collection**, then drop files or whole folders, or use the pickers. Folder uploads keep relative paths as document names and skip dot-folders, `node_modules`, `bin` and `obj`.
- **New collection…** opens the collections page ready to create one. After creating it, you return with the new collection selected.
- Uploading the same name into the same collection replaces the document. Identical content is skipped.
- Each document can be deleted, re-indexed or inspected. Inspecting shows its chunks, the extracted text and the original file.
- **Test search** shows exactly what opencode will get back.

**Collections page** (http://localhost:5077/collections.html):

- Create several collections at once, one name per line.
- Rename by editing names in the table, then **Save changes**. All edits apply together or not at all, so swapping two names works. A rename carries over to every document in that collection.
- Give each collection a **description** and **keywords**: these are what AI agents are told about the library (see [Connecting agents](#connecting-agents-opencode-claude-code)). **Suggest** drafts both with a local model (`Rag:Collections:SuggestModel`, default `qwen3:8b`) from the collection's titles and section headings; nothing is saved until **Save changes**.
- Tick collections and **Remove selected…** to remove them. You choose whether their documents move to `(default)` or are deleted. A move is refused when `(default)` already has a document with the same name.
- `(default)` is the built-in, unnamed collection. It can't be renamed or removed. In the API and MCP tools it is called `default`.

API uploads that name a collection that doesn't exist yet create it automatically.

Supported files: Markdown, plain text, source code (about 60 extensions) and PDFs, up to 500 MB each (`Rag:MaxUploadBytes`). Binary files are rejected.

**Stats page** (http://localhost:5077/stats.html):

- **Totals per collection and overall:** documents, PDF pages, chunks, characters indexed, file and database size.
- **Breakdowns:** document types and code languages; where each document's text comes from (original text, PDF embedded, embedded plus layout, OCR, mixed, no text yet); PDF pages by text source; status.
- **Work remaining:** pages needing OCR, pages not yet laid out, with a rough time estimate; pages that look like poor old OCR; documents with notices.
- **Other figures:** the largest documents and which OCR models read how many pages.
- **Drill-downs:** almost every number links to the Documents page, filtered to the documents it counts (a banner shows the filter, with **Clear filter**). From there, **Select all shown** plus **Process…** or **Re-index…** fixes what the stats revealed.

## OCR and layout analysis for PDFs

PDFs go through two optional, slower steps on top of normal indexing. Both run in the background analysis lane, one page at a time, and suit overnight batches.

- **Layout analysis** uses the layout service (`layout-service/`, PaddleOCR PP-StructureV3, run by `docker compose`). It finds each page's orientation, its regions (titles, text, tables, figures, captions, headers and footers) and their reading order.
- **OCR** reads pages without text with a vision model on the Ollama server (default `glm-ocr`; set `Rag:Ocr:Model` to change it).

How pages are read:

| Page | With the layout service | Without it |
|---|---|---|
| Scanned (no text) | Turned upright; each region read by glm-ocr with a prompt suited to it (text, table, formula) | Whole page read by glm-ocr |
| Digital (has text) | The PDF's own words placed into the regions: no re-OCR, so no new errors | The PDF's text in its stored order |

Either way, running headers, footers and page numbers are dropped (only when they're short, so a long note at the foot of a page is kept). Headings become Markdown and carry over between pages as section breadcrumbs. Tables become Markdown tables, and text inside figures (diagram labels) is kept as figure text. If glm-ocr fails on one region, that region uses the layout service's own reading, and the job notice says so.

**In the UI:**
- **Status badges.** **OCR** marks pages read by local OCR, and **Layout** marks pages arranged by layout analysis. Their tooltips give counts, model and date. MCP results mark OCR'd passages too.
- **Scans** stop at **needs OCR** after upload. **Run OCR** reads them.
- **Re-index… on a PDF** offers:
  - *re-index*;
  - *replace text with local OCR* (pages without text, suspected poor old OCR, or all pages);
  - *analyze layout* (optionally re-analyzing pages done before);
  - *revert to embedded text*.
- **Process…** queues many PDFs at once: the ticked documents, or every PDF in the current collection filter. Choose OCR, layout or both. A preview shows how many documents and pages are involved and a rough duration before you queue.
- **The processing queue panel** shows the running job, what's queued, and **Cancel all**. Cancelling keeps finished pages.
- **Nothing is thrown away.** Each page keeps its embedded text, OCR text and layout text side by side, so results can be reverted or re-run (e.g. with a better model). Work interrupted by a restart resumes where it stopped.

**Timing on this Mac:** layout takes about 25–40 seconds per page. glm-ocr on `tb` adds a few seconds per region on scans.

**Settings** are under `Rag:Ocr` and `Rag:Layout` (see [Configuration.md](Configuration.md#ocr-ragocr)). Docker needs enough memory for the layout service, about 4–6 GB; 16 GB for Docker Desktop is comfortable.

"Looks like poor OCR" is a heuristic: invisible text over a scanned image, OCR software named in the PDF metadata, or garbled words. It only ranks pages for the dialog and never triggers OCR by itself. Ideas for later are in [Future Plans.md](Future%20Plans.md).

## How it works

- **Chunking** (`Ingestion/Chunker.cs`): chunks target about 1,200 characters, with a hard cap of 1,800 and about 150 characters of overlap.
  - Markdown splits on sections and paragraphs and keeps a heading breadcrumb. Fenced code blocks stay intact.
  - Code splits on blank-line groups and records line ranges.
  - PDFs are chunked per page.
  - Tiny sections merge with their neighbours instead of becoming useless one-line chunks.
- **Embedding** (`Embeddings/OllamaEmbedder.cs`): calls `POST /api/embed` in batches of 32. It uses the `search_document:` and `search_query:` prefixes that nomic-embed-text expects. A short header (file name, language, section or page) is embedded with each chunk.
- **Search** (`Search/SearchService.cs`): runs pgvector cosine search (HNSW index) and Postgres full-text search (`websearch_to_tsquery`) and merges them with reciprocal rank fusion. Exact identifiers such as `CB-12` or class names are found even when embeddings miss them.
- **Ingestion** runs in two independent lanes, each handling one job at a time: indexing (uploads and re-indexes) and OCR. Uploads finish within seconds even during a long OCR run. Uploading a new version of a document while it's being OCR'd cancels that run and indexes the new file. Search keeps working throughout, and a document being OCR'd stays searchable with its previous text.
- **Extracted text** is cleaned before storage: NUL characters, stray control characters and broken surrogates are removed, because Postgres rejects them.

Settings are in `src/Ragamuffin/appsettings.json` under `Rag`; every one is listed in [Configuration.md](Configuration.md). Switching to an embedding model with a different dimension needs a new migration, because the column is `vector(768)`.

## Development

Test against the separate `rag_dev` database, so a dev instance doesn't pick up jobs queued in the live one:

```bash
ConnectionStrings__Rag="Host=localhost;Port=5432;Database=rag_dev;Username=rag;Password=rag" Rag__FilesDirectory=/tmp/rag-dev-files dotnet run --project src/Ragamuffin --urls http://localhost:5078
```

```bash
dotnet test                                                       # chunker, extractor, fusion tests
dotnet ef migrations add <Name> -p src/Ragamuffin -o Data/Migrations
```

## License

Licensed under the [Apache License, Version 2.0](LICENSE).

Third-party components keep their own licenses. That includes the NuGet packages, PaddleOCR and its models in the layout service, and the Ollama models it uses (`nomic-embed-text`, `glm-ocr`), which are downloaded separately and not part of this repository.
