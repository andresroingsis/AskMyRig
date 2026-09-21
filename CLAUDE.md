# Ask My Rig

A RAG app over my own music-gear manuals (PDF). Ask a question in plain English, get an answer grounded in the manuals, with manual and page citations. Project 1 of a five-project applied-AI portfolio roadmap.

Read `docs/HANDOVER.md` before starting a new task or project phase. It holds the decisions and their reasons, specs for the remaining work, and the full roadmap. It's a snapshot from the move to Claude Code (21 Sep 2026); this file and the code are the live source of truth.

<!-- Note for humans: the Retrieval, Answering and Measurement rules were proposed during the claude.ai → Claude Code handover. Edit freely. Block-level HTML comments like this one are stripped before Claude reads the file. -->

## Status

Update this section whenever a task lands or a decision is made.

- Project 1, retrieval foundations: about 80% done (audited against the code and the live database, 21 Sep 2026)
  - Done: PdfPig extraction → boilerplate removal → heading-aware segmentation → token-budget chunking → Ollama embeddings → chunks and `VECTOR(768)` rows in SQL Server 2025 via Dapper. 8 manuals, 483 chunks, all 483 embedded.
  - Done: semantic search. `POST /search` returns the top-k chunks with manual, page range, heading, content and similarity, plus embed/search timings.
  - Done: retrieval lives in `AskMyRig.Core` behind `IRetriever`, implemented by `VectorRetriever`. Project 2's keyword and fused retrievers plug in at the one registration line in `Program.cs`.
  - Remaining, in order: LLM answering with citations → UI. Estimate 2–3 weekends.
- The handover listed the search endpoint as not started; it was already built. Everything else in the handover's remaining-work list still stands.
- Next: Project 2 (eval set, recall@k and MRR, hybrid search, parameter-table fix).

## Commands

Run from the repo root.

```bash
dotnet build AskMyRig.slnx
dotnet run --project AskMyRig.Ingestion -- parse    # PDFs → output/chunks.json (no network)
dotnet run --project AskMyRig.Ingestion -- ingest   # chunks.json → SQL Server, embedding as it goes
dotnet run --project AskMyRig.Ingestion             # both
dotnet run --project AskMyRig.Api                   # http://localhost:5124
```

`parse` and `ingest` are separate on purpose: re-parsing costs nothing, and re-embedding doesn't mean re-reading the PDFs. Ingestion is idempotent per manual — a re-run replaces that manual's chunks rather than duplicating them.

There is no test project yet.

## Local prerequisites

- **SQL Server 2025** (17.0.1000.7 RTM), instance `localhost\MSSQLSERVER01`, database `AskMyRig`, Windows auth.
- **Ollama** at `http://localhost:11434` with `nomic-embed-text` pulled (`ollama pull nomic-embed-text`).
- **.NET 10 SDK** (currently 10.0.300-preview).
- Config: the API reads `Sql:ConnectionString` and `Ollama:Url|Model|Dimensions` from `AskMyRig.Api/appsettings.json`. Ingestion reads user secrets and environment variables only — its Ollama settings and its data/output paths are compile-time constants in `AskMyRig.Ingestion/Program.cs`. See Known gaps.
- The API calls `EnsureReadyAsync()` during startup, so it will not boot unless Ollama is running and the model returns 768-dimension vectors.

## Corpus

`data/` holds the PDFs and is gitignored. Currently 8 Yamaha documents → 483 chunks, 176,634 tokens:

| Manual (file stem) | Chunks | Pages | Tokens |
|---|---:|---:|---:|
| PSR-SX920_reference_manual_En_B0 | 182 | 145 | 71,065 |
| psrsx920_sx720_en_om_b0 | 175 | 132 | 65,311 |
| stagepas1kmk | 66 | 28 | 22,177 |
| MIDI_song_to_style_OM_En_A0 | 29 | 34 | 7,929 |
| android_connect_En_D0_web | 13 | 12 | 5,027 |
| audio_phraser_en_om_a0 | 12 | 9 | 3,456 |
| audio_phraser_en_ig_a0 | 4 | 4 | 876 |
| MIDI_song_to_style_v120_SM_En_A0 | 2 | 2 | 793 |

