namespace AskMyRig.Core;

/// <summary>
/// How long one stage of a retrieval took.
///
/// Stages differ by strategy, which is why this is a list rather than fixed
/// fields: the vector retriever embeds the question and then scans, a keyword
/// retriever only queries, and a fused retriever reports its children's stages
/// plus the fusion step. Keeping them separate is what tells you whether a slow
/// search is the model or the database.
/// </summary>
public sealed record StageTiming(string Stage, long Ms);

/// <summary>Ordered hits, best first, together with where the time went.</summary>
public sealed record RetrievalResult(IReadOnlyList<SearchHit> Hits, IReadOnlyList<StageTiming> Timings)
{
    /// <summary>Milliseconds for a named stage, or 0 if this retriever has no such stage.</summary>
    public long Ms(string stage) =>
        Timings.FirstOrDefault(timing => timing.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase))?.Ms ?? 0;
}

/// <summary>
/// A strategy for finding the chunks most likely to answer a question.
///
/// The interface exists because retrieval is the part of this system that will
/// change most. Project 2 adds a keyword retriever over SQL Server full-text
/// search and a third that fuses the two rankings; Project 3 wraps retrieval as
/// a tool the model can call. All of them return the same shape, so the endpoint
/// that calls this never has to know which one it got.
///
/// Implementations return hits already sorted best-first, because rank order is
/// what reciprocal rank fusion consumes in Project 2 - it fuses by position, not
/// by score, since a full-text RANK and a cosine similarity are not comparable
/// numbers.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// Short identifier for the strategy, e.g. "vector". Goes in logs and in the
    /// configuration column of docs/RESULTS.md, so a measured run can be traced
    /// back to what produced it.
    /// </summary>
    string Name { get; }

    Task<RetrievalResult> RetrieveAsync(SearchRequest request, CancellationToken cancellationToken = default);
}
