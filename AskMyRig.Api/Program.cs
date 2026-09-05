using AskMyRig.Core;

var builder = WebApplication.CreateBuilder(args);

var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "nomic-embed-text";
var dimensions = builder.Configuration.GetValue("Ollama:Dimensions", 768);

var connectionString = builder.Configuration["Sql:ConnectionString"]
    ?? throw new InvalidOperationException("Sql:ConnectionString is not configured.");

// Singleton because it owns an HttpClient. One instance for the app's lifetime
// is what you want - a new HttpClient per request exhausts sockets under load.
builder.Services.AddSingleton<IEmbeddingProvider>(
    _ => new OllamaEmbeddingProvider(ollamaUrl, ollamaModel, dimensions));

builder.Services.AddSingleton(sp =>
    new ChunkSearcher(connectionString, sp.GetRequiredService<IEmbeddingProvider>()));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Fail at startup rather than on the first query. If Ollama is not running,
// you want to know now, not when a search returns a 500.
await app.Services.GetRequiredService<IEmbeddingProvider>().EnsureReadyAsync();

app.MapGet("/", () => "Ok").WithName("");

app.MapGet("/health", (IEmbeddingProvider provider) => Results.Ok(new
{
    status = "ok",
    model = provider.ModelName,
    dimensions = provider.Dimensions
}));

app.MapPost("/search", async (
    SearchRequest request,
    ChunkSearcher searcher,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    try
    {
        return Results.Ok(await searcher.SearchAsync(request, cancellationToken));
    }
    catch (OllamaException ex)
    {
        // 503 rather than 500: the API is fine, its dependency is not.
        return Results.Problem(
            title: "Embedding model unavailable",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
