using System.CommandLine; // Kräver NuGet: System.CommandLine
using QvecSharp;

var rootCommand = new RootCommand("ZvecSharp CLI - Högpresterande Vektordatabas");

// Kommando: Initiera ny DB
var initCommand = new Command("init", "Skapa en ny databasfil");
var pathOption = new Option<string>("--path", "Sökväg till filen");
initCommand.AddOption(pathOption);
initCommand.SetHandler((path) => {
    using var db = new VectorDatabase(path, dim: 1536, max: 10000);
    Console.WriteLine($"Databas skapad: {path}");
}, pathOption);

// Kommando: Sök
var searchCommand = new Command("search", "Sök i databasen");
var queryOption = new Option<string>("--vector", "Frågevektor (kommaseparerad)");
searchCommand.AddOption(pathOption);
searchCommand.AddOption(queryOption);
searchCommand.SetHandler((path, vectorStr) => {
    float[] query = vectorStr.Split(',').Select(float.Parse).ToArray();
    using var db = new VectorDatabase(path);
    var results = db.SearchParallel(query, topK: 3);

    foreach (var r in results)
        Console.WriteLine($"ID: {r.Id}, Score: {r.Score:F4}, Meta: {r.Metadata}");
}, pathOption, queryOption);

rootCommand.AddCommand(initCommand);
rootCommand.AddCommand(searchCommand);

return await rootCommand.InvokeAsync(args);