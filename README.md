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
| Uploaded originals | `data/files/` |

## Running

```bash
docker compose up -d                 # Postgres + pgvector (restarts with Docker Desktop)
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

## OCR for scanned PDFs

PDF pages without text can be read by a vision model on the Ollama server (default `glm-ocr`; set `Rag:Ocr:Model` to change it).

- **Scanned PDFs** stop at **needs OCR** after upload. Their **Run OCR** button reads the pages without text.
- **OCR'd documents** show an **OCR** badge in the status column. A partly OCR'd PDF shows a count such as `OCR 12/40`. The badge's tooltip gives the model, page count and date. MCP results mark OCR'd passages too, so opencode knows they may contain recognition errors.
- **Re-index… on a PDF** opens a dialog:
  - *Re-index* from the PDF's embedded text. OCR'd pages keep their OCR text.
  - *Replace text with local OCR*, for pages without text, pages whose embedded text looks like poor old OCR, or all pages.
  - *Revert to embedded text*, shown when OCR'd pages have embedded text to go back to.
- **While OCR runs**, the status shows page progress and a **Cancel OCR** button. Cancelling keeps the pages already finished.
- **Restarts don't lose work.** Each page is saved as soon as it's read, and an OCR run interrupted by a restart or reboot resumes where it stopped.
- **Embedded text is never thrown away.** Each page stores its embedded and OCR text side by side, so OCR can be reverted or re-run, for example with a better model later.

"Looks like poor OCR" is a heuristic: invisible text over a scanned image, OCR software named in the PDF metadata, or garbled words. It only ranks pages for the dialog and never triggers OCR by itself.

OCR runs one page at a time, at roughly 1–15 seconds a page on `tb`. Pages are trimmed to their content before being sent, because glm-ocr can skip isolated text (such as a title) on a mostly blank page. Settings are under `Rag:Ocr` in `appsettings.json`. Ideas for later are in [Future Plans.md](Future%20Plans.md).

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
