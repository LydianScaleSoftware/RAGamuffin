# Future plans

Improvements we may make later, plus decisions worth revisiting. Each entry says why it was deferred or chosen, so it can be re-judged with better information.

## Seeing what Claude changed

Claude can now change the library through MCP (save page text, add conversions as Claudomatics, re-chunk, delete). The web UI shows the results (the **Claude** and **Claudomatics** badges, the **Claudomatics** page, "from …" / "→ …" links, the **Updated** time, chunk titles and summaries in **Inspect**), but not what changed, when or why.

### Pages view for each PDF
A per-page screen: the page image beside the text in use, with its source marked (embedded, layout, glm-ocr or Claude), Claude's note for the page, and a comparison with the text it replaced. Buttons per page to switch back to an earlier version or remove Claude's text, and a filter for "pages needing review" matching `list_pages`.
- *Replaces* the earlier "side-by-side page review" plan, which predates Claude's page text.
- *Why it's cheap now:* everything it shows is already stored (`document_pages` keeps embedded, layout, OCR and Claude text, `claude_note`, and `source_before_claude`), and pages render to PNG for `get_page` already. It needs a page-image endpoint, the view, a text diff, and per-page set-source endpoints that re-index the document.

### Claudomatics page (built)
The **Claudomatics** page lists every chunk Claude chose, grouped by day and document, with edit, delete, export and the source page image. Possible next steps: bulk select (delete or re-summarize several), a "summary missing" filter, and asking Claude from the page to re-chunk a document.

### Activity log
A record of every change made through the write tools (`import_file`, `add_document`, `set_chunks`, `set_page_text`, `delete_document`) and of the web UI's own uploads, re-indexes and deletes: time, document, what changed (pages saved or confirmed, chunk counts before and after, conversion links), Claude's notes, and which client made it. Shown as an **Activity** list (and "recent changes" on the Stats page), filterable by document.
- *Why:* today the only trace is the service log. A log table written by the write tools (and the tool-call filter, which already sees every call) would make Claude's work auditable and easy to review after a long session.

## Binary files

### Binaries through the web upload
Binary files are stored encoded as hex dumps (with the original bytes kept), but only when Claude adds them (`import_file`, `add_document`); the web upload still rejects them. Next: accept them in the web upload too, under the same `Rag:Mcp:MaxBinaryBytes` limit (or a separate upload limit), with the ⬢ Encoded badge.

### Better encodings for known formats
A plain hex dump suits ROMs and unknown files. Known Apple II formats could get more useful text: a 6502/65816 disassembly for binaries with a known load address, ProDOS or DOS 3.3 catalogs and per-file dumps for disk images, decoded shape tables or fonts. Each would still keep the original bytes and the Encoded marking.

## OCR

### Flag pages where OCR and embedded text disagree
After re-OCR'ing a page that had embedded text, compare the two versions. Small differences are probably fixes; large ones (missing lines, changed numbers) could be dropped or invented text from the vision model. Pages over a threshold would be flagged for review instead of silently accepted.
- Flagged pages would then show up in `list_pages` for Claude to check (`rag-check-pdf`), and in the Pages view.
- The mechanism exists now: pages record an `issue` from their last OCR or layout run (failed regions, no text, layout failures) and `list_pages(which="problems")` lists them. A disagreement check would add one more kind of issue.

### "Correct" prompting mode for glm-ocr
Claude already works this way: `get_page` gives it the page image *and* every existing text version, and it confirms or corrects rather than retyping. The same idea could be tried for **Run OCR**: give glm-ocr the image and the embedded text and ask it to fix errors. It's less likely to drop content but may carry old errors forward. Lower priority now that Claude covers the careful case.

### Claude behind the Run OCR button
**Run OCR** uses glm-ocr on `tb`; Claude only checks pages when asked from Claude Code. The button could optionally send pages to Claude through the Anthropic API instead (an API key, per-page charges). Worth it only if glm-ocr's quality is a real problem for bulk scans.

### Choosing the model
`glm-ocr` was a best guess: small, document-focused and the most-pulled OCR model on Ollama. `qwen3.8:27b` (already installed, vision-capable, slower) is the obvious local alternative for messy pages; Claude (`rag-check-pdf`) is the most accurate option but costs tokens per page. Possible improvements:
- A model picker in the UI, and a filter for "documents OCR'd with a model other than the current one", to re-run them in bulk.
- A small benchmark page: OCR one page with two models and show both.
- *Note:* reasoning models such as qwen3.8 need `Rag:Ocr:Think` set to `false`.

### Automatic OCR and layout on import
`Rag:Ocr:AutoOnImport` and `Rag:Layout:AutoOnImport` exist but are off by default, so documents are processed in deliberate batches (e.g. overnight with **Process…**). Revisit once the pipeline has proven reliable.

### Apple Vision fallback
macOS's Vision framework (`VNRecognizeTextRequest`) gives good OCR on this Mac with no model download, via a small Swift helper. It would be useful when `tb` is offline, or as a second opinion for comparing transcriptions. It only works on macOS.

