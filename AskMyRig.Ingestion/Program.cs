using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.ML.Tokenizers;

namespace AskMyRig.Ingestion;

public static class Program
{
    private const string DataFolder = @"D:\Repos\AskMyRig\data";
    private const string OutputFolder = @"D:\Repos\AskMyRig\output";
    // Used only for token counting during chunking. Any cl100k-based tokenizer
    // gives a close enough estimate of chunk size; it does not have to match
    // the embedding model.
    private const string TokenizerModel = "text-embedding-3-small";

    private const string OllamaUrl = "http://localhost:11434";
    private const string EmbeddingModel = "nomic-embed-text";
    private const int EmbeddingDimensions = 768;

    /// <summary>
    /// Usage:
    ///   dotnet run parse    - PDFs to chunks.json (free, no network)
    ///   dotnet run ingest   - chunks.json to SQL Server, embedding as it goes
    ///   dotnet run          - both
    ///
    /// Keeping these separate means re-parsing costs nothing, and re-embedding
    /// does not require re-reading 145 pages of PDF.
    /// </summary>
    public static async Task Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        if (mode is "parse" or "all")
        {
            Parse();
        }

        if (mode is "ingest" or "all")
        {
            await IngestAsync();
        }
    }

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

    // ---------- parse ----------

    private static void Parse()
    {
        Directory.CreateDirectory(OutputFolder);

        var tokenizer = TiktokenTokenizer.CreateForModel(TokenizerModel);
        var pdfPaths = Directory.GetFiles(DataFolder, "*.pdf").OrderBy(path => path).ToList();

        if (pdfPaths.Count == 0)
        {
            Console.WriteLine($"No PDFs found in {DataFolder}");
            return;
        }

        var allChunks = new List<Chunk>();
        var allHeadings = new List<string>();

        foreach (var pdfPath in pdfPaths)
        {
            var manualName = Path.GetFileNameWithoutExtension(pdfPath);
            Console.WriteLine($"=== {manualName} ===");

            var pages = PdfExtractor.Extract(pdfPath);
            var totalBlocks = pages.Sum(page => page.Blocks.Count);
            var emptyPages = pages.Count(page => page.Blocks.Count == 0);

            Console.WriteLine($"pages: {pages.Count}, blocks: {totalBlocks}, pages with no text: {emptyPages}");

            if (emptyPages == pages.Count)
            {
                Console.WriteLine("  SKIPPED: image-only PDF\n");
                continue;
            }

            var boilerplate = TextCleaner.FindBoilerplate(pages);
            Console.WriteLine($"boilerplate patterns removed: {boilerplate.Count}");
            foreach (var pattern in boilerplate.Take(12))
            {
                Console.WriteLine($"  \"{Truncate(pattern, 70)}\"");
            }

            var segments = SegmentBuilder.Build(pages, boilerplate);
            var headingShare = totalBlocks == 0 ? 0 : 100.0 * SegmentBuilder.LastHeadingCount / totalBlocks;
            Console.WriteLine($"segments: {segments.Count}, headings: {SegmentBuilder.LastHeadingCount} ({headingShare:F1}% of blocks)");

            var chunks = ChunkBuilder.Build(manualName, segments, tokenizer);
            ReportChunks(chunks);

            allChunks.AddRange(chunks);
            allHeadings.AddRange(segments
                .Where(segment => segment.StartsSection)
                .Select(segment => $"{manualName} p.{segment.Page}  {Truncate(segment.Text.Replace("\n", " "), 90)}"));

            Console.WriteLine();
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(OutputFolder, "chunks.json"), JsonSerializer.Serialize(allChunks, options));
        File.WriteAllLines(Path.Combine(OutputFolder, "headings.txt"), allHeadings);

        Console.WriteLine($"total chunks: {allChunks.Count}, total tokens: {allChunks.Sum(chunk => chunk.Tokens):N0}\n");
    }

    // ---------- ingest ----------

    private static async Task IngestAsync()
    {
        var configuration = BuildConfiguration();

        // No API key. The model runs on this machine.
        var connectionString = configuration["Sql:ConnectionString"]
            ?? "Server=localhost\\MSSQLSERVER01;Database=AskMyRig;Trusted_Connection=True;TrustServerCertificate=True;";

        var chunksPath = Path.Combine(OutputFolder, "chunks.json");

        if (!File.Exists(chunksPath))
        {
            Console.WriteLine($"{chunksPath} not found - run 'parse' first.");
            return;
        }

        var chunks = JsonSerializer.Deserialize<List<Chunk>>(File.ReadAllText(chunksPath))!;
        Console.WriteLine($"loaded {chunks.Count} chunks");

        using var provider = new OllamaEmbeddingProvider(
            OllamaUrl, EmbeddingModel, EmbeddingDimensions);

        // Fail fast with a readable message rather than mid-run.
        await provider.EnsureReadyAsync();
        Console.WriteLine($"using {provider.ModelName} ({provider.Dimensions} dimensions) at {OllamaUrl}");

        var store = new ChunkStore(connectionString, provider.Dimensions);
        await store.EnsureDimensionsMatchAsync();

        foreach (var group in chunks.GroupBy(chunk => chunk.Manual))
        {
            var manualChunks = group.ToList();
            var pageCount = manualChunks.Max(chunk => chunk.PageTo);

            Console.WriteLine($"=== {group.Key} ({manualChunks.Count} chunks) ===");

            var manualId = await store.UpsertManualAsync(group.Key, pageCount);

            // Idempotent: a re-run replaces this manual's chunks rather than
            // duplicating them.
            await store.DeleteChunksAsync(manualId);

            var progress = new Progress<int>(done =>
                Console.WriteLine($"  embedded {done}/{manualChunks.Count}"));

            // EmbeddingKind.Document because these are corpus passages.
            // Questions get embedded as Query at search time.
            var vectors = await provider.EmbedAsync(
                manualChunks.Select(chunk => chunk.Content).ToList(),
                EmbeddingKind.Document,
                progress);

            await store.InsertChunksAsync(manualId, manualChunks, vectors);
            Console.WriteLine($"  stored {manualChunks.Count} rows");
        }

        var counts = await store.GetCountsAsync();
        Console.WriteLine($"\nmanuals: {counts.Manuals}, chunks: {counts.Chunks}, with embeddings: {counts.Embedded}");
    }

    // ---------- reporting ----------

    private static void ReportChunks(List<Chunk> chunks)
    {
        if (chunks.Count == 0)
        {
            Console.WriteLine("chunks: 0  <-- something upstream is wrong");
            return;
        }

        var tokens = chunks.Select(chunk => chunk.Tokens).OrderBy(count => count).ToList();

        Console.WriteLine(
            $"chunks: {chunks.Count}  tokens min/median/max: " +
            $"{tokens.First()}/{tokens[tokens.Count / 2]}/{tokens.Last()}  total: {tokens.Sum():N0}");

        var wide = chunks.Count(chunk => chunk.PageTo - chunk.PageFrom > 3);
        if (wide > 0)
        {
            Console.WriteLine($"  WARNING: {wide} chunks span more than 3 pages");
        }
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length] + "...";
}