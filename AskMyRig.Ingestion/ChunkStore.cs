using AskMyRig.Core;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Data;

namespace AskMyRig.Ingestion;

public sealed class ChunkStore(string connectionString, int dimensions)
{
    public async Task<int> UpsertManualAsync(string name, int pageCount)
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            MERGE dbo.Manuals AS target
            USING (SELECT @Name AS Name, @PageCount AS PageCount) AS source
            ON target.Name = source.Name
            WHEN MATCHED THEN UPDATE SET PageCount = source.PageCount
            WHEN NOT MATCHED THEN INSERT (Name, PageCount) VALUES (source.Name, source.PageCount)
            OUTPUT inserted.Id;
            """;

        return await connection.ExecuteScalarAsync<int>(sql, new { Name = name, PageCount = pageCount });
    }

    public async Task DeleteChunksAsync(int manualId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync("DELETE FROM dbo.Chunks WHERE ManualId = @ManualId", new { ManualId = manualId });
    }

    /// <summary>
    /// Verifies the Embedding column's declared size matches the embedding
    /// provider. Catching this here gives a clear message instead of a cast
    /// failure several hundred rows into ingestion.
    /// </summary>
    public async Task EnsureDimensionsMatchAsync()
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            SELECT c.max_length
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.name = 'Chunks' AND c.name = 'Embedding';
            """;

        var maxLength = await connection.ExecuteScalarAsync<int?>(sql);

        if (maxLength is null)
        {
            throw new InvalidOperationException("dbo.Chunks.Embedding not found - run Schema.sql first.");
        }

        // VECTOR(n) stores n four-byte floats plus an eight-byte header.
        var declared = (maxLength.Value - 8) / 4;

        if (declared != dimensions)
        {
            throw new InvalidOperationException(
                $"Schema declares VECTOR({declared}) but the embedding provider produces {dimensions}. " +
                $"Update Schema.sql and re-run it.");
        }
    }

    public async Task InsertChunksAsync(int manualId, IReadOnlyList<Chunk> chunks, IReadOnlyList<float[]> vectors)
    {
        if (chunks.Count != vectors.Count)
        {
            throw new ArgumentException("Chunk and vector counts must match.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var sql = $"""
            INSERT INTO dbo.Chunks (ManualId, PageFrom, PageTo, Heading, Content, Tokens, Embedding)
            VALUES (@ManualId, @PageFrom, @PageTo, @Heading, @Content, @Tokens, CAST(@Embedding AS VECTOR({dimensions})));
            """;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            var parameters = new DynamicParameters();
            parameters.Add("ManualId", manualId);
            parameters.Add("PageFrom", chunk.PageFrom);
            parameters.Add("PageTo", chunk.PageTo);
            parameters.Add("Heading", chunk.Heading, DbType.String, size: 400);
            parameters.Add("Content", chunk.Content, DbType.String, size: -1);
            parameters.Add("Tokens", chunk.Tokens);

            // size: -1 means NVARCHAR(MAX). The literal runs to several thousand
            // characters; without this Dapper caps it at 4,000 and the CAST
            // fails on a truncated array.
            parameters.Add("Embedding", VectorLiteral.From(vectors[i]), DbType.String, size: -1);

            await connection.ExecuteAsync(sql, parameters, transaction);
        }

        await transaction.CommitAsync();
    }

    public async Task<(int Manuals, int Chunks, int Embedded)> GetCountsAsync()
    {
        await using var connection = new SqlConnection(connectionString);

        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM dbo.Manuals),
                (SELECT COUNT(*) FROM dbo.Chunks),
                (SELECT COUNT(*) FROM dbo.Chunks WHERE Embedding IS NOT NULL);
            """;

        await using var reader = await connection.ExecuteReaderAsync(sql);
        await reader.ReadAsync();

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}