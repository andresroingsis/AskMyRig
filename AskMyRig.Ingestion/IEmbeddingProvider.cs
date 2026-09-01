namespace AskMyRig.Ingestion;

/// <summary>
/// Which role the text plays in a search.
///
/// This matters because retrieval is asymmetric: a question and the passage
/// answering it look nothing alike. Models such as nomic-embed-text were
/// trained with task prefixes so they can encode the two differently. Getting
/// this wrong costs real accuracy - queries embedded as documents land in
/// slightly the wrong region of the space.
/// </summary>
public enum EmbeddingKind
{
    /// <summary>Text being indexed into the corpus.</summary>
    Document,

    /// <summary>A question being asked against the corpus.</summary>
    Query
}

/// <summary>
/// Anything that can turn text into vectors.
///
/// The interface exists so the rest of the pipeline never knows or cares where
/// the model runs. Swapping Ollama for a hosted API, or for ONNX Runtime
/// in-process, means writing one new class and changing one line in Program.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Vector length this provider produces. Must match the VECTOR(n) column
    /// in the database - a mismatch is the most common failure in this kind of
    /// system, so it is exposed rather than assumed.
    /// </summary>
    int Dimensions { get; }

    string ModelName { get; }

    /// <summary>Throws with a readable message if the model is not reachable.</summary>
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task<List<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        EmbeddingKind kind,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
