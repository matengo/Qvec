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
    // H�r skickar du in dina inst�llningar (t.ex. fr�n appsettings.json)
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

app.MapPost("/entry", (QvecDatabase db, AddEntryRequest request) =>
{
    if (request.Vector == null || request.Vector.Length == 0)
        return Results.BadRequest("Vektor saknas");
    
    if (string.IsNullOrEmpty(request.Metadata))
        return Results.BadRequest("Metadata saknas");

    try
    {
        db.AddEntry(request.Vector, request.Metadata);
        return Results.Ok(new { message = "Entry added successfully" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to add entry: {ex.Message}", statusCode: 500);
    }
});

app.MapPut("/entry/{id}", (QvecDatabase db, int id, UpdateEntryRequest request) =>
{
    if (request.Vector == null || request.Vector.Length == 0)
        return Results.BadRequest("Vektor saknas");
    
    if (string.IsNullOrEmpty(request.Metadata))
        return Results.BadRequest("Metadata saknas");

    try
    {
        db.UpdateEntry(id, request.Vector, request.Metadata);
        return Results.Ok(new { message = "Entry updated successfully" });
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.NotFound(new { message = $"Entry with id {id} not found" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to update entry: {ex.Message}", statusCode: 500);
    }
});

app.MapDelete("/entry/{id}", (QvecDatabase db, int id) =>
{
    try
    {
        db.DeleteEntry(id);
        return Results.Ok(new { message = "Entry deleted successfully" });
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.NotFound(new { message = $"Entry with id {id} not found" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to delete entry: {ex.Message}", statusCode: 500);
    }
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
        entryPointIndex = db.GetEntryPoint(), // L�gg till en enkel getter f�r _header.EntryPoint
        layerDistribution = stats.Select(kv => new { Layer = kv.Key, Count = kv.Value }),
        fileSizeMb = new FileInfo("vectors.qvec").Length / 1024 / 1024
    });
});
app.Run();



public record SearchRequest(float[] Vector, int TopK = 5);
public record AddEntryRequest(float[] Vector, string Metadata);
public record UpdateEntryRequest(float[] Vector, string Metadata);
public record SearchResponse { public int Id { get; init; } public float Score { get; init; } public required string Metadata { get; init; } }

[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(AddEntryRequest))]
[JsonSerializable(typeof(UpdateEntryRequest))]
[JsonSerializable(typeof(List<SearchResponse>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