### OCR for image uploads
PNG/JPEG/TIFF uploads (photos of pages, screenshots) could use the same pipeline as scanned PDF pages. Until then, Claude can read an image itself and add its text with `add_document`.

### Smarter "poor OCR" detection
The current check is conservative: invisible text over a full-page image, OCR software in the PDF metadata, or garbled tokens. Options:
- A dictionary-based word check.
- A per-document score shown in the UI.
- Letting the user mark a whole document as "old OCR" so every page is re-OCR'd, or listed for Claude to check.
- This check decides which pages `list_pages` reports as needing review, so better detection also saves Claude's time on long manuals.

## Layout analysis

Built: the layout service (`layout-service/`, PaddleOCR PP-StructureV3) handles orientation, regions and reading order; glm-ocr reads each region of scans; digital pages are filled with the PDF's own words; batches are queued with **Process…**. The comparison that led here is in `eval/` (re-run `eval/scripts/` to re-check after changes). Follow-ups:

### Run the layout service on `tb`'s GPU
On this Mac's CPU it takes about 25–40 seconds per page. With Docker on `tb` and a GPU image (`LAYOUT_DEVICE=gpu`), it would be several times faster. Only `Rag:Layout:Url` would change.

### Re-assemble pages without re-running models
Each page stores its regions (`layout_json`). Improvements to the assembly step (e.g. better table handling) could be re-applied to embedded-text pages from the stored regions, without calling the layout service again. Scans would still need glm-ocr re-runs for changed regions.

### Dense and unusual tables
Paddle's cell grid is sometimes coarser than the real table (the crow_cousins opcode sheet). Those tables fall back to the PDF's content order, which keeps rows intact but loses the grid. Options: glm-ocr `Table Recognition:` on the table crop as a second opinion, or a column-gap splitter for side-by-side tables. For the tables that matter, Claude can transcribe the page as a Markdown table (as it did for Table 7-2 of the W65C816S data sheet).

