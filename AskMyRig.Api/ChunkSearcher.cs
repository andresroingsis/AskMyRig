using System.Diagnostics;
using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AskMyRig.Core;

/// <summary>
/// Semantic search over the ingested chunks.
///
/// Two steps, timed separately on purpose. Embedding the question is a call out
/// to the model; the SQL scan is local. When something feels slow you want to
/// know which half is responsible.
/// </summary>
public sealed class ChunkSearcher(string connectionString, IEmbeddingProvider provider)
{
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw new ArgumentException("Question cannot be empty.", nameof(request));
        }

        var embedWatch = Stopwatch.StartNew();

        // EmbeddingKind.Query, not Document. This is the whole reason the enum
        // exists: nomic prefixes the question with "search_query:" so the model
        // encodes it as a question rather than as a passage. Embedding a
        // question as a document puts it in slightly the wrong place and
        // quietly costs accuracy.
        var vectors = await provider.EmbedAsync(
            [request.Question],
            EmbeddingKind.Query,
            cancellationToken: cancellationToken);

        embedWatch.Stop();

        var searchWatch = Stopwatch.StartNew();

        // No vector index, so this is an exact scan over all 483 rows. At this
        // size that is both faster and more accurate than approximating.
        var sql = $"""
            SELECT TOP (@TopK)
                   c.Id,
                   m.Name AS Manual,
                   c.PageFrom,
                   c.PageTo,
                   c.Heading,
                   c.Content,
                   c.Tokens,
                   1 - VECTOR_DISTANCE('cosine', c.Embedding, CAST(@Query AS VECTOR({provider.Dimensions}))) AS Similarity
            FROM dbo.Chunks c
            JOIN dbo.Manuals m ON m.Id = c.ManualId
            WHERE c.Embedding IS NOT NULL
              AND (@Manual IS NULL OR m.Name = @Manual)
            ORDER BY Similarity DESC;
            """;

        var parameters = new DynamicParameters();
        parameters.Add("TopK", Math.Clamp(request.TopK, 1, 50));
        parameters.Add("Manual", request.Manual);

        // NVARCHAR(MAX). The literal is several thousand characters and Dapper
        // would otherwise cap it at 4,000, truncating the array mid-number.
        parameters.Add("Query", VectorLiteral.From(vectors[0]), DbType.String, size: -1);

        await using var connection = new SqlConnection(connectionString);
        var hits = (await connection.QueryAsync<SearchHit>(sql, parameters)).ToList();

        searchWatch.Stop();

        return new SearchResponse(
            request.Question,
            hits.Count,
            embedWatch.ElapsedMilliseconds,
            searchWatch.ElapsedMilliseconds,
            hits);
    }
}
