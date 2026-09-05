using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace AskMyRig.Core;

/// <summary>Ollama replied, but not with a vector. Carries its error text.</summary>
public sealed class OllamaException(string message) : Exception(message);

/// <summary>
/// Calls a locally running Ollama instance.
///
/// Deliberately plain HttpClient. Ollama's embedding endpoint is a single POST,
/// and seeing the request and response shape is worth more here than an
/// abstraction that hides them.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly int _batchSize;

    public int Dimensions { get; }
    public string ModelName { get; }

    public OllamaEmbeddingProvider(
        string baseUrl = "http://localhost:11434",
        string model = "nomic-embed-text",
        int dimensions = 768,
        int batchSize = 8)
    {
        ModelName = model;
        Dimensions = dimensions;
        _batchSize = batchSize;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        TagsResponse? tags;

        try
        {
            tags = await _http.GetFromJsonAsync<TagsResponse>("/api/tags", cancellationToken);
        }
        catch (Exception ex)
        {
            throw new OllamaException(
                $"Cannot reach Ollama at {_http.BaseAddress}. Is it running? Try: ollama list ({ex.Message})");
        }

        var installed = tags?.Models?.Select(model => model.Name).ToList() ?? [];

        var found = installed.Any(name =>
            name.Split(':')[0].Equals(ModelName.Split(':')[0], StringComparison.OrdinalIgnoreCase));

        if (!found)
        {
            throw new OllamaException(
                $"Model '{ModelName}' is not installed. Run: ollama pull {ModelName}\n" +
                $"Installed: {(installed.Count == 0 ? "(none)" : string.Join(", ", installed))}");
        }

        // Prove the endpoint works before processing hundreds of chunks.
        var probe = await PostAsync([ApplyTaskPrefix("connection test", EmbeddingKind.Document)], cancellationToken);

        if (probe[0].Length != Dimensions)
        {
            throw new OllamaException(
                $"Model '{ModelName}' returns {probe[0].Length}-dimension vectors but this provider is " +
                $"configured for {Dimensions}. Update the constructor and the VECTOR(n) column to " +
                $"{probe[0].Length}, then re-run Schema.sql.");
        }
    }

    public async Task<List<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        EmbeddingKind kind,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>(texts.Count);

        for (var offset = 0; offset < texts.Count; offset += _batchSize)
        {
            var slice = texts.Skip(offset).Take(_batchSize).ToList();
            var prepared = slice.Select(text => ApplyTaskPrefix(Sanitise(text), kind)).ToList();

            try
            {
                results.AddRange(await PostAsync(prepared, cancellationToken));
            }
            catch (OllamaException ex)
            {
                // A whole batch failed. Retry one at a time so the offending
                // chunk gets named instead of 8 chunks disappearing together.
                Console.WriteLine($"  batch at index {offset} failed: {ex.Message}");
                Console.WriteLine("  retrying individually to isolate the cause...");

                for (var i = 0; i < prepared.Count; i++)
                {
                    try
                    {
                        results.AddRange(await PostAsync([prepared[i]], cancellationToken));
                    }
                    catch (OllamaException single)
                    {
                        var preview = prepared[i].Length <= 120 ? prepared[i] : prepared[i][..120] + "...";

                        throw new OllamaException(
                            $"Chunk at index {offset + i} could not be embedded.\n" +
                            $"  Ollama said: {single.Message}\n" +
                            $"  Length: {prepared[i].Length} chars\n" +
                            $"  Starts: {preview}");
                    }
                }
            }

            progress?.Report(results.Count);
        }

        return results;
    }

    private async Task<List<float[]>> PostAsync(List<string> batch, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(
            "/api/embed",
            new EmbedRequest(ModelName, batch),
            cancellationToken);

        // Read the body BEFORE throwing. Ollama returns {"error":"..."} with a
        // 500, and that text is the whole diagnosis. EnsureSuccessStatusCode
        // discards it, which is why the previous version told us nothing.
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var trimmed = body.Length <= 400 ? body : body[..400] + "...";

            throw new OllamaException($"HTTP {(int)response.StatusCode} - {trimmed}");
        }

        var payload = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken)
            ?? throw new OllamaException("Ollama returned an empty response body.");

        if (payload.Embeddings is null || payload.Embeddings.Count != batch.Count)
        {
            throw new OllamaException(
                $"Asked for {batch.Count} vectors, received {payload.Embeddings?.Count ?? 0}.");
        }

        return payload.Embeddings;
    }

    /// <summary>
    /// Strips characters that survive PDF extraction but break tokenizers.
    ///
    /// Yamaha manuals use symbol fonts for the button icons, and those glyphs
    /// land in the Unicode private use area - meaningless to an embedding model
    /// and a plausible cause of a 500. Control characters and lone surrogates
    /// (halves of a pair, produced when extraction splits text mid-character)
    /// are invalid UTF-8 and will upset a JSON parser or tokenizer.
    /// </summary>
    private static string Sanitise(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch is '\n' or '\r' or '\t')
            {
                builder.Append(' ');
                continue;
            }

            // Keep valid surrogate pairs intact; drop unpaired halves.
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(ch).Append(text[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(ch) || char.IsControl(ch))
            {
                continue;
            }

            // Private use area - symbol font glyphs.
            if (ch is >= '\uE000' and <= '\uF8FF')
            {
                continue;
            }

            builder.Append(ch);
        }

        var cleaned = builder.ToString().Trim();

        // An empty input is another way to earn a 500. Keep the index aligned
        // with a placeholder rather than silently dropping a row.
        return cleaned.Length == 0 ? "(no text)" : cleaned;
    }

    private string ApplyTaskPrefix(string text, EmbeddingKind kind)
    {
        if (!ModelName.Contains("nomic", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return kind == EmbeddingKind.Query
            ? $"search_query: {text}"
            : $"search_document: {text}";
    }

    public void Dispose() => _http.Dispose();

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embeddings")] List<float[]>? Embeddings);

    private sealed record TagsResponse(
        [property: JsonPropertyName("models")] List<TagModel>? Models);

    private sealed record TagModel(
        [property: JsonPropertyName("name")] string Name);
}