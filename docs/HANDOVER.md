# Ask My Rig: Handover

Moved from claude.ai to Claude Code on 21 September 2026. This document carries everything that was decided or planned outside the code. It's a snapshot: `CLAUDE.md` holds the live status, and wherever this file and the code disagree, the code wins.

Anything marked **proposed** was suggested at handover time and hasn't been agreed yet. Treat it as a starting point, not a requirement.

Contents: 1. Summary · 2. Decisions · 3. Project 1 status · 4. Project 1 remaining work · 5. Roadmap, Projects 2–5 · 6. The rig · 7. Open decisions

---

## 1. Summary

Ask My Rig answers natural-language questions about my own music-gear manuals and cites the manual and page each answer came from. It's the first of five portfolio projects that build applied-AI skills on top of an existing .NET background, and it's meant to become a real, finished portfolio piece.

The learning goal is to understand the retrieval pipeline end to end (embeddings, chunking, vector search and grounded generation) and, above all, why retrieval quality dominates everything downstream. If the right chunk never comes back from search, no prompt can rescue the answer.

## 2. Decisions

| Decision | Reason |
|---|---|
| Build on .NET rather than a Python-first stack | Build on existing .NET experience instead of starting over. Python appears only in Project 4, and optionally Project 5 |
| SQL Server 2025 `VECTOR` over Postgres/pgvector | Keeps the work transferable to the SQL Server stack I use day to day. Standard Developer edition is already installed locally |
| Corpus: my own gear manuals, three PDFs | The core idea: ask natural-language questions of my own manuals and get cited answers |
| .NET 10, PdfPig, Dapper, ASP.NET Core minimal API, Ollama or ONNX embeddings, a small React or Blazor front end | The stack chosen at the start |

## 3. Project 1 status

Complexity 2 (out of 5, relative to my .NET ability). About 70% done.

Ingestion is in place: PDFs are parsed with PdfPig, split into chunks, embedded, and stored with their vectors in SQL Server 2025 through Dapper. This is inferred from the remaining-work list, so confirm it against the code.

Remaining, in order: the search endpoint, LLM answering with citations, and the UI. Estimate: three or four weekends.

Implementation details that live only in the code (embedding model and dimension, chunking parameters, schema, commands, which three manuals) are listed under Unknowns in `CLAUDE.md`, to be resolved from the repo in the first session.

## 4. Project 1: remaining work (proposed specs)

### 4.1 Search endpoint

Given a question, return the top-k chunks with enough metadata to cite them. Project 2 measures exactly this component, so build it as its own service rather than inline in the answer endpoint.

- Route: `GET /api/search?q=...&k=5`, returning `[{ chunkId, manual, page, score, text }]`.
- Embed the query with the same model, dimension and prefix convention used at ingestion. Some models expect task prefixes; nomic-embed-text, for example, wants `search_query: ` on queries and `search_document: ` on chunks. A mismatch doesn't throw an error, it just quietly lowers recall.
- Exact nearest-neighbour search is plenty for three manuals. An illustrative query:

```sql
-- Illustrative only: adapt names to the real schema,
-- and pass the query vector the same way ingestion passes vectors.
SELECT TOP (@k)
       c.Id, d.Title, c.PageNumber, c.Text,
       VECTOR_DISTANCE('cosine', c.Embedding, @queryVector) AS Distance
FROM dbo.Chunks AS c
JOIN dbo.Documents AS d ON d.Id = c.DocumentId
ORDER BY Distance;
```

- Expose similarity as `1 - distance`, so higher means better in the API and UI.
- Put retrieval behind an interface (for example `IRetriever`) so Project 2 can add keyword and fused retrievers without touching callers, and Project 3 can wrap it as a tool.
- Done when: for five questions with a known answer page, that page appears in the top five results, and every result carries manual and page.

### 4.2 Answering with citations

