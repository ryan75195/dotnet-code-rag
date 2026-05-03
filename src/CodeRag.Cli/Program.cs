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
var formatOpt = new Option<string>("--format", () => "text", "Output format: text or json.");

var queryCmd = new Command("query", "Search the code index for chunks similar to a natural-language query.");
queryCmd.AddArgument(textArg);
queryCmd.AddOption(dbOpt);
queryCmd.AddOption(topKOpt);
queryCmd.AddOption(kindOpt);
queryCmd.AddOption(projectOpt);
queryCmd.AddOption(nsOpt);
queryCmd.AddOption(asyncOpt);
queryCmd.AddOption(formatOpt);
queryCmd.SetHandler(async (InvocationContext ctx) =>
{
    var text = ctx.ParseResult.GetValueForArgument(textArg);
    var db = ctx.ParseResult.GetValueForOption(dbOpt);
    var topK = ctx.ParseResult.GetValueForOption(topKOpt);
    var kind = ctx.ParseResult.GetValueForOption(kindOpt);
    var project = ctx.ParseResult.GetValueForOption(projectOpt);
    var ns = ctx.ParseResult.GetValueForOption(nsOpt);
    var isAsync = ctx.ParseResult.GetValueForOption(asyncOpt);
    var format = ctx.ParseResult.GetValueForOption(formatOpt) ?? "text";
    var command = host.Services.GetRequiredService<QueryCommand>();
    ctx.ExitCode = await command.ExecuteAsync(text, db?.FullName, topK, kind, project, ns, isAsync, format, ctx.GetCancellationToken());
});

var rootCommand = new RootCommand("CodeRag — code RAG indexer");
rootCommand.AddCommand(indexCmd);
rootCommand.AddCommand(queryCmd);

return await rootCommand.InvokeAsync(args);
