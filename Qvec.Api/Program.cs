using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Qvec.Core;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<QvecExceptionHandler>();

builder.Services.AddSingleton<IValidateOptions<QvecOptions>, QvecOptionsValidator>();
builder.Services.AddOptions<QvecOptions>()
    .BindConfiguration(QvecOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<QvecOptions>>().Value);

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<QvecOptions>();
    var directory = Path.GetDirectoryName(Path.GetFullPath(settings.Path));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    return new QvecDatabase(
        settings.Path,
        dim: settings.Dimension,
        max: settings.MaxCount,
        maxNeighbors: settings.M,
        maxLayers: settings.MaxLayers,
        distanceFunction: settings.DistanceFunction);
});
builder.Services.AddHostedService<QvecDatabaseStartupService>();

// API keys fit an embedded database wrapper: there are no per-user identities to model,
// and the symmetric shared-secret check stays small, dependency-light, and Native-AOT friendly.
builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.Scheme)
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.Scheme, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter();
builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<QvecOptions>((options, settings) =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = static async (context, cancellationToken) =>
        {
            await Results.Problem(
                title: "Too many requests",
                detail: "Rate limit exceeded.",
                statusCode: StatusCodes.Status429TooManyRequests)
                .ExecuteAsync(context.HttpContext);
        };

        options.AddPolicy("qvec", httpContext =>
        {
            if (!settings.RateLimit.Enabled)
            {
                return RateLimitPartition.GetNoLimiter("qvec");
            }

            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString()
                ?? httpContext.Request.Headers.Host.ToString()
                ?? "qvec";

            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.RateLimit.PermitLimit,
                Window = TimeSpan.FromSeconds(settings.RateLimit.WindowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
    });

builder.Services.AddHealthChecks()
    .AddCheck("self", static () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<QvecReadinessHealthCheck>("qvec", tags: ["ready"]);

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains("live")
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = static check => check.Tags.Contains("ready")
}).AllowAnonymous();

var api = app.MapGroup("")
    .RequireRateLimiting("qvec")
    .RequireAuthorization();

api.MapPost("/vectors", (QvecDatabase db, AddVectorRequest request) =>
{
    var error = ValidateVector(request.Vector, db.VectorDimension);
    if (error is not null)
    {
        return error;
    }

    var id = db.AddEntry(request.Vector!, request.Metadata ?? string.Empty, request.ExternalId);
    var response = new AddVectorResponse(id);

    return Results.Created($"/vectors/{id}", response);
});

api.MapGet("/vectors/{id:guid}", Results<Ok<VectorResponse>, NotFound> (QvecDatabase db, Guid id) =>
{
    var entry = db.GetByGuid(id);
    if (entry is null)
    {
        return TypedResults.NotFound();
    }

    return TypedResults.Ok(new VectorResponse(id, entry.Value.Vector, entry.Value.Metadata));
});

api.MapDelete("/vectors/{id:guid}", Results<Ok<DeleteVectorResponse>, NotFound> (QvecDatabase db, Guid id) =>
{
    bool deleted = db.Delete(id);
    return deleted
        ? TypedResults.Ok(new DeleteVectorResponse(true, id))
        : TypedResults.NotFound();
});

api.MapPut("/vectors/{id:guid}", Results<Ok<UpdateVectorResponse>, NotFound, ProblemHttpResult> (QvecDatabase db, Guid id, UpdateVectorRequest request) =>
{
    if (request.Vector is not null)
    {
        var error = ValidateVector(request.Vector, db.VectorDimension);
        if (error is not null)
        {
            return error;
        }
    }

    bool updated = db.Update(id, request.Vector, request.Metadata);
    return updated
        ? TypedResults.Ok(new UpdateVectorResponse(true, id))
        : TypedResults.NotFound();
});

api.MapPost("/search", Results<Ok<List<SearchResponse>>, ProblemHttpResult> (QvecDatabase db, QvecOptions settings, SearchRequest request) =>
{
    var error = ValidateVector(request.Vector, db.VectorDimension);
    if (error is not null)
    {
        return error;
    }

    error = ValidateTopK(request.TopK, settings.MaxTopK);
    if (error is not null)
    {
        return error;
    }

    var response = db.Search(request.Vector!, request.TopK)
        .Select(r => new SearchResponse(r.Id, r.Score, r.Metadata))
        .ToList();

    return TypedResults.Ok(response);
});

api.MapGet("/stats", (QvecDatabase db, QvecOptions settings) =>
{
    var stats = db.GetStats();
    var fileInfo = new FileInfo(settings.Path);
    var response = new StatsResponse(
        db.GetCount(),
        db.LiveCount,
        db.DeletedCount,
        db.MaxCount,
        db.VectorDimension,
        db.GetEntryPoint(),
        stats.Select(kv => new LayerStat(kv.Key, kv.Value)).ToList(),
        fileInfo.Exists ? fileInfo.Length / 1024d / 1024d : 0d);

    return TypedResults.Ok(response);
});

app.Run();

static ProblemHttpResult? ValidateVector(float[]? vector, int expectedDimension)
{
    if (vector is null || vector.Length == 0)
    {
        return BadRequestProblem("Invalid vector", "Vector is required.");
    }

    if (vector.Length != expectedDimension)
    {
        return BadRequestProblem(
            "Invalid vector dimension",
            $"Vector dimension mismatch: database expects {expectedDimension} dimensions but the supplied vector has {vector.Length}.");
    }

    return null;
}

static ProblemHttpResult? ValidateTopK(int topK, int maxTopK)
{
    if (topK <= 0)
    {
        return BadRequestProblem("Invalid topK", "topK must be greater than zero.");
    }

    if (topK > maxTopK)
    {
        return BadRequestProblem("Invalid topK", $"topK must be less than or equal to {maxTopK}.");
    }

    return null;
}

static ProblemHttpResult BadRequestProblem(string title, string detail) =>
    TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status400BadRequest);

public sealed partial class Program;

[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(AddVectorRequest))]
[JsonSerializable(typeof(UpdateVectorRequest))]
[JsonSerializable(typeof(VectorResponse))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(LayerStat))]
[JsonSerializable(typeof(List<LayerStat>))]
[JsonSerializable(typeof(AddVectorResponse))]
[JsonSerializable(typeof(DeleteVectorResponse))]
[JsonSerializable(typeof(UpdateVectorResponse))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(List<SearchResponse>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal partial class AppJsonSerializerContext : JsonSerializerContext;