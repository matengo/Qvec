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
        return Results.Json(new MessageResponse("Vektor saknas"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 400);

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
        return Results.Json(new MessageResponse("Vektor saknas"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 400);
    
    if (string.IsNullOrEmpty(request.Metadata))
        return Results.Json(new MessageResponse("Metadata saknas"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 400);

    try
    {
        db.AddEntry(request.Vector, request.Metadata);
        return Results.Json(new MessageResponse("Entry added successfully"), AppJsonSerializerContext.Default.MessageResponse);
    }
    catch (Exception ex)
    {
        return Results.Json(new MessageResponse($"Failed to add entry: {ex.Message}"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 500);
    }
});

app.MapPut("/entry/{id}", (QvecDatabase db, int id, UpdateEntryRequest request) =>
{
    if (request.Vector == null || request.Vector.Length == 0)
        return Results.Json(new MessageResponse("Vektor saknas"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 400);
    
    if (string.IsNullOrEmpty(request.Metadata))
        return Results.Json(new MessageResponse("Metadata saknas"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 400);

    try
    {
        db.UpdateEntry(id, request.Vector, request.Metadata);
        return Results.Json(new MessageResponse("Entry updated successfully"), AppJsonSerializerContext.Default.MessageResponse);
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.Json(new MessageResponse($"Entry with id {id} not found"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 404);
    }
    catch (Exception ex)
    {
        return Results.Json(new MessageResponse($"Failed to update entry: {ex.Message}"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 500);
    }
});

app.MapDelete("/entry/{id}", (QvecDatabase db, int id) =>
{
    try
    {
        db.DeleteEntry(id);
        return Results.Json(new MessageResponse("Entry deleted successfully"), AppJsonSerializerContext.Default.MessageResponse);
    }
    catch (ArgumentOutOfRangeException)
    {
        return Results.Json(new MessageResponse($"Entry with id {id} not found"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 404);
    }
    catch (Exception ex)
    {
        return Results.Json(new MessageResponse($"Failed to delete entry: {ex.Message}"), AppJsonSerializerContext.Default.MessageResponse, statusCode: 500);
    }
});

app.MapGet("/health", (QvecDatabase db) =>
{
    bool healthy = db.IsHealthy();

    if (healthy)
        return Results.Json(new HealthResponse("Healthy", db.GetCount()), AppJsonSerializerContext.Default.HealthResponse);

    return Results.Json(new HealthResponse("Unhealthy", 0), AppJsonSerializerContext.Default.HealthResponse, statusCode: 503);
});
app.MapGet("/stats", (QvecDatabase db) =>
{
    var stats = db.GetStats();
    return Results.Json(new StatsResponse(
        db.GetCount(),
        db.GetEntryPoint(),
        stats.Select(kv => new { Layer = kv.Key, Count = kv.Value }),
        new FileInfo("vectors.qvec").Length / 1024 / 1024
    ), AppJsonSerializerContext.Default.StatsResponse);
});
app.Run();



public record SearchRequest(float[] Vector, int TopK = 5);
public record AddEntryRequest(float[] Vector, string Metadata);
public record UpdateEntryRequest(float[] Vector, string Metadata);
public record SearchResponse { public int Id { get; init; } public float Score { get; init; } public required string Metadata { get; init; } }
public record MessageResponse(string Message);
public record HealthResponse(string Status, int Count);
public record StatsResponse(int TotalVectors, int EntryPointIndex, object LayerDistribution, long FileSizeMb);

[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(AddEntryRequest))]
[JsonSerializable(typeof(UpdateEntryRequest))]
[JsonSerializable(typeof(List<SearchResponse>))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StatsResponse))]
internal partial class AppJsonSerializerContext : JsonSerializerContext { }