- Route: `POST /api/ask` with `{ question, k }`, returning `{ answer, citations: [{ n, manual, page, chunkId, snippet }], retrieved: [...] }`.
- Pipeline: search, then build a prompt with numbered sources, generate, validate citations, respond.
- Label each source in the prompt, e.g. `[2] <manual title>, p. 87`, followed by the chunk text. Instruct the model to answer only from the sources, cite every claim with `[n]`, and say plainly when the sources don't cover the question.
- Validate server-side: parse the `[n]` markers, drop any that don't map to a source, and return only the sources actually cited. An answer with no valid citation is a "not found", not a fact.
- Use a low temperature for grounded answers. Streaming the response is a nice-to-have.
- Call the model through Microsoft.Extensions.AI's `IChatClient`, so the provider (a local Ollama model or a hosted API) stays swappable. Project 3's tool calling builds on the same abstraction.
- The generation model isn't chosen yet (see Open decisions).
- Done when: real questions get correct page citations; an out-of-scope question ("what's the capital of France?") gets a clear "not in the manuals"; unit tests cover prompt assembly and citation parsing.

### 4.3 UI

- One screen: a question box, the answer with clickable `[n]` citation chips, and a sources list with manual, page, snippet and similarity score.
- A citation click opens the PDF at the cited page: serve the manuals as static files and link to `/manuals/<file>.pdf#page=<n>`, which most browser PDF viewers honour.
- A "show retrieved chunks" toggle with scores, for debugging now and for explaining retrieval in demos.
- React vs Blazor is still open (see Open decisions).
- Done when: someone new to the project can ask a question and check the answer against the manual page in two clicks.

### 4.4 Definition of done for Project 1

- Ask, get a grounded answer, verify the citation: end to end in the UI.
- Out-of-scope questions are declined rather than answered from general knowledge.
- A README with what it does, a screenshot or GIF, an architecture sketch (ingest → chunk → embed → store → retrieve → generate → cite) and local setup steps. It links to the official manual downloads; the PDFs themselves stay out of the repo.
- Unit tests for the deterministic parts: chunking, prompt assembly, citation parsing.

## 5. Roadmap: Projects 2–5

Complexity is out of 5, relative to my .NET ability rather than absolute difficulty. The skills map: Projects 1–2 cover retrieval, grounding and evaluation; Project 3 covers tool calling; Project 4 covers model serving, Python interop and async orchestration; Project 5 covers audio measurement fundamentals.

### Project 2: Make it measurably good

Complexity 3. About two weekends.

Build an evaluation set of 25 questions with known answer pages, measure recall@k and mean reciprocal rank (MRR), then improve against the numbers. Add keyword search alongside vector search and fuse the rankings. Fix the parameter-table extraction. A measured improvement ("recall@5 went from X to Y after adding hybrid search") is what separates this from every other RAG side project.

Tech: SQL Server full-text search, reciprocal rank fusion, xUnit for the eval harness.

Design notes (proposed):

**Eval set**

- A versioned file such as `eval/questions.json`, one entry per question: id, question, manual, answer page(s), category (lookup, procedure, parameter table, spans pages), notes.
- Questions written by hand, phrased the way they'd really be asked. Not lifted from manual sentences and not generated from chunks: both copy the chunks' wording and inflate the scores.
- Several parameter-table questions, so the table fix shows up in the numbers.
- Freeze the set before tuning. With 25 questions it's a development set; say so when reporting.

**Metrics**

- A retrieved chunk is relevant if it comes from the right manual and its page (or page range) overlaps an answer page.
- recall@k: the fraction of questions with at least one relevant chunk in the top k. This hit-rate definition is the usual one when each question has one known answer location; state it in the README. Report k = 1, 3, 5 and 10.
- MRR: the mean over questions of 1 / (rank of the first relevant chunk), scoring 0 when nothing relevant is in the top 10.

**Harness**

