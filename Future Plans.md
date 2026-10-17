# Future plans

Improvements we may make later, plus decisions worth revisiting. Each entry says why it was deferred or chosen, so it can be re-judged with better information.

## OCR

### Side-by-side page review
A per-page review screen showing the page image, the embedded text and the OCR text, with differences highlighted and accept/revert buttons for each page.
- *Why deferred:* the data needed for it is already kept (`document_pages` stores embedded and OCR text side by side), and **Revert to embedded text** already gives a safe undo at document level.
- *What it needs:* an endpoint that renders a page image, a diff view, and per-page accept/revert endpoints that re-chunk the document.

### Flag pages where OCR and embedded text disagree
After re-OCR'ing a page that had embedded text, compare the two versions. Small differences are probably fixes; large ones (missing lines, changed numbers) could be dropped or invented text from the vision model. Pages over a threshold would be flagged for review instead of silently accepted.
- Pairs naturally with the side-by-side review.

### "Correct" prompting mode
Instead of transcribing from scratch, give the model the page image *and* the existing embedded text and ask it to fix errors. This is anchored to the old text, so it's less likely to drop content, but it may carry old errors forward. Worth comparing against fresh transcription on real old OCR'd PDFs.

### Choosing the model
`glm-ocr` was a best guess: small, document-focused and the most-pulled OCR model on Ollama. `qwen3.8:27b` (already installed, vision-capable, slower) is the obvious alternative for messy pages. Possible improvements:
- A model picker in the UI, and a filter for "documents OCR'd with a model other than the current one", to re-run them in bulk.
- A small benchmark page: OCR one page with two models and show both.
- *Note:* reasoning models such as qwen3.8 need `Rag:Ocr:Think` set to `false`.

### Automatic OCR and layout on import
`Rag:Ocr:AutoOnImport` and `Rag:Layout:AutoOnImport` exist but are off by default, so documents are processed in deliberate batches (e.g. overnight with **Process…**). Revisit once the pipeline has proven reliable.

### Apple Vision fallback
macOS's Vision framework (`VNRecognizeTextRequest`) gives good OCR on this Mac with no model download, via a small Swift helper. It would be useful when `tb` is offline, or as a second opinion for comparing transcriptions. It only works on macOS.

### OCR for image uploads
PNG/JPEG/TIFF uploads (photos of pages, screenshots) could use the same pipeline as scanned PDF pages.

### Smarter "poor OCR" detection
The current check is conservative: invisible text over a full-page image, OCR software in the PDF metadata, or garbled tokens. Options:
- A dictionary-based word check.
- A per-document score shown in the UI.
- Letting the user mark a whole document as "old OCR" so every page is re-OCR'd.

## Layout analysis

Built: the layout service (`layout-service/`, PaddleOCR PP-StructureV3) handles orientation, regions and reading order; glm-ocr reads each region of scans; digital pages are filled with the PDF's own words; batches are queued with **Process…**. The comparison that led here is in `eval/` (re-run `eval/scripts/` to re-check after changes). Follow-ups:

### Run the layout service on `tb`'s GPU
On this Mac's CPU it takes about 25–40 seconds per page. With Docker on `tb` and a GPU image (`LAYOUT_DEVICE=gpu`), it would be several times faster. Only `Rag:Layout:Url` would change.

### Re-assemble pages without re-running models
Each page stores its regions (`layout_json`). Improvements to the assembly step (e.g. better table handling) could be re-applied to embedded-text pages from the stored regions, without calling the layout service again. Scans would still need glm-ocr re-runs for changed regions.

### Dense and unusual tables
Paddle's cell grid is sometimes coarser than the real table (the crow_cousins opcode sheet). Those tables fall back to the PDF's content order, which keeps rows intact but loses the grid. Options: glm-ocr `Table Recognition:` on the table crop as a second opinion, or a column-gap splitter for side-by-side tables.

