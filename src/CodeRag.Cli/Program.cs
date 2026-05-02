using System.CommandLine;
using System.CommandLine.Invocation;
using CodeRag.Cli.Commands;
using CodeRag.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCoreServices();
builder.Services.AddTransient<IndexCommand>();
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

var rootCommand = new RootCommand("CodeRag — code RAG indexer");
rootCommand.AddCommand(indexCmd);

return await rootCommand.InvokeAsync(args);