- An xUnit project whose eval tests carry `[Trait("Category", "Eval")]`, so everyday runs skip them with `dotnet test --filter "Category!=Eval"`. They need SQL Server and the embedding model running.
- Every run appends a row to `docs/RESULTS.md`: date, commit, configuration (retriever, chunk size and overlap, k, fusion constant), recall@1/3/5/10 and MRR.
- Once a baseline exists, an optional regression gate fails the run if recall@5 drops below it.

**Order of work** (one change per measurement, so each gain can be attributed)

1. Build and freeze the eval set.
2. Measure the vector-only baseline: the Project 1 pipeline, unchanged.
3. Add full-text search and reciprocal rank fusion, then measure.
4. Fix parameter-table extraction, re-ingest, then measure.
5. Only then tune chunk size, overlap and k, measuring each change.

**Hybrid search**

- Check Full-Text Search is installed: `SELECT FULLTEXTSERVICEPROPERTY('IsFullTextInstalled');` returns 1 if it is. If it returns 0, add the Full-Text Search feature through SQL Server setup.
- A full-text index needs a unique, single-column, non-nullable key index on the chunks table.
- Start with `FREETEXTTABLE` for natural-language questions (it handles inflected word forms); `CONTAINSTABLE` gives finer control later.
- Why hybrid should help: manuals are full of exact tokens (parameter names, menu paths, model numbers) where keyword matching beats embeddings, while embeddings win on paraphrased questions.
- Fuse by rank, not score, because full-text `RANK` and cosine distance live on different scales. Reciprocal rank fusion: `score(d) = Σ 1 / (k + rank_i(d))` across retrievers, with k = 60, the constant from the original RRF paper. Take the top 20–50 from each retriever, fuse, return the top k. Write it as a pure C# function so it's trivial to unit-test.

**Parameter-table extraction**

- Known problem: parameter tables don't come out of extraction usefully. First inspect the actual chunks from a parameter-table page to see how they break (jumbled column order, merged rows, lost headers).
- Candidate fixes: rebuild rows from PdfPig's word bounding boxes (group words by baseline, sort by x, split columns at gaps); PdfPig's document layout analysis tools (page segmenters, reading-order detection); or a PdfPig-based table extractor such as Tabula Sharp, evaluated before adopting.
- Chunk tables by row or small row group, repeating the table title, section heading and column headers in every chunk so each chunk stands alone.
- The eval set's parameter-table questions are the proof that it worked.

### Project 3: Gig prep agent

Complexity 3. About three weekends.

Tool calling: the model decides which function to invoke rather than just answering. The plan's rationale: it's the fastest-growing area of applied AI work.

Tech as planned: Microsoft.Extensions.AI function calling or Semantic Kernel, structured JSON outputs, and Project 1's retrieval exposed as one of the tools.

