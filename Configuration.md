# RAGamuffin configuration reference

Every setting RAGamuffin reads, with its default and what it changes. For what to install first, see [Requirements](README.md#requirements) in the README.

## Where settings come from

Settings are standard ASP.NET Core configuration. Later sources override earlier ones:

1. `src/Ragamuffin/appsettings.json`, the checked-in defaults (copied into `publish/` when the service is installed).
2. `src/Ragamuffin/appsettings.{Environment}.json`. The installed service runs as `Production`; `dotnet run` runs as `Development`.
3. Environment variables, with `__` between levels: `Rag__OllamaUrl`, `Rag__Ocr__Model`, `ConnectionStrings__Rag`.
4. Command-line arguments when running by hand: `dotnet run --project src/Ragamuffin -- --Rag:Ocr:Model=qwen3.8:27b`.

Settings are read at startup. After changing a file, run `scripts/install-service.sh` again (it re-publishes and restarts the service).

**Keeping machine-specific values out of git:** put them in `src/Ragamuffin/appsettings.Production.json` and add that file to `.gitignore`. It overrides `appsettings.json` for the installed service only.

In the tables below, *default* is the value built into the code; where the checked-in `appsettings.json` sets something different for this machine, that's noted.

## Server and storage

| Setting | Default | What it does |
|---|---|---|
| `Urls` | `http://localhost:5077` (from `appsettings.json`) | Address and port the web UI, API and MCP endpoint listen on. Keep it on `localhost` unless you add authentication. |
| `ConnectionStrings:Rag` | `Host=localhost;Port=5432;Database=rag;Username=rag;Password=rag` | Postgres connection. Must match the credentials in `docker-compose.yml`. |
| `Rag:FilesDirectory` | `data/files` (`appsettings.json`: `../../data/files`) | Where uploaded originals are stored, relative to the app's folder. The installed service sets it to the absolute `data/files` path through its environment. |
| `Rag:MaxUploadBytes` | `524288000` (500 MB) | Largest file accepted per upload. A request may carry up to ten times this, since the UI sends files in batches of ten. |
| `Logging:LogLevel:*` | `Information`; framework and HTTP client noise at `Warning` | Standard ASP.NET Core log levels. The service logs to `data/logs/ragamuffin.log`. |

## Embeddings

| Setting | Default | What it does |
|---|---|---|
| `Rag:OllamaUrl` | `http://10.10.10.2:11434` | Ollama server used for embeddings, OCR, Suggest and `ask_library`. |
| `Rag:EmbeddingModel` | `qwen3-embedding:0.6b` | Model that embeds chunks and search queries. It must produce **1,024-dimensional** vectors (see [Changing models](#changing-models)). |
| `Rag:EmbeddingBatchSize` | `32` | Chunks sent per `/api/embed` request. |
| `Rag:DocumentPrefix` | *(empty)* | Text put before every chunk when embedding. qwen3-embedding takes documents as they are (nomic-embed-text wanted `search_document: `). |
| `Rag:QueryPrefix` | `Instruct: Given a question about the user's documents, retrieve the passages that answer it` + newline + `Query: ` | Text put before every search query when embedding: the task instruction qwen3-embedding is trained with (nomic-embed-text wanted `search_query: `). Naming the library's subject helps: `appsettings.json` here uses "…a question about programming the Apple IIgs, retrieve the passage from a technical manual…", which found the right passage in the top five for 23 of 30 test questions against 21 with the generic wording. Changing it needs no re-index; documents are embedded without a prefix. |
| `Rag:EmbeddingMaxChars` | `60000` | Longest text embedded per chunk: the header, title and summary, then the chunk's text cut to fit. Enough for the largest chunk Claude may define; qwen3-embedding reads 32,768 tokens. |

## Chunking

| Setting | Default | What it does |
|---|---|---|
| `Rag:ChunkTargetChars` | `1200` | Size chunks aim for. |
| `Rag:ChunkMaxChars` | `1800` | Hard cap on a chunk's size. |
| `Rag:ChunkOverlapChars` | `150` | Trailing text carried into the next chunk so passages aren't cut off mid-thought. |

These apply to the server's own chunking; documents Claude adds as Claudomatics aren't affected. Changes apply to documents indexed afterwards; re-index existing documents to re-chunk them (`POST /api/reindex-all` re-indexes everything).

## OCR (`Rag:Ocr`)

OCR reads PDF pages that have no text, using a vision model on the Ollama server. With the layout service available, each region of a page is read separately (see [Layout](#layout-raglayout)); otherwise the whole page is sent at once.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Turns OCR off entirely: **Run OCR** is disabled and scanned PDFs stay at *needs OCR*. |
| `Model` | `glm-ocr` | Ollama vision model. Each page records which model read it, so switching later is safe. |
| `Prompt` | `Text Recognition:` | Instruction sent with whole-page images. glm-ocr expects this exact task prompt. (Region OCR picks its own prompt per region: `Text Recognition:`, `Table Recognition:` or `Formula Recognition:`.) |
| `Dpi` | `150` | Rendering resolution for **whole-page** OCR only. |
| `MaxImageEdge` | `1800` | Whole-page images are scaled down so their longer side fits this many pixels, after blank margins are trimmed. |
| `NumCtx` | `16384` | Context size requested from Ollama for OCR calls (image plus output). |
| `MaxOutputTokens` | `4096` | Cap on text generated per image. Output cut off by the cap is treated as a failed read, because it's almost always the model repeating itself. A failed region falls back to the layout service's own reading. |
| `PageTimeoutSeconds` | `300` | Time limit per OCR call (a page, or one region of it). |
| `KeepAlive` | `5m` | How long Ollama keeps the OCR model loaded after the last request. |
| `Think` | *(unset)* | Sent as Ollama's `think` flag when set. Set it to `false` for reasoning models such as `qwen3.8:27b`, so they answer directly. |
| `MaxPages` | `2000` | Refuses analysis jobs (OCR plus layout) that would cover more pages of one document than this. A safety cap against accidents. |
| `AutoOnImport` | `false` | When `true`, PDFs with pages lacking text are OCR'd straight after upload instead of waiting at *needs OCR*. |

## Layout (`Rag:Layout`)

The layout service (`layout-service/`, PaddleOCR PP-StructureV3 in Docker) finds each page's orientation, its regions (titles, text, tables, figures, captions, headers and footers) and their reading order.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Turns layout analysis off. OCR then falls back to whole pages, and digital PDFs keep their plain embedded text. |
| `Url` | `http://localhost:5002` | Where the layout service listens. Change it if you run the service on another machine (for example `tb` with a GPU). |
| `Dpi` | `200` | Rendering resolution for layout analysis and region OCR. Higher reads small print better; region crops come from this full-resolution render. |
| `MaxImageEdge` | `2000` | The copy sent to the layout service is scaled down to this longer side. Its models resize internally anyway, and full-size pages can exhaust the container's memory. Region boxes are scaled back up, so OCR accuracy is unaffected. |
| `PageTimeoutSeconds` | `900` | Time limit for analyzing one page. |
| `IncludeFigureText` | `true` | Keeps text inside figures (diagram labels) as `[Figure text: …]`. |
| `AutoOnImport` | `false` | When `true`, PDFs with embedded text get layout analysis queued straight after upload. |

## Collections (`Rag:Collections`)

| Setting | Default | What it does |
|---|---|---|
| `SuggestModel` | `qwen3:8b` | Ollama chat model that drafts collection descriptions and keywords for **Suggest**. It must be able to answer in JSON; small models are fine. Thinking is switched off for these requests. |
| `SuggestTimeoutSeconds` | `180` | Time limit for one suggestion, including loading the model if it isn't loaded yet. |

## MCP (`Rag:Mcp`)

Token budgets are estimated at about 3.5 characters per token and sized for Claude's large context.

| Setting | Default | What it does |
|---|---|---|
| `ContextTokens` | `2000` | Token budget for each block of text an agent receives about the library: the server instructions sent when a client connects (added to its system prompt), and the `list_collections` answer. When text doesn't fit, keywords and descriptions are shortened first, then the smallest collections are summarized as "…and N more". |
| `DefaultResults` | `8` | Results `search_documents` returns when the agent doesn't pass `top_k` (it may ask for up to 25). |
| `SearchTokens` | `12000` | Budget for one `search_documents` answer. Higher-ranked results get a larger share; a passage longer than its share is excerpted around the query's words (`get_chunk` returns it whole). |
| `DocumentTokens` | `25000` | Budget for one `get_document` or `get_chunk` answer. Longer reads are cut with a hint for where to continue. |
| `ListTokens` | `6000` | Budget for one `list_documents` answer; the rest is summarized as "…and N more". |
| `ImportRoots` | *(empty: your home folder)* | Folders `import_file` and `add_document` may read files from, as a list: `["/Users/me/Manuals", "/Users/me/src"]`. Paths outside them are refused. |
| `MaxBinaryBytes` | `1048576` (1 MB) | Largest binary file `import_file` and `add_document` accept; it's stored as a hex dump (about 4.9 times its size as text) plus the original bytes. The web upload rejects binaries. |
| `MaxChunkChars` | `60000` | Largest chunk Claude may define with `add_document` or `set_chunks`. |
| `AnswerModel` | *(empty)* | Ollama chat model for the optional `ask_library` tool, which writes a short answer from retrieved passages. Empty leaves the tool out, since Claude reads the passages itself. Set it (e.g. `qwen3:8b`) for smaller agents. |
| `AnswerContextTokens` | `3000` | How much retrieved text the answer model is given (up to six passages, excerpted to fit). |
| `AnswerMaxTokens` | `700` | Cap on the length of an answer. The model's context is set to fit both, between 4,096 and 32,768 tokens. |
| `AnswerTimeoutSeconds` | `180` | Time limit for one answer, including loading the model. On timeout or any error, `ask_library` returns search results with a note. |

The **Agents read** and **Agents write** switches in the web UI's header aren't configuration settings: they're stored in the database (`settings` table, keys `mcp.enabled` and `mcp.write`) and changed from the UI or with `PUT /api/settings/agents` and `{"enabled": false}` or `{"writes": false}`.

## Docker services (`docker-compose.yml`)

| Item | Value | Notes |
|---|---|---|
| Compose project | `ragamuffin` | |
| Postgres | container `ragamuffin-postgres`, image `pgvector/pgvector:pg18`, port `127.0.0.1:5432` | `POSTGRES_USER`, `POSTGRES_PASSWORD` and `POSTGRES_DB` (all `rag`) must match `ConnectionStrings:Rag`. Data in volume `ragamuffin_pgdata`. |
| Layout service | container `ragamuffin-layout`, built from `layout-service/`, port `127.0.0.1:5002` | Models (about 1.8 GB) download on first start into volume `ragamuffin_layout-models`. `LAYOUT_DEVICE` selects `cpu` (default) or `gpu`; GPU use also needs a GPU build of PaddlePaddle in the image and a GPU-enabled Docker host. |

Both containers have `restart: unless-stopped`, so they come back with Docker Desktop. The ports are bound to `127.0.0.1` only.

## Background service (`scripts/install-service.sh`)

| Item | Value |
|---|---|
| launchd label | `com.lydianscale.ragamuffin` (`~/Library/LaunchAgents/com.lydianscale.ragamuffin.plist`) |
| Program | `dotnet publish/Ragamuffin.dll`, working directory `publish/` |
| Environment | `ASPNETCORE_ENVIRONMENT=Production`, `Rag__FilesDirectory=<repo>/data/files` |
| Behaviour | Starts at login, restarts if it exits (`KeepAlive`), logs to `data/logs/ragamuffin.log` |

The script publishes into a clean `publish/` folder, then reloads the agent. Interrupted indexing and analysis jobs resume after the restart. `scripts/uninstall-service.sh` removes the agent; documents and the database are untouched.

## MCP clients

**Claude Code:**

```bash
claude mcp add --transport http --scope user rag http://localhost:5077/mcp
scripts/install-skills.sh
```

The key (`rag`) prefixes the tool names Claude sees, e.g. `mcp__rag__search_documents`. The server identifies itself as `ragamuffin` and sends its own usage instructions, so no notes are needed in `CLAUDE.md`. `scripts/install-skills.sh` links the skills in `skills/` into `~/.claude/skills` (links, so they follow the repo).

## Fixed in code

These aren't settings; changing them means editing the code.

| Value | Where | Notes |
|---|---|---|
| Embedding dimensions: 1,024 | `RagDbContext.EmbeddingDimensions` | The database column is `vector(1024)`. pgvector's HNSW index takes at most 2,000 dimensions, so larger models (qwen3-embedding:4b has 2,560) would need `halfvec`. |
| A page "has text" with 20+ non-space characters | `PdfPages.MinUsefulChars` | Fewer counts as blank (needs OCR). |
| Headers and footers dropped only when ≤ 15 words | `LayoutAssembler.MaxFurnitureWords` | Longer "footer" regions (notes, legends) are kept. |
| Rebuilt tables abandoned when a cell holds ≥ 3 line breaks | `LayoutAssembler.MaxLinesPerCell` | The table then keeps the PDF's own content order. |
| Hybrid search: reciprocal rank fusion with k = 60; at least 20 candidates from each of vector and full-text search | `SearchService` | Full-text search first requires every word; when that finds too few, it adds chunks matching any word, ranked by how rare the matched words are. |
| Results per search: up to 50 (web UI/API), up to 25 (MCP) | `SearchService`, `RagTools` | MCP search fetches four times as many candidates (at most 50), then merges and de-duplicates them. |
| MCP passages merge at most 3 consecutive server-made chunks (Claudomatics are never merged) and rank by their chunks' summed scores; when duplicates differ in code, the one from the document whose name the question mentions is kept; passages sharing ≥ 60% of their 5-word phrases count as duplicates | `LeanText.MaxRun`, `LeanText.Dedupe` | Keeps one manual's many editions from filling the results. |
| Excerpts pick the lines that cover the most query terms (rare terms and repeated mentions count more), then join nearby lines and add the lines that follow | `LeanText.Excerpt` | Used for search results and for the passages `ask_library` sends its model. |
| `ask_library` retrieves 24 candidates and answers from the best 6 passages. Any name the answer calls like a function (`Name(`) that isn't in those passages triggers one rewrite with the list of calls the passages do show; if some remain, the answer carries a warning naming them. Language keywords and standard C library functions (`printf`, `strlen`, …) are exempt, since manuals describe them without naming them | `LibraryAnswerer` | Small models tend to invent plausible toolbox calls. |
| Outlines of plain-text documents come from detected headings: short capitalized lines after a blank line or outdented from the next line; "Chapter", "Part", "Appendix"… start top-level entries | `DocumentReader.DetectHeadings` | Markdown headings are used when present; PDFs without headings fall back to each page's opening line. |
| A conversion hides the PDF pages its chunks cover (by their `pages`); a conversion without page numbers hides the whole PDF | `SearchService` | Lets a manual be converted chapter by chapter. |
| Claudomatics: whole lines, in order, not overlapping; uncovered lines with text are reported back | `ChunkPlan` | |
| Pages "need review" when they have a recorded OCR or layout problem, no usable text, embedded text that looks garbled, or only glm-ocr's reading; problem pages are listed first | `PageTools.NeedsReview` | A page Claude saved or confirmed no longer needs review; a later glm-ocr run doesn't replace Claude's text. |
| Muted collections are left out of search (MCP and Test search) unless named in `collection`/`collections`; listings and instructions still show them, marked | `SearchService.ResolveAsync` | The mute switch is on the Collections page (`POST /api/collections/mute`). |
| Collection names ≤ 100 characters; descriptions ≤ 1,000 characters; up to 20 keywords of ≤ 60 characters | `CollectionRules` | |
| Batch time estimates assume about 45 seconds per page | `app.js`, `stats.js` | Display only. |
| OCR/layout availability cached 15 s; MCP instructions cached 30 s | `OcrEngine`, `LayoutClient`, `LibraryGuide` | |

## Changing models

- **Embedding model.** A model with a different vector size needs a database migration to change `vector(1024)`, and every document has to be re-embedded: `POST /api/reindex-all` (about 40 minutes for the current library). A model with the same size only needs the re-index, because vectors from different models can't be compared. Also set `DocumentPrefix`/`QueryPrefix` to what the new model expects.
- **OCR model.** Existing OCR text stays as it is; each page records the model that read it (shown in the OCR badge tooltip and on the Stats page). To re-read pages with the new model, use **Re-index… → Replace text with local OCR**, or **Process…**. For reasoning models, also set `Rag:Ocr:Think` to `false`.
- **Suggest and answer models.** Nothing else changes; they're only used for **Suggest** and `ask_library`. Using the same model for both keeps one fewer model loaded on the Ollama server.