### Rotated pages with embedded text
Layout is skipped on embedded-text pages that are rotated (the PDF's own rotation flag, or a rotation the layout service detects), because mapping words onto rotated regions needs extra geometry. They keep their plain embedded text and are listed in the job notice.

### Formulas and charts
Formula regions are sent to glm-ocr's `Formula Recognition:` mode but stored as plain text. Chart regions keep only their visible text. Rendering formulas as LaTeX and describing charts are both possible with a larger vision model.

### Document-level heading levels
Headings are `#` for document titles and `##` for everything else. Real heading depth (chapter > section > subsection) could be inferred from font size on digital pages, or from numbering ("2.1.3"), which would give deeper breadcrumbs.

## Web import and crawling

Point the server at a URL and ingest that page, or a whole documentation site, into a collection. The motivating case is a local, searchable copy of the Avalonia docs for opencode.

### How it would work
- **Starting point:** a start URL plus a scope. By default the scope is same host and the start URL's path prefix (e.g. everything under `https://docs.avaloniaui.net/docs/`), with a maximum depth and page count. Optional include/exclude patterns cover sections like versioned duplicates or blog pages.
- **Finding pages:** use the site's `sitemap.xml` when it has one; most documentation generators (Docusaurus, MkDocs, DocFX) publish one, and it's faster and cleaner than following links. Fall back to following in-scope links.
- **Politeness:** respect `robots.txt`, fetch one page at a time with a delay, and identify the crawler in its user agent.
- **HTML to Markdown:** extract only the main article (drop navigation, sidebars, footers, cookie banners), keep headings and code blocks, and convert tables. The Markdown then goes through the existing chunker, so headings become section breadcrumbs.
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
- **Chunk sizes:** 1,200 / 1,800 / 150 characters were chosen to fit nomic-embed-text's 2,048-token context with room to spare. Revisit if answers often lack surrounding context.
- **Embedding model:** the vector column is fixed at 768 dimensions. Changing model means a migration and re-embedding everything.
- **Parallel OCR pages:** pages are OCR'd one at a time. With `OLLAMA_NUM_PARALLEL` above 1 on `tb`, two or three pages at once could shorten long runs, at the cost of more GPU memory.

## Collections

- **Move documents between collections** in the UI, without re-uploading.
- **Merge collections:** renaming onto an existing name is rejected today, so merging needs a deliberate flow that handles duplicate document names.
- **Per-collection settings,** e.g. OCR model or chunk size.
- **Collection names as foreign keys:** documents reference collections by name with `ON UPDATE CASCADE`. It's simple and readable, but a numeric key would make renames cheaper for very large collections.

## MCP and opencode

- **Collection names in the `collection` parameter:** the tool's parameter description could list the valid collection names (it needs a custom tool listing, since tool descriptions are normally fixed at startup). Worth adding if agents guess names that don't exist.
- **MCP prompts:** slash commands such as `/ragamuffin:research <topic>` that tell the agent to search, compare sources and cite them.

- **Write tools:** MCP is read-only by design; documents are managed in the web UI. Adding `add_note` or `add_document` tools would let the opencode agent save findings.
- **Code Mode (decided):** opencode V2 wraps MCP tools in Code Mode by default. With local models it caused failed calls: a nonexistent tool name, and calls without `await` ("JSON.stringify received an un-awaited Promise"). The recommended config now sets `"codemode": false`. Revisit if RAGamuffin grows many more tools, where Code Mode's batching might pay off.

## Operations

- **No authentication:** the server listens only on `localhost`. Exposing it on the network would need auth first.
- **Database in Docker:** chosen because Homebrew no longer ships prebuilt Postgres for Intel Macs. Docker Desktop must start at login for the database to come back.
- **Backups:** nothing backs up the Postgres volume or `data/files` yet.
- **Dev database:** a `rag_dev` database exists for test instances (see the README). It was created by hand, not by a script.
