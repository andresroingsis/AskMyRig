namespace AskMyRig.Core;

/// <param name="TopK">
/// How many chunks to retrieve before filtering by MinSimilarity.
/// Five is a good default: enough context for a complete answer, few
/// enough that the model stays focused rather than rambling.
/// </param>
public record AskRequest(string Question, int TopK = 5, string? Manual = null);

/// <summary>
/// One source the model had access to when producing the answer.
/// Every [Source N] reference in the answer text maps to the Nth item here.
/// </summary>
public record Citation(
    string Manual,
    int PageFrom,
    int PageTo,
    string? Heading,
    double Similarity);

/// <param name="Answer">
/// The generated text. May contain [Source N] references that map to
/// the Citations list. If AnswerFromContext is false, the model could
/// not find relevant information and no real generation happened.
/// </param>
/// <param name="AnswerFromContext">
/// False when nothing in the corpus cleared the similarity threshold.
/// Lets the UI show a different state ("not found") rather than treating
/// an honest "I don't know" the same as a real answer.
/// </param>
public record AskResponse(
    string Question,
    string Answer,
    IReadOnlyList<Citation> Citations,
    long EmbedMs,
    long SearchMs,
    long GenerateMs,
    bool AnswerFromContext);