Shape: give it a setlist, and it looks up keys and tempos, transposes charts for the 5-string bass, and answers questions like "how do I save this as a registration" from the manuals (which needs the keyboard's manual in the corpus).

**Update since the plan was written:** Semantic Kernel is now in maintenance mode. Microsoft Agent Framework 1.0, generally available since April 2026, is its successor and gets all new feature work. For a new project the realistic choice is plain Microsoft.Extensions.AI function calling (lighter, fewer concepts) or Agent Framework (fuller agent features, built on the same Microsoft.Extensions.AI abstractions). Re-check the landscape when this project starts.

Design notes (proposed):

- Candidate tools: `LookupSong(title, artist)` returning key and tempo; `TransposeChart(chart, fromKey, toKey)`; `SearchManuals(question)` wrapping the Project 1–2 retriever and returning chunks with citations.
- The model chooses and sequences the tools; the music theory stays in deterministic C#. Transposition is code, not generation. Every tool gets unit tests that don't involve the model.
- The final result is a typed object (for example a gig sheet: song, key, tempo, transposed chart, notes) produced with structured JSON output, so the UI can render it.
- With a local model, choose one with dependable tool calling; small models fumble multi-step tool use. Carry Project 2's habit over with a handful of scenario tests (setlist in, expected tool calls and output out).

### Project 4: Practice companion (the flagship)

Complexity 4. About six to eight weekends, and worth every one.

Serving a real ML model, Python interop and asynchronous job orchestration. The memorable one, because very few people work at the intersection of .NET and audio ML.

Tech: Demucs in a Python FastAPI service, .NET orchestrating jobs over RabbitMQ, Docker Compose, blob storage, a React front end with waveform display.

Shape: upload a track, get separated stems, then mute the bass and play along, loop sections, and slow down without pitch shift.

Design notes (proposed):

- Flow: React → .NET API (upload, job status, stem URLs) → blob storage → RabbitMQ job → Demucs service → stems back to blob storage → status to the UI via polling or SignalR.
- Job lifecycle: queued, processing, then completed or failed, with retries and a dead-letter queue.
- Separation on a CPU takes minutes per track, which is exactly why it's an async job rather than a request.
- Local blob storage in Compose: Azurite or MinIO.
- Demucs's standard models produce four stems (drums, bass, other, vocals); an experimental six-stem model adds guitar and piano. Confirm the project's current maintenance status and model options before starting.
- Biggest technical risk, so spike it first: synchronised multi-stem playback that changes tempo without changing pitch, in the browser. Web Audio's `playbackRate` shifts pitch, so this needs a time-stretch algorithm (for example a SoundTouch or Rubber Band port in an AudioWorklet) or server-side pre-rendered slower versions of the stems.
- Waveform display: for example wavesurfer.js.
- Public demos use my own recordings or royalty-free tracks, not commercial songs.

### Project 5: Mix analyser (optional)

Complexity 4. About four weekends.

Learn sound-engineering fundamentals by being forced to implement them. Measure LUFS, true peak, spectral balance and dynamic range, then have an LLM translate the numbers into plain advice. Building the measurement tool teaches more than a course would.

Tech: NAudio or a Python analysis service, plus LLM interpretation.

Design notes (proposed):

- Standards to implement: ITU-R BS.1770 for loudness (K-weighting and gated 400 ms blocks, giving integrated, short-term and momentary LUFS) and oversampled true peak in dBTP; EBU Tech 3342 for loudness range (LRA); FFT band energies for spectral balance; crest factor and peak-to-loudness ratio for dynamics.
- Implement the measurements rather than calling a library for them, since that's the point of the project, then validate against references such as ffmpeg's `ebur128` filter and pyloudnorm, with xUnit tests on reference files and explicit tolerances.
- Same principle as Project 3: code measures, the LLM explains. Give it the numbers plus a small reference table of targets (by genre and streaming platform) and have it cite the table.

## 6. The rig

The gear the manuals come from:

- Yamaha PSR-SX920 arranger keyboard
- Yamaha TRBX305 5-string bass
- Yamaha NTX1 nylon-string acoustic-electric guitar
- Universal Audio VOLT 2/76 audio interface
- Yamaha Stagepas 1K portable PA
- Assorted instrument and vocal mics
- Luna DAW on a Windows 11 laptop

Three of these manuals are in the corpus; which three is one of the Unknowns in `CLAUDE.md`.

## 7. Open decisions

- Generation model and host for Project 1 answers: a local model via Ollama, or a hosted API.
- React vs Blazor for the Project 1 UI. React reuses existing skills and is the planned stack for Project 4; Blazor keeps the whole app in C#.
- Project 3: Microsoft.Extensions.AI vs Microsoft Agent Framework; where keys and tempos come from; what "transposed for the 5-string" should actually output.
- Project 4: whether the Python service consumes RabbitMQ directly, or a .NET worker consumes and calls FastAPI (keeping orchestration in .NET and the Python side a stateless model server); client-side vs server-side time-stretching; which separation model.
- Project 5: NAudio vs a Python analysis service.
