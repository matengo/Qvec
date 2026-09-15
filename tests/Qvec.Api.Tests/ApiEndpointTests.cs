using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Qvec.Api.Tests;

public sealed class ApiEndpointTests
{
    [Fact]
    public async Task PostVectorThenSearchReturnsCreatedVector()
    {
        using var database = new TempApiDb();
        await using var factory = new QvecApiFactory(database);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-key");

        using var create = await client.PostAsJsonAsync("/vectors", new { vector = new[] { 1f, 0f, 0f }, metadata = "alpha" });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(create.Headers.Location);
        using var createJson = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync());
        var id = createJson.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);

        using var search = await client.PostAsJsonAsync("/search", new { vector = new[] { 1f, 0f, 0f }, topK = 1 });

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = await JsonDocument.ParseAsync(await search.Content.ReadAsStreamAsync());
        var result = Assert.Single(searchJson.RootElement.EnumerateArray());
        Assert.Equal(id, result.GetProperty("id").GetGuid());
        Assert.Equal("alpha", result.GetProperty("metadata").GetString());
    }

    [Fact]
    public async Task DimensionMismatchReturnsProblemDetailsBadRequest()
    {
        using var database = new TempApiDb();
        await using var factory = new QvecApiFactory(database);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-key");

        using var response = await client.PostAsJsonAsync("/vectors", new { vector = new[] { 1f, 0f }, metadata = "bad" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(400, problem?.Status);
        Assert.Contains("dimension", problem?.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedRequestIsRejected()
    {
        using var database = new TempApiDb();
        await using var factory = new QvecApiFactory(database);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/search", new { vector = new[] { 1f, 0f, 0f }, topK = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RateLimitReturnsTooManyRequests()
    {
        using var database = new TempApiDb();
        await using var factory = new QvecApiFactory(database, overrides: new Dictionary<string, string?>
        {
            ["Qvec:RateLimit:PermitLimit"] = "1",
            ["Qvec:RateLimit:WindowSeconds"] = "60"
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "test-key");

        using var first = await client.PostAsJsonAsync("/search", new { vector = new[] { 1f, 0f, 0f }, topK = 1 });
        using var second = await client.PostAsJsonAsync("/search", new { vector = new[] { 1f, 0f, 0f }, topK = 1 });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HealthEndpointReportsHealthy()
    {
        using var database = new TempApiDb();
        await using var factory = new QvecApiFactory(database);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public void StartupFailsOnInvalidConfiguration()
    {
        using var database = new TempApiDb();
        using var factory = new QvecApiFactory(database, overrides: new Dictionary<string, string?>
        {
            ["Qvec:Dimension"] = "0"
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Qvec", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}