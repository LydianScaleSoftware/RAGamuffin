---
name: rag-convert-pdf
description: Convert a PDF (usually a manual) into clean Markdown and index it in RAGamuffin with chunks Claude chooses, so each section, table, listing or reference entry is one search result. Use when the user asks to convert a manual or PDF to Markdown for RAGamuffin, re-chunk a document, or make a manual easier to search.
---

# Convert a PDF to Markdown and index it in RAGamuffin

The result is a Markdown file on disk plus a RAGamuffin document made from it, linked to the original PDF, whose
chunks you choose: **Claudomatics** (Claude + automatic). The user can browse, edit and export them on the web UI's
Claudomatics page. The
PDF's own chunks for the pages you convert drop out of search; the PDF stays in the library for page images.

## 1. Plan

- Make sure the PDF is in the library (`list_documents(filter=…)`); if not, `import_file(path, collection)` and
  wait for it to index. You need it there for `get_page` and for `source_document`.
- `get_document(pdf)` shows its outline; `list_pages(pdf, which="all")` shows page count and text quality.
- **Split big manuals by chapter**: one Markdown file and one `add_document` call per chapter (or per 50–150
  pages). Each call must list all of that file's chunks, and a 1,000-page manual in one call would be too large.
  Name them so they sort: `Manuals/Toolbox Ref Vol 2/16 QuickDraw II.md`. Conversions may cover only some
  chapters; only the pages they cover leave search.
- Tell the user the plan (chapters, page count, rough size) before a long conversion. Page images cost roughly
  1,500 tokens each.

## 2. Write the Markdown

Work in batches of about 10 pages. For each page, `get_page(pdf, page)` gives the image and the existing text
versions; the image is the truth, the text saves typing where it's right. (For a PDF on disk you may instead
read it with the Read tool, up to 20 pages at a time.)

Append each batch to the chapter's `.md` file as you go, so nothing is lost if the session is interrupted.

- Put a page marker before each page's content: `<!-- page 112 -->`. Chunks take their page numbers from these.
- One `#` heading for the chapter, `##`/`###` for its sections, following the manual's own structure.
- Tables as Markdown tables with every row and column; code and listings in fenced blocks with the language.
- Copy identifiers, call names, parameters, constants and hex values exactly; don't fix the manual's code.
- Join a sentence, table or listing that continues across a page break into one piece (keep the page marker
  where the new page starts, outside code blocks and tables — put it just before the block if the block starts
  on that page).
- Leave out running headers, footers and page numbers. Describe figures in one line; mark `[illegible]` text.
- Skip the table of contents and index pages, or keep them but leave them out of the chunks.

If you corrected a page's text substantially, you may also save it to the PDF with `set_page_text` (optional;
ask the user if they want the PDF's own pages fixed too).

## 3. Choose the chunks (the Claudomatics)

Read the finished file with line numbers (the Read tool shows them) and divide it into chunks. A chunk is one
coherent unit someone would want as a single search result:

- a section or subsection that explains one thing (typically 300–3,000 characters);
- a reference entry: one toolbox call, command or instruction with its parameters, errors and notes;
- a table, whole, with the sentence that introduces it;
- a listing or code example, whole, with the explanation just before it.

Never split a code block, table or reference entry. Don't make tiny chunks of a heading alone, and don't merge
unrelated topics. Size is up to the unit (limit 60,000 characters); for long units the summary matters most,
because the search embedding reads the summary and only the first part of the text.

For each chunk give:

- `start_line`, `end_line` — whole lines, in order, not overlapping. Blank lines between chunks are fine.
- `title` — what it is: `"DrawCString"`, `"Listing 10-1: Writing text to the graphics screen"`.
- `section` — breadcrumb: `"Chapter 16 QuickDraw II > Drawing text"`.
- `summary` — one or two sentences in the words someone would search with, including synonyms the text doesn't
  use: `"How to draw a C string at the pen location with QuickDraw II; parameters and an example."`
- `kind` — `section`, `reference`, `table`, `listing`, `example`, `glossary`, …
- `symbol` — for a reference entry or code: the call or function name.
- `pages` — from the page markers the chunk spans: `"112"` or `"112-113"`.

## 4. Add it

```
add_document(
  name="Manuals/Toolbox Ref Vol 2/16 QuickDraw II.md",
  path="/absolute/path/16 QuickDraw II.md",
  collection="Apple 2gs Manuals",
  source_document="Manuals/Apple IIGS Toolbox Reference Volume 2.pdf",
  chunks=[…])
```

If it reports errors, fix the ranges and call again (nothing is stored until it succeeds). If it reports lines
no chunk covers, check they were left out on purpose.

## 5. Check

Run two or three `search_documents` queries a user might ask about the chapter and confirm the new chunks come
back with sensible titles. Report the files written, chunk counts and anything skipped.

To re-chunk later without rewriting the file, use `set_chunks(document, chunks)`.
