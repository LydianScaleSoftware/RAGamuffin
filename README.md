# RAG server for opencode

A small local retrieval service for opencode. You add documents in a web UI. They are chunked, embedded with `nomic-embed-text` on the Ollama box (`tb`, 10.10.10.2), and stored in Postgres with pgvector. opencode queries them through MCP over HTTP.

```
web UI ──upload──▶ RagServer ──/api/embed──▶ Ollama (tb) nomic-embed-text
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
| Database | Docker container `rag-postgres`, `localhost:5432`, db/user/password `rag` |
| Layout service | Docker container `rag-layout`, `localhost:5002` (first start downloads its models, a few minutes) |
| Uploaded originals | `data/files/` |

## Running

```bash
docker compose up -d                 # Postgres + pgvector, and the layout service (both restart with Docker Desktop)
dotnet run --project src/RagServer   # applies migrations on startup
```

To run it permanently as a launchd agent that starts at login, use `scripts/install-service.sh`. It publishes to `publish/` and logs to `data/logs/ragserver.log`. Re-run the script after code changes. `scripts/uninstall-service.sh` removes the agent. Docker Desktop must be set to start at login for the database to come back after a reboot.

## opencode

Add this to `~/.config/opencode/opencode.json`:

```json
"mcp": {
  "rag": { "type": "remote", "url": "http://localhost:5077/mcp", "enabled": true }
}
```

Tools exposed (all read-only):

- `search_documents(query, top_k=8, collection?)`: ranked passages with file, line range or page, and section.
- `list_documents(collection?)`
- `get_document(name_or_id, collection?)`: full extracted text, truncated above 60k characters.

## Managing documents

**Documents page** (http://localhost:5077):

- Pick the target in **Add to collection**, then drop files or whole folders, or use the pickers. Folder uploads keep relative paths as document names and skip dot-folders, `node_modules`, `bin` and `obj`.
- **New collection…** opens the collections page ready to create one. After creating it, you return with the new collection selected.
- Uploading the same name into the same collection replaces the document. Identical content is skipped.
- Each document can be deleted, re-indexed or inspected. Inspecting shows its chunks, the extracted text and the original file.
- **Test search** shows exactly what opencode will get back.

**Collections page** (http://localhost:5077/collections.html):

- Create several collections at once, one name per line.
- Rename by editing names in the table, then **Save renames**. All edits apply together or not at all, so swapping two names works. A rename carries over to every document in that collection.
- Tick collections and **Remove selected…** to remove them. You choose whether their documents move to `(default)` or are deleted. A move is refused when `(default)` already has a document with the same name.
- `(default)` is the built-in, unnamed collection. It can't be renamed or removed. In the API and MCP tools it is called `default`.

API uploads that name a collection that doesn't exist yet create it automatically.

Supported files: Markdown, plain text, source code (about 60 extensions) and PDFs. Binary files are rejected.

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

**Settings** are under `Rag:Ocr` and `Rag:Layout` in `appsettings.json`. Docker needs enough memory for the layout service, about 4–6 GB; 16 GB for Docker Desktop is comfortable.

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

Settings are in `src/RagServer/appsettings.json` under `Rag`: Ollama URL, model, chunk sizes and the files directory. Switching to an embedding model with a different dimension needs a new migration, because the column is `vector(768)`.

## Development

Test against the separate `rag_dev` database, so a dev instance doesn't pick up jobs queued in the live one:

```bash
ConnectionStrings__Rag="Host=localhost;Port=5432;Database=rag_dev;Username=rag;Password=rag" Rag__FilesDirectory=/tmp/rag-dev-files dotnet run --project src/RagServer --urls http://localhost:5078
```

```bash
dotnet test                                                       # chunker, extractor, fusion tests
dotnet ef migrations add <Name> -p src/RagServer -o Data/Migrations
```
