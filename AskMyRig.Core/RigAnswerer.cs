using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace AskMyRig.Core;

/// <summary>
/// Orchestrates the full ask pipeline: retrieve relevant chunks, build a
/// grounded prompt, call the language model, return the answer with citations.
///
/// This class deliberately knows nothing about where either model runs. It
/// receives an IRetriever and an IChatClient via the constructor - the retriever
/// could be vector, keyword or fused (Project 2), and the chat client could be
/// Ollama on your laptop, Azure OpenAI or the Anthropic API. Swapping either
/// means changing one line in Program.cs, not touching this class.
/// </summary>
public sealed class RigAnswerer(IRetriever retriever, IChatClient chatClient)
{
    /// <summary>
    /// Chunks below this score are almost certainly coincidental matches.
    /// The "capital of Colombia" test scored 0.52 - this threshold puts
    /// irrelevant questions firmly in "I don't know" territory.
    /// </summary>
    private const double MinSimilarity = 0.55;

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken ct = default)
    {
        // STEP 1: RETRIEVE
        // Get the most semantically similar chunks for the question.
        // The vector retriever calls Ollama to embed the question
        // (EmbeddingKind.Query), then runs the cosine distance scan in SQL.
        var retrieval = await retriever.RetrieveAsync(
            new SearchRequest(request.Question, request.TopK, request.Manual), ct);

        var hits = retrieval.Hits
            .Where(h => h.Similarity >= MinSimilarity)
            .ToList();

        // Nothing in the corpus is relevant enough. Return honestly rather
        // than letting the model fill the gap with its training data.
        if (hits.Count == 0)
        {
            return new AskResponse(
                request.Question,
                "The manuals I have access to don't contain relevant information for this question. "
                + "Try rephrasing, or check the manual directly.",
                [],
                retrieval.Ms("embed"),
                retrieval.Ms("search"),
                0,
                AnswerFromContext: false);
        }

        // STEP 2: BUILD THE PROMPT
        // This is the most important design decision in the whole pipeline.
        // Two messages:
        //   System = stable rules about HOW to answer (never changes)
        //   User   = the context chunks + the question (changes every request)
        //
        // The model only sees the chunks, not the whole corpus. It cannot
        // retrieve anything else. Every fact it states must come from what
        // we give it here, which is why the citations are verifiable.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt()),
            new(ChatRole.User, BuildUserMessage(request.Question, hits))
        };

        // STEP 3: GENERATE
        // This is the only slow step. On CPU with llama3.2:3b, expect
        // 15-60 seconds. The model generates tokens one by one, building
        // the answer from the context we gave it.
        var sw = Stopwatch.StartNew();

        var response = await chatClient.GetResponseAsync(
            messages,
            new ChatOptions
            {
                // Low temperature = more factual, less creative.
                // We want it to stick close to the source material.
                Temperature = 0.1f,

                // 600 tokens is about 450 words - enough for a thorough
                // answer to most manual questions without rambling.
                MaxOutputTokens = 600
            },
            ct);

        sw.Stop();

        // Map each hit to a citation. The [Source N] numbers in the answer
        // text correspond to the 1-based index in this list.
        var citations = hits
            .Select(h => new Citation(h.Manual, h.PageFrom, h.PageTo, h.Heading, h.Similarity))
            .ToList();

        return new AskResponse(
            request.Question,
            response.Text,
            citations,
            retrieval.Ms("embed"),
            retrieval.Ms("search"),
            sw.ElapsedMilliseconds,
            AnswerFromContext: true);
    }

    /// <summary>
    /// The rules the model must follow. Short, specific, and firm.
    ///
    /// Key insight: the model will happily answer from its training data if
    /// you let it. Rule 1 ("ONLY the numbered sources") is what prevents
    /// hallucination. Rule 3 ("[Source N] after every claim") is what makes
    /// errors visible - if it cites wrong, you can catch it by checking.
    /// </summary>
    private static string BuildSystemPrompt() => """
        You are a practical assistant for Yamaha musical equipment manuals.
        The user is sitting at their instrument and needs clear, accurate instructions.

        Rules you must follow without exception:
        1. Answer using ONLY the information in the numbered [Source N] sections the user provides.
        2. After every factual claim, write [Source N] with the correct source number.
        3. If the sources do not contain enough information to answer, say: "The manual sections I was given don't fully cover this - check [source name] around page [X]."
        4. Never invent button names, menu paths, parameter names, or values not present in the sources.
        5. Be concise and practical. Number your steps. Do not repeat the question.
        """;

    /// <summary>
    /// Formats the retrieved chunks into a numbered source list the model can
    /// cite, followed by the question.
    ///
    /// The [Source N] numbering must match the Citations list index (1-based)
    /// so the UI can resolve "[Source 2]" to the second citation card.
    ///
    /// Including the heading in the source header is important: it gives the
    /// model the section context even when the body text doesn't repeat it.
    /// </summary>
    private static string BuildUserMessage(string question, IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var pages = h.PageFrom == h.PageTo
                ? $"p.{h.PageFrom}"
                : $"p.{h.PageFrom}-{h.PageTo}";

            sb.AppendLine($"[Source {i + 1}] {h.Manual} - {pages}");

            if (!string.IsNullOrWhiteSpace(h.Heading))
            {
                sb.AppendLine($"Section: {h.Heading}");
            }

            sb.AppendLine(h.Content);
            sb.AppendLine();
        }

        sb.AppendLine($"Question: {question}");

        return sb.ToString();
    }
}
