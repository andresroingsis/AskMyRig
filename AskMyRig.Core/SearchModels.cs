namespace AskMyRig.Core;

/// <param name="TopK">
/// How many chunks to return. Eight is a reasonable default: enough context for
/// an answer later, few enough that you can eyeball whether retrieval worked.
/// </param>
/// <param name="Manual">Optional filter to a single manual by name.</param>
public record SearchRequest(string Question, int TopK = 8, string? Manual = null);

/// <param name="Similarity">
/// 1 minus cosine distance, so 1.0 is identical and 0 is unrelated. Expect
/// good matches somewhere around 0.6-0.8 rather than near 1.0 - a question and
/// its answer are never textually identical.
/// </param>
public record SearchHit(
    long Id,
    string Manual,
    int PageFrom,
    int PageTo,
    string? Heading,
    string Content,
    int Tokens,
    double Similarity);

public record SearchResponse(
    string Question,
    int Count,
    long EmbedMs,
    long SearchMs,
    IReadOnlyList<SearchHit> Hits);