### Rotated pages with embedded text
Layout is skipped on embedded-text pages that are rotated (the PDF's own rotation flag, or a rotation the layout service detects), because mapping words onto rotated regions needs extra geometry. They keep their plain embedded text and are listed in the job notice.

### Formulas and charts
Formula regions are sent to glm-ocr's `Formula Recognition:` mode but stored as plain text. Chart regions keep only their visible text. Rendering formulas as LaTeX and describing charts are both possible with a larger vision model.

### Document-level heading levels
Headings are `#` for document titles and `##` for everything else. Real heading depth (chapter > section > subsection) could be inferred from font size on digital pages, or from numbering ("2.1.3"), which would give deeper breadcrumbs.

## Web import and crawling

Point the server at a URL and ingest that page, or a whole documentation site, into a collection. The motivating case is a local, searchable copy of the Avalonia docs.

For a handful of pages this no longer needs server work: Claude Code can fetch a page, write it as Markdown, and add it with `add_document` as Claudomatics. A server-side crawler is still the better fit for whole sites and for refreshing them on a schedule.

### How it would work
- **Starting point:** a start URL plus a scope. By default the scope is same host and the start URL's path prefix (e.g. everything under `https://docs.avaloniaui.net/docs/`), with a maximum depth and page count. Optional include/exclude patterns cover sections like versioned duplicates or blog pages.
- **Finding pages:** use the site's `sitemap.xml` when it has one; most documentation generators (Docusaurus, MkDocs, DocFX) publish one, and it's faster and cleaner than following links. Fall back to following in-scope links.
- **Politeness:** respect `robots.txt`, fetch one page at a time with a delay, and identify the crawler in its user agent.
- **HTML to Markdown:** extract only the main article (drop navigation, sidebars, footers, cookie banners), keep headings and code blocks, and convert tables. The Markdown then goes through the existing chunker, so headings become section breadcrumbs (or Claude re-chunks the important pages with `set_chunks`).
- **Storage:** each page becomes a document named by its URL path, in a collection chosen at import (e.g. "Avalonia docs"). The source URL is stored with the document, and MCP search results cite the URL instead of a file name.
- **Re-crawl/refresh:** re-run a saved crawl to pick up changes. Pages are skipped when unchanged (ETag/`Last-Modified`, or same content hash), updated when changed, and optionally removed when they've disappeared from the site. A schedule (e.g. weekly) could come later.
- **Background job:** crawls would run in their own lane with progress and cancel, like OCR, so uploads aren't blocked.
- **UI:** an "Import from URL…" action with the scope options, a preview of how many pages are in scope before starting, and a list of saved crawls with their last run.

### Things to check per site
- **Source repositories:** many documentation sites are built from Markdown in a public Git repository. Importing that repository's Markdown directly can be cleaner than crawling the rendered HTML. Worth checking for Avalonia before building a crawler around it.
- **JavaScript-rendered sites:** pages that only render in a browser would need a headless browser (e.g. Playwright) instead of plain HTTP. Most documentation sites serve full HTML, so this should be an exception path.
- **Versioned docs:** sites often publish the same page for several versions. The scope should pick one version to avoid near-duplicate chunks crowding search results.
- **Licensing:** a local copy for personal search is the intended use; keep crawls to documentation you're allowed to copy.

## Search and indexing

- **Reranking:** a cross-encoder reranker after hybrid retrieval would improve the ordering of the top results.
- **Minimum relevance:** search always returns `top_k` results, even weak ones. A similarity floor would let tools say "nothing relevant".
- **Chunk sizes:** the server's own 1,200 / 1,800 / 150 characters date from nomic-embed-text's 2,048-token context. qwen3-embedding reads 32,768 tokens, so larger automatic chunks are now possible; documents Claude converts already use its own sizes.
- **Embedding model:** `qwen3-embedding:0.6b` (1,024 dimensions). The 4b model may search better but has 2,560 dimensions, past pgvector's 2,000-dimension HNSW limit, so it would need `halfvec`; worth measuring with the same 30-question test first.
- **Search test set:** the 30 paraphrased IIgs questions used to choose qwen3-embedding (and to tune the query instruction) live only in a scratch folder. Checking them in with a script that scores the live search would let any ranking or model change be measured before it ships.
- **Query instruction per collection:** `Rag:QueryPrefix` names the Apple IIgs because the library is mostly IIgs, and that scored better than generic wording. If other subjects grow, the instruction could come from each collection's description, or a search could be run once per relevant collection.
- **Parallel OCR pages:** pages are OCR'd one at a time. With `OLLAMA_NUM_PARALLEL` above 1 on `tb`, two or three pages at once could shorten long runs, at the cost of more GPU memory.

## Collections

- **Move documents between collections** in the UI, without re-uploading.
- **Merge collections:** renaming onto an existing name is rejected today, so merging needs a deliberate flow that handles duplicate document names.
- **Per-collection settings,** e.g. OCR model, chunk size or query instruction.
- **Mute presets:** muting is per collection today. Named presets ("IIgs System 6 work", "Apple II+ work") would switch a whole set of mutes in one click once there are several conflicting groups; Claude could also be told a preset's name.
- **Muting per session:** mutes are global, so two Claude sessions on different projects share them. Per-task scoping already works through `collections`/`exclude` on `search_documents`; a session-level default (e.g. from a project's `CLAUDE.md`) could build on that.
- **Collection names as foreign keys:** documents reference collections by name with `ON UPDATE CASCADE`. It's simple and readable, but a numeric key would make renames cheaper for very large collections.

## MCP and Claude Code

- **Code by function:** `add_document` already stores `kind` and `symbol` on chunks, so whole functions can be indexed without fragmenting. Still to do: a `rag-index-code` skill (one chunk per function or class, with the signature in the title and a summary of what it does), and possibly a server helper that proposes function boundaries (Roslyn for C#, tree-sitter for others) so Claude doesn't have to read every file to find them. Search could also filter by `kind` or look up a `symbol` exactly.
- **Large conversions:** `add_document` takes all of a file's chunks in one call, so the skill splits big manuals by chapter. An "append chunks" mode (add a batch of chunks to a document in several calls) would allow one document per manual.
- **Collection names in the `collection` parameter:** the tool's parameter description could list the valid collection names (it needs a custom tool listing, since tool descriptions are normally fixed at startup). Worth adding if agents guess names that don't exist.
- **MCP prompts:** for Claude Code the skills (`rag-check-pdf`, `rag-convert-pdf`) do this job. Prompts such as `/ragamuffin:research <topic>` would bring similar guidance to other MCP clients.
- **Write tools (built):** `import_file`, `add_document`, `set_chunks`, `set_page_text` and `delete_document`, behind the **Agents write** switch, reading files only under `Rag:Mcp:ImportRoots`. A smaller `add_note` tool (save a finding without a file) could follow if Claude often stores notes.
- **`ask_library`:** kept for agents with small contexts but off unless `Rag:Mcp:AnswerModel` is set. Remove it entirely if no such client is used again.

## Operations

- **No authentication:** the server listens only on `localhost`. Exposing it on the network would need auth first, more so now that MCP tools can change the library and read local files (limited to `Rag:Mcp:ImportRoots`).
- **Database in Docker:** chosen because Homebrew no longer ships prebuilt Postgres for Intel Macs. Docker Desktop now starts at login (its setting plus a login item), so the database returns after a reboot once someone logs in (or with macOS automatic login). Starting before any login would need Postgres outside Docker and a launchd daemon instead of the current agent.
- **Backups:** nothing backs up the Postgres volume or `data/files` yet. That matters more now: Claude's corrected page text and its chunk choices exist only in the database (conversions also keep their Markdown files wherever Claude wrote them). A nightly `pg_dump` plus a copy of `data/files` would cover it.
- **Dev database:** a `rag_dev` database exists for test instances (see the README). It was created by hand, not by a script.
