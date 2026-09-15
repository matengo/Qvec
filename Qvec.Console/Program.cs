using Qvec.Core;
using System.CommandLine;

// Shared options. The dimension and capacity must match between `init` and every
// later command: the on-disk format is not self-describing, so opening a file with
// a different `max` or `dim` silently misreads it. Previously `init` used max:10000
// while `search` fell back to the default max:1000, which corrupted every lookup.
// Exposing them as explicit shared options makes the mismatch visible.
var pathOption = new Option<string>("--path") { Description = "Sökväg till databasfilen", Required = true };
var dimOption = new Option<int>("--dim") { Description = "Vektordimension", DefaultValueFactory = _ => 1536 };
var maxOption = new Option<int>("--max") { Description = "Maximalt antal vektorer", DefaultValueFactory = _ => 10000 };

var rootCommand = new RootCommand("Qvec CLI - Högpresterande Vektordatabas");

// Kommando: Initiera ny DB
var initCommand = new Command("init", "Skapa en ny databasfil");
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
        Console.WriteLine($"Databas skapad: {path} (dim={dim}, max={max})");
        Console.WriteLine($"Använd samma --dim {dim} --max {max} för efterföljande kommandon.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Kunde inte skapa databasen: {ex.Message}");
        return 1;
    }
});

// Kommando: Sök
var queryOption = new Option<string>("--vector") { Description = "Frågevektor (kommaseparerad)", Required = true };
var topKOption = new Option<int>("--top-k") { Description = "Antal träffar att returnera", DefaultValueFactory = _ => 3 };

var searchCommand = new Command("search", "Sök i databasen");
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
        Console.Error.WriteLine($"Databasfilen finns inte: {path}");
        return 1;
    }

    if (topK <= 0)
    {
        Console.Error.WriteLine("--top-k måste vara större än 0.");
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
        Console.Error.WriteLine($"Ogiltig vektor: {ex.Message}");
        return 1;
    }

    if (query.Length == 0)
    {
        Console.Error.WriteLine("--vector innehöll inga tal.");
        return 1;
    }

    try
    {
        using var db = new QvecDatabase(path, dim, max);
        var results = db.SearchSimpleParallel(query, topK);

        if (results.Count == 0)
        {
            Console.WriteLine("Inga träffar.");
            return 0;
        }

        foreach (var r in results)
            Console.WriteLine($"ID: {r.Id}, Score: {r.Score:F4}, Meta: {r.Metadata}");

        return 0;
    }
    catch (QvecDimensionException ex)
    {
        Console.Error.WriteLine(
            $"Vektorn har {ex.Actual} dimensioner men databasen förväntar sig {ex.Expected}.");
        return 1;
    }
    catch (QvecException ex)
    {
        Console.Error.WriteLine($"Databasfel: {ex.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(initCommand);
rootCommand.Subcommands.Add(searchCommand);

return rootCommand.Parse(args).Invoke();
