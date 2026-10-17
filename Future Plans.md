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

### Layout-aware OCR for tables and figures
glm-ocr has separate `Table Recognition:` and `Figure Recognition:` modes, and its official SDK runs layout detection first and recognizes each region separately. We send the whole page with `Text Recognition:`. Findings so far:
- Body text on a dense page came out word-for-word identical to the embedded text.
- Running headers and footers were left out. That's good for search, but it means page furniture is lost.
- On a mostly blank page it skipped the title until we trimmed blank margins (now done automatically). Other isolated small blocks may still be skipped.

A layout pass (detect regions, then send each with the right prompt) would improve tables and isolated blocks.

### Choosing the model
`glm-ocr` was a best guess: small, document-focused and the most-pulled OCR model on Ollama. `qwen3.8:27b` (already installed, vision-capable, slower) is the obvious alternative for messy pages. Possible improvements:
- A model picker in the UI, and a filter for "documents OCR'd with a model other than the current one", to re-run them in bulk.
- A small benchmark page: OCR one page with two models and show both.
- *Note:* reasoning models such as qwen3.8 need `Rag:Ocr:Think` set to `false`.

### Bulk actions
- "Run OCR on all documents that need it" (per collection or everywhere).
- "Re-OCR all suspect pages" across a collection.
- *Why deferred:* one document at a time keeps runs visible and cancellable while the model choice is still unproven.

### Automatic OCR on import
`Rag:Ocr:AutoOnImport` exists but is off by default. Scanned PDFs wait at **needs OCR** for a manual **Run OCR**, so a big batch can't occupy `tb` unexpectedly. Revisit once OCR has proven reliable.

### Apple Vision fallback
macOS's Vision framework (`VNRecognizeTextRequest`) gives good OCR on this Mac with no model download, via a small Swift helper. It would be useful when `tb` is offline, or as a second opinion for comparing transcriptions. It only works on macOS.

### OCR for image uploads
PNG/JPEG/TIFF uploads (photos of pages, screenshots) could use the same pipeline as scanned PDF pages.

### Smarter "poor OCR" detection
The current check is conservative: invisible text over a full-page image, OCR software in the PDF metadata, or garbled tokens. Options:
- A dictionary-based word check.
- A per-document score shown in the UI.
- Letting the user mark a whole document as "old OCR" so every page is re-OCR'd.

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

- **Write tools:** MCP is read-only by design; documents are managed in the web UI. Adding `add_note` or `add_document` tools would let the opencode agent save findings.
- **Code Mode:** opencode V2 calls MCP tools through Code Mode by default. In testing, qwen first tried a tool name that doesn't exist, then got it right. If calls are unreliable, set `"codemode": false` on the `rag` server.

## Operations

- **No authentication:** the server listens only on `localhost`. Exposing it on the network would need auth first.
- **Database in Docker:** chosen because Homebrew no longer ships prebuilt Postgres for Intel Macs. Docker Desktop must start at login for the database to come back.
- **Backups:** nothing backs up the Postgres volume or `data/files` yet.
- **Dev database:** a `rag_dev` database exists for test instances (see the README). It was created by hand, not by a script.
