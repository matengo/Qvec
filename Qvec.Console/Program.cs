using QvecSharp;
using System;
using System.CommandLine;

var rootCommand = new RootCommand("QvecSharp CLI - Högpresterande Vektordatabas");

// Kommando: Initiera ny DB
var initCommand = new Command("init", "Skapa en ny databasfil");
var pathOption = new Option<string>("--path", "Sökväg till filen");
initCommand.Options.Add(pathOption);
initCommand.SetAction(parseResult =>
{
    var path = parseResult.GetValue(pathOption);
    using var db = new VectorDatabase(path, dim: 1536, max: 10000);
    Console.WriteLine($"Databas skapad: {path}");
});

// Kommando: Sök
var searchCommand = new Command("search", "Sök i databasen");
var queryOption = new Option<string>("--vector", "Frågevektor (kommaseparerad)");
searchCommand.Options.Add(pathOption);
searchCommand.Options.Add(queryOption);
searchCommand.SetAction(parseResult =>
{
    var path = parseResult.GetValue(pathOption);
    var vectorStr = parseResult.GetValue(queryOption);
    float[] query = vectorStr.Split(',').Select(float.Parse).ToArray();
    using var db = new VectorDatabase(path);
    var results = db.SearchParallel(query, topK: 3);

    foreach (var r in results)
        Console.WriteLine($"ID: {r.Id}, Score: {r.Score:F4}, Meta: {r.Metadata}");
});

rootCommand.Subcommands.Add(initCommand);
rootCommand.Subcommands.Add(searchCommand);

return rootCommand.Parse(args).Invoke();