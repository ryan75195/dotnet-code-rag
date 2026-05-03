namespace CodeRag.Mcp;

internal static class IndexPathResolver
{
    public const string EnvVarName = "CODERAG_INDEX_PATH";

    public static string Resolve(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            return Path.GetFullPath(args[0]);
        }
        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }
        var localDefault = Path.Combine(Directory.GetCurrentDirectory(), ".coderag", "index.db");
        return Path.GetFullPath(localDefault);
    }
}
