---
name: rag-check-pdf
description: Check and correct the text of a PDF in the RAGamuffin library page by page against the page images (Claude does the OCR and text verification). Use when the user asks to OCR, verify, proofread or fix the text of a PDF or manual in RAGamuffin, or to add a scanned PDF so it becomes searchable.
---

# Check a PDF's text in RAGamuffin

RAGamuffin (the `rag` MCP server) keeps every PDF page with several text versions: the text embedded in the
PDF, glm-ocr's reading, a layout-arranged version, and yours. Your job: look at each page image, make sure the
text the library uses matches the page, and save corrections. The library re-indexes after you save.

## 1. Find the document and the pages to check

- If the user gave a file path and the PDF isn't in the library yet, add it: `import_file(path, collection)`.
  Wait for indexing (about a minute for a few hundred pages; `list_pages` reports "no pages" until then).
- `list_documents` and `get_document` show each PDF's **page health**: "N problem pages", "no problems (not
  verified)" or "verified". Each page has one state:
  - **problem**: the text is probably wrong or missing (glm-ocr failed on a region, layout failed on a scan, no
    text on a page with content, garbled text). Start with `list_pages(document, which="problems")`.
  - **note**: something was skipped but the text is intact (e.g. layout skipped on a rotated page).
  - **muted**: the user set a problem or note aside in the web UI. Leave muted pages alone unless asked.
  - **blank**: no text, and the page image is blank too.
  - **verified**: you checked it (`set_page_text`) or the user marked it correct.
  - **unverified**: machine-read text nobody has checked, with nothing pointing to a fault.
- `list_pages(document)` lists the pages that need review: problem pages, then unverified pages only glm-ocr has
  read. `which="all"` lists every page, and `which="notes"` lists notes and muted items.
- A PDF that came with its own OCR text layer isn't faulty because of that. To settle it, check a **sample**
  instead of every page (see step 3).
- Tell the user how many pages need review before starting a big job. Each page image costs roughly 1,500 tokens,
  so a few hundred pages is a long session; offer to do a range (`pages="1-50"`), only the problem pages, or a
  sample check.
- Tell the user how many pages need review before starting a big job. Each page image costs roughly 1,500 tokens,
  so a few hundred pages is a long session; offer to do a range (`pages="1-50"`) or only the flagged pages.

## 2. Check pages in batches

For each page, `get_page(document, page)` returns the image and every text version, with the one in use marked.

- **If the text in use matches the page**, confirm it without retyping:
  `{page: 12, confirm: true}`.
- **If it's wrong or missing**, write the page's full text as Markdown from the image, using the existing
  versions to save effort where they're right, and save it: `{page: 12, text: "…", note: "fixed table; OCR dropped
  the second column"}`.

Save several pages per `set_page_text` call (5–10), with `reindex=false` on every call except the last one for
the document, so it re-indexes once.

## How to transcribe a page

- Reading order as a person reads the page: columns top to bottom, left column first; captions next to their
  figures; sidebars after the main text they belong to.
- Markdown structure: `#`/`##`/`###` for the page's real headings, lists as lists, tables as Markdown tables
  (keep every row and column), code and listings in fenced blocks with the language (```c, ```asm, ```pascal).
- Copy exactly: function and call names, parameters, constants, hex values (`$A604`), register names, error
  codes, punctuation in code. Don't "fix" the manual's spelling of identifiers or its code.
- Leave out running headers, footers and page numbers. Join words hyphenated across line breaks.
- Describe figures you can't transcribe in one line: `[Figure 10-3: QuickDraw coordinate plane, origin top-left]`.
- Mark text you can't read as `[illegible]` rather than guessing.

## 3. Verifying a whole document by sample

To give a document a clean bill of health without checking every page:

1. `list_pages(document, which="sample")` picks about 12 unverified pages spread through the document, favouring
   tables and listings.
2. Check each one with `get_page`. Confirm it (`confirm: true`) or correct it with `set_page_text`.
3. If the sample matched (a few small corrections are fine), call `verify_document(document, note)` with a short
   account, e.g. "12 sampled pages matched; one misread digit in a table corrected". It needs at least 10 checked
   pages, and the web UI then shows the document as **Verified**.
4. If many sampled pages were wrong, don't verify. Tell the user, and suggest re-OCR or a full check.

## 4. Finish

Pass `reindex=true` on the last `set_page_text` call. Report what you checked: pages confirmed, pages corrected
(with the kind of problems found), the document's health afterwards, and pages left for later.
