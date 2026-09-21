using AskMyRig.Core;
using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "nomic-embed-text";
var dimensions = builder.Configuration.GetValue("Ollama:Dimensions", 768);

// Separate from the embedding model: one turns text into vectors, the other
// writes the answer. They are different models and can be swapped independently.
var chatModel = builder.Configuration["Ollama:ChatModel"] ?? "llama3.2";

var connectionString = builder.Configuration["Sql:ConnectionString"]
    ?? throw new InvalidOperationException("Sql:ConnectionString is not configured.");

// Singleton because it owns an HttpClient. One instance for the app's lifetime
// is what you want - a new HttpClient per request exhausts sockets under load.
builder.Services.AddSingleton<IEmbeddingProvider>(
    _ => new OllamaEmbeddingProvider(ollamaUrl, ollamaModel, dimensions));

// Registered as IRetriever, not as the concrete class. Project 2 swaps in a
// keyword retriever and a fused one; only this line should have to change.
builder.Services.AddSingleton<IRetriever>(sp =>
    new VectorRetriever(connectionString, sp.GetRequiredService<IEmbeddingProvider>()));

// OllamaApiClient implements IChatClient, so RigAnswerer never learns which
// provider it got. Pointing this at Azure OpenAI or Anthropic is one line.
builder.Services.AddSingleton<IChatClient>(
    _ => new OllamaApiClient(new Uri(ollamaUrl), chatModel));

builder.Services.AddSingleton(sp => new RigAnswerer(
    sp.GetRequiredService<IRetriever>(),
    sp.GetRequiredService<IChatClient>()));

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
    IRetriever retriever,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    try
    {
        var result = await retriever.RetrieveAsync(request, cancellationToken);

        return Results.Ok(new SearchResponse(
            request.Question,
            result.Hits.Count,
            result.Ms("embed"),
            result.Ms("search"),
            result.Hits));
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

app.MapPost("/ask", async (
    AskRequest request,
    RigAnswerer answerer,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    try
    {
        return Results.Ok(await answerer.AskAsync(request, cancellationToken));
    }
    catch (OllamaException ex)
    {
        return Results.Problem(
            title: "Embedding model unavailable",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
