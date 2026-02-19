using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json.Serialization;
using QvecSharp;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});
builder.Services.AddSingleton<QvecDatabase>(sp =>
{
    // Här skickar du in dina inställningar (t.ex. från appsettings.json)
    return new QvecDatabase("vectors.qvec", dim: 1536, max: 100000);
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();


if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/search", (QvecDatabase db, SearchRequest request) =>
{
    if (request.Vector == null || request.Vector.Length == 0)
        return Results.BadRequest("Vektor saknas");

    var topResults = db.Search(request.Vector, request.TopK);

    var response = topResults.Select(r => new SearchResponse
    {
        Id = r.Id,
        Score = r.Score,
        Metadata = r.Metadata
    }).ToList();

    return Results.Json(response, AppJsonSerializerContext.Default.ListSearchResponse);
});
app.MapGet("/health", (QvecDatabase db) =>
{
    bool healthy = db.IsHealthy();

    if (healthy)
        return Results.Ok(new { status = "Healthy", count = db.GetCount() });

    return Results.Problem("Database corrupt or not loaded", statusCode: 503);
});
app.MapGet("/stats", (QvecDatabase db) =>
{
    var stats = db.GetStats();
    return Results.Ok(new
    {
        totalVectors = db.GetCount(),
        entryPointIndex = db.GetEntryPoint(), // Lägg till en enkel getter för _header.EntryPoint
        layerDistribution = stats.Select(kv => new { Layer = kv.Key, Count = kv.Value }),
        fileSizeMb = new FileInfo("vectors.qvec").Length / 1024 / 1024
    });
});
app.MapDelete("/vectors/{guid}", (QvecDatabase db, Guid guid) =>
{
    bool deleted = db.Delete(guid);
    return deleted ? Results.Ok(new { deleted = true, id = guid }) : Results.NotFound();
});
app.MapPut("/vectors/{guid}", (QvecDatabase db, Guid guid, UpdateRequest request) =>
{
    bool updated = db.Update(guid, request.Vector, request.Metadata);
    return updated ? Results.Ok(new { updated = true, id = guid }) : Results.NotFound();
});
app.Run();



public record SearchRequest(float[] Vector, int TopK = 5);
public record UpdateRequest(float[]? Vector, string? Metadata);
public record SearchResponse { public Guid Id { get; init; } public float Score { get; init; } public string Metadata { get; init; } }

[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(List<SearchResponse>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
