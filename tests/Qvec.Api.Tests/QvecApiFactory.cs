using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Qvec.Api.Tests;

internal sealed class TempApiDb : IDisposable
{
    private readonly string _directory;

    public string Path { get; }

    public TempApiDb(string fileName = "api-test.qvec")
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qvec-api-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, fileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}

internal sealed class QvecApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public QvecApiFactory(TempApiDb database, string apiKey = "test-key", IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Qvec:Path"] = database.Path,
            ["Qvec:Dimension"] = "3",
            ["Qvec:MaxCount"] = "16",
            ["Qvec:M"] = "4",
            ["Qvec:MaxLayers"] = "3",
            ["Qvec:DistanceFunction"] = "DotProduct",
            ["Qvec:MaxTopK"] = "5",
            ["Qvec:Authentication:Enabled"] = "true",
            ["Qvec:Authentication:ApiKey"] = apiKey,
            ["Qvec:RateLimit:Enabled"] = "true",
            ["Qvec:RateLimit:PermitLimit"] = "100",
            ["Qvec:RateLimit:WindowSeconds"] = "60"
        };

        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        _settings = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.Sources.Clear();
            configuration.AddInMemoryCollection(_settings);
        });
    }
}