The handover says "three manuals". That's out of date: the corpus is 8 documents covering two pieces of gear (PSR-SX920 keyboard, Stagepas 1K PA) plus companion software guides. The VOLT 2/76, TRBX305 and NTX1 manuals are **not** in the corpus. The manual name stored in SQL is the PDF file stem, which is not presentable — citations will need a display title.

## Stack

- .NET 10, C#, ASP.NET Core minimal API
- PdfPig 0.1.16 for PDF text extraction, with Docstrum block detection and reading-order detection
- SQL Server 2025 native `VECTOR(768)`, Dapper 2.1.79, Microsoft.Data.SqlClient 7.0.2
- Embeddings: Ollama, `nomic-embed-text`, 768 dimensions
- Token counting: Microsoft.ML.Tokenizers, cl100k_base (`text-embedding-3-small` vocabulary) — a size estimate for chunking only, not nomic's tokenizer
- Front end: small React or Blazor app (undecided)
- Tests: xUnit, Moq (no test project yet)

## Pipeline as built

- **Extract** (`PdfExtractor`): PdfPig words → Docstrum blocks → reading order. Per block: text, mean font size, bold if most letters are bold. PdfPig page objects never escape the method.
- **Clean** (`TextCleaner`): boilerplate = blocks of 120 chars or fewer whose digit-normalised form repeats on at least `max(5, 50%)` of pages; documents under 10 pages are exempt. Also drops letter-spaced side tabs ("V o i c e s") and TOC dot-leader lines.
- **Segment** (`SegmentBuilder`): a block is a heading if its font is at least 1.20x the document's character-weighted body size and it passes shape tests (4-90 chars, mostly letters, no sentence-ending punctuation). Heading blocks are kept as segments, not discarded, and flagged `StartsSection`.
- **Chunk** (`ChunkBuilder`): 600 tokens max, 100 tokens overlap, and a section boundary closes the chunk once it holds at least 120 tokens. No overlap is carried across a section boundary. Oversized segments split on sentence boundaries. `PageFrom`/`PageTo` are the min/max page of the segments in the chunk; the heading is prepended to the content unless it's already there.
- **Embed** (`OllamaEmbeddingProvider`): batches of 8; a failed batch is retried one at a time so the offending chunk gets named. Task prefixes `search_document: ` and `search_query: ` are applied only when the model name contains "nomic". Input is sanitised — control characters, lone surrogates and private-use-area glyphs (Yamaha's symbol-font button icons) are stripped.
- **Store** (`ChunkStore`): the vector is written as a JSON array string and `CAST(@Embedding AS VECTOR(768))`, with the parameter declared NVARCHAR(MAX) so Dapper doesn't truncate it at 4,000 chars. `EnsureDimensionsMatchAsync` checks the column's declared width against the provider before any rows are written.
- **Retrieve** (`IRetriever` / `VectorRetriever`, both in Core): embeds the question as `Query`, then an exact `TOP (@TopK) ... 1 - VECTOR_DISTANCE('cosine', ...) AS Similarity ORDER BY Similarity DESC` with an optional manual filter. No ANN index — an exact scan over 483 rows is both faster and more accurate at this size. Hits come back sorted best-first, which is what reciprocal rank fusion will consume in Project 2. `RetrievalResult` carries a `StageTiming` per stage rather than fixed fields, because a keyword retriever has no embed step and a fused one has stages of its own. Measured on this machine: ~52 ms to embed, ~22 ms to scan once warm; the first query after startup costs about 1.2 s in connection and plan compilation.

## Schema

No `Schema.sql` exists in the repo, even though `ChunkStore`'s error messages tell you to run it. This is what the live database actually contains:

- `dbo.Manuals` — `Id` int identity PK, `Name` nvarchar(200) not null unique, `PageCount` int not null default 0
- `dbo.Chunks` — `Id` bigint identity PK, `ManualId` int not null (FK to `Manuals.Id`, no cascade), `PageFrom` int, `PageTo` int, `Heading` nvarchar(400) null, `Content` nvarchar(max) not null, `Tokens` int, `Embedding` vector(768) null
- `IX_Chunks_ManualId` non-clustered on `Chunks.ManualId`

## Known gaps

Carry these into the next pieces of work rather than rediscovering them.

- **`Schema.sql` is missing.** The database exists only on this machine and can't be recreated from the repo. Deliberately not being added — noted so nobody trusts `ChunkStore`'s "run Schema.sql first" error message.
- **Citations have no display title.** `Manuals.Name` is the PDF file stem, so a citation reads "psrsx920_sx720_en_om_b0, p. 90". Answering and the UI both need something human — a `Title` column or a mapping.
- **Ingestion has hardcoded absolute paths** (`D:\Repos\AskMyRig\data`, `...\output`) and compile-time Ollama settings, while the API takes both from configuration. If they ever drift, chunks and queries get embedded by different models and retrieval quietly degrades.
- **Full-Text Search is not installed.** `SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')` returns 0, so Project 2's hybrid search needs the feature added through SQL Server setup first.
- **No tests.** Chunking, prompt assembly and citation parsing are the deterministic parts the handover wants covered.
- **Two transitive packages carry high-severity advisories** (`dotnet build` reports NU1903): `Microsoft.OpenApi` 2.0.0 via `Microsoft.AspNetCore.OpenApi` in the API, and `Microsoft.Bcl.Memory` 9.0.4 in Ingestion. `PdfExtractor` also uses the obsolete `Letter.Font` (CS0618) instead of `FontDetails`.
- **Leftovers:** `AskMyRig.Api.http` still contains the `weatherforecast` template, `app.MapGet("/", ...).WithName("")` has an empty endpoint name, and `UseDefaultFiles`/`UseStaticFiles` are wired up with no `wwwroot` present.
- **README.md is a stub** and contains an unrelated note about a GTX 1050 Vulkan driver.

## Decisions

- SQL Server 2025 `VECTOR` rather than Postgres/pgvector: keeps the work transferable to the SQL Server stack I use day to day.
- Built on .NET to leverage existing experience. Python enters only in Project 4 (Demucs service) and optionally Project 5.
- The corpus is my own gear manuals. Answers come from them, never from the model's general knowledge.

## Retrieval rules

- Retrieval quality dominates answer quality. When an answer is wrong, inspect the retrieved chunks before touching the prompt.
- Embed queries with exactly the same model, dimension and prefix convention as the stored chunks.
- Exact k-NN (`TOP (@k) ... ORDER BY VECTOR_DISTANCE('cosine', ...)`) is fine at this size; no ANN index needed.
- Every chunk carries its manual and page(s); every citation is manual plus page.
- Keep retrieval behind an interface. Project 2 adds keyword and fused retrievers; Project 3 exposes retrieval as a tool.

## Answering rules

- Answer only from the retrieved chunks. If they don't contain the answer, say so.
- Cite with `[n]` markers that map to numbered sources in the prompt. Validate markers server-side and drop any that don't map to a retrieved chunk.
- Return the retrieved chunks alongside the answer so the UI can show sources and scores.

## Measurement rules (from Project 2 on)

- The baseline is the vector-only pipeline as it stands when Project 2 starts. Record it before adding keyword search, fusion or chunking changes.
- One change per measurement. Log every run (date, commit, config, recall@1/3/5/10, MRR) in `docs/RESULTS.md`.
- Only measured numbers go in the README.

## Repo hygiene

- The manual PDFs stay out of git: `data/` is gitignored and nothing under it is tracked. The manuals are the manufacturers' copyrighted documents; the README links to the official downloads instead.
- `CLAUDE.local.md` and `output/` (generated `chunks.json` and `headings.txt`) are gitignored.

## Roadmap (details in `docs/HANDOVER.md`)

1. Ask My Rig: retrieval foundations (current)
2. Make it measurably good: eval set, recall@k and MRR, hybrid search with RRF, table extraction
3. Gig prep agent: tool calling, with this project's retrieval as one tool
4. Practice companion (flagship): Demucs stem separation in Python, .NET job orchestration over RabbitMQ
5. Mix analyser (optional): LUFS, true peak, spectral balance and dynamics, explained by an LLM
