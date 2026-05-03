using System.CommandLine;
using System.CommandLine.Invocation;
using CodeRag.Cli.Commands;
using CodeRag.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCoreServices();
builder.Services.AddTransient<IndexCommand>();
builder.Services.AddTransient<QueryCommand>();
var host = builder.Build();

var slnArg = new Argument<FileInfo>("solution", "Path to the .sln or .slnx file.");
var outOption = new Option<DirectoryInfo?>("--out", "Output directory; defaults to <sln-dir>/.coderag.");

var indexCmd = new Command("index", "Build or refresh a code index for the given solution.");
indexCmd.AddArgument(slnArg);
indexCmd.AddOption(outOption);
indexCmd.SetHandler(async (InvocationContext ctx) =>
{
    var sln = ctx.ParseResult.GetValueForArgument(slnArg);
    var outDir = ctx.ParseResult.GetValueForOption(outOption);
    var command = host.Services.GetRequiredService<IndexCommand>();
    ctx.ExitCode = await command.ExecuteAsync(sln.FullName, outDir?.FullName, ctx.GetCancellationToken());
});

var textArg = new Argument<string>("text", "Natural-language query.");
var dbOpt = new Option<FileInfo?>("--db", "Path to index.db; defaults to ./.coderag/index.db");
var topKOpt = new Option<int>("--top-k", () => 10, "Number of hits to return.");
var kindOpt = new Option<string?>("--kind", "Filter by symbol_kind (e.g. method, class, property).");
var projectOpt = new Option<string?>("--project", "Filter by containing project name.");
var nsOpt = new Option<string?>("--namespace", "Filter by containing namespace.");
var asyncOpt = new Option<bool?>("--is-async", "Filter to async methods only.");
var accessibilityOpt = new Option<string?>("--accessibility", "Filter by accessibility (public, internal, protected, ...).");
var hasAttrOpt = new Option<string?>("--has-attribute", "Filter chunks that carry the given attribute (fully qualified).");
var implementsOpt = new Option<string?>("--implements", "Filter chunks implementing the given interface (fully qualified).");
var returnTypeContainsOpt = new Option<string?>("--return-type-contains", "Filter chunks whose return type contains the given substring.");
var excludeTestsOpt = new Option<bool>("--exclude-tests", () => false, "Exclude chunks in test namespaces.");
var excludeNsOpt = new Option<string?>("--exclude-namespace", "Exclude chunks whose namespace contains the given substring.");
var maxDistanceOpt = new Option<double?>("--max-distance", "Drop hits with KNN distance above this value.");
var formatOpt = new Option<string>("--format", () => "text", "Output format: text or json.");

var queryCmd = new Command("query", "Search the code index for chunks similar to a natural-language query.");
queryCmd.AddArgument(textArg);
queryCmd.AddOption(dbOpt);
queryCmd.AddOption(topKOpt);
queryCmd.AddOption(kindOpt);
queryCmd.AddOption(projectOpt);
queryCmd.AddOption(nsOpt);
queryCmd.AddOption(asyncOpt);
queryCmd.AddOption(accessibilityOpt);
queryCmd.AddOption(hasAttrOpt);
queryCmd.AddOption(implementsOpt);
queryCmd.AddOption(returnTypeContainsOpt);
queryCmd.AddOption(excludeTestsOpt);
queryCmd.AddOption(excludeNsOpt);
queryCmd.AddOption(maxDistanceOpt);
queryCmd.AddOption(formatOpt);
queryCmd.SetHandler(async (InvocationContext ctx) =>
{
    var options = new QueryCommandOptions(
        Text: ctx.ParseResult.GetValueForArgument(textArg),
        DbPath: ctx.ParseResult.GetValueForOption(dbOpt)?.FullName,
        TopK: ctx.ParseResult.GetValueForOption(topKOpt),
        SymbolKind: ctx.ParseResult.GetValueForOption(kindOpt),
        Project: ctx.ParseResult.GetValueForOption(projectOpt),
        ContainingNamespace: ctx.ParseResult.GetValueForOption(nsOpt),
        IsAsync: ctx.ParseResult.GetValueForOption(asyncOpt),
        Accessibility: ctx.ParseResult.GetValueForOption(accessibilityOpt),
        HasAttribute: ctx.ParseResult.GetValueForOption(hasAttrOpt),
        Implements: ctx.ParseResult.GetValueForOption(implementsOpt),
        ReturnTypeContains: ctx.ParseResult.GetValueForOption(returnTypeContainsOpt),
        ExcludeTests: ctx.ParseResult.GetValueForOption(excludeTestsOpt),
        ExcludeNamespace: ctx.ParseResult.GetValueForOption(excludeNsOpt),
        MaxDistance: ctx.ParseResult.GetValueForOption(maxDistanceOpt),
        Format: ctx.ParseResult.GetValueForOption(formatOpt) ?? "text");
    var command = host.Services.GetRequiredService<QueryCommand>();
    ctx.ExitCode = await command.ExecuteAsync(options, ctx.GetCancellationToken());
});

var rootCommand = new RootCommand("CodeRag — code RAG indexer");
rootCommand.AddCommand(indexCmd);
rootCommand.AddCommand(queryCmd);

return await rootCommand.InvokeAsync(args);
