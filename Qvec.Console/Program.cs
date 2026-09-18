using Qvec.Core;
using System.CommandLine;

// Shared options. The dimension and capacity must match between `init` and every
// later command: the on-disk format is not self-describing, so opening a file with
// a different `max` or `dim` silently misreads it. Previously `init` used max:10000
// while `search` fell back to the default max:1000, which corrupted every lookup.
// Exposing them as explicit shared options makes the mismatch visible.
var pathOption = new Option<string>("--path") { Description = "Path to the database file", Required = true };
var dimOption = new Option<int>("--dim") { Description = "Vector dimension", DefaultValueFactory = _ => 1536 };
var maxOption = new Option<int>("--max") { Description = "Maximum number of vectors", DefaultValueFactory = _ => 10000 };

var rootCommand = new RootCommand("Qvec CLI - High-performance vector database");

// Command: Initialize new DB
var initCommand = new Command("init", "Create a new database file");
initCommand.Options.Add(pathOption);
initCommand.Options.Add(dimOption);
initCommand.Options.Add(maxOption);
initCommand.SetAction(parseResult =>
{
    var path = parseResult.GetRequiredValue(pathOption);
    var dim = parseResult.GetValue(dimOption);
    var max = parseResult.GetValue(maxOption);

    try
    {
        using var db = new QvecDatabase(path, dim, max);
        Console.WriteLine($"Database created: {path} (dim={dim}, max={max})");
        Console.WriteLine($"Use the same --dim {dim} --max {max} for subsequent commands.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not create the database: {ex.Message}");
        return 1;
    }
});

// Command: Search
var queryOption = new Option<string>("--vector") { Description = "Query vector (comma-separated)", Required = true };
var topKOption = new Option<int>("--top-k") { Description = "Number of matches to return", DefaultValueFactory = _ => 3 };

var searchCommand = new Command("search", "Search the database");
searchCommand.Options.Add(pathOption);
searchCommand.Options.Add(dimOption);
searchCommand.Options.Add(maxOption);
searchCommand.Options.Add(queryOption);
searchCommand.Options.Add(topKOption);
searchCommand.SetAction(parseResult =>
{
    var path = parseResult.GetRequiredValue(pathOption);
    var vectorStr = parseResult.GetRequiredValue(queryOption);
    var dim = parseResult.GetValue(dimOption);
    var max = parseResult.GetValue(maxOption);
    var topK = parseResult.GetValue(topKOption);

    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Database file does not exist: {path}");
        return 1;
    }

    if (topK <= 0)
    {
        Console.Error.WriteLine("--top-k must be greater than 0.");
        return 1;
    }

    float[] query;
    try
    {
        query = vectorStr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine($"Invalid vector: {ex.Message}");
        return 1;
    }

    if (query.Length == 0)
    {
        Console.Error.WriteLine("--vector contained no numbers.");
        return 1;
    }

    try
    {
        using var db = new QvecDatabase(path, dim, max);
        var results = db.SearchSimpleParallel(query, topK);

        if (results.Count == 0)
        {
            Console.WriteLine("No matches.");
            return 0;
        }

        foreach (var r in results)
            Console.WriteLine($"ID: {r.Id}, Score: {r.Score:F4}, Meta: {r.Metadata}");

        return 0;
    }
    catch (QvecDimensionException ex)
    {
        Console.Error.WriteLine(
            $"The vector has {ex.Actual} dimensions but the database expects {ex.Expected}.");
        return 1;
    }
    catch (QvecException ex)
    {
        Console.Error.WriteLine($"Database error: {ex.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(initCommand);
rootCommand.Subcommands.Add(searchCommand);

return rootCommand.Parse(args).Invoke();
