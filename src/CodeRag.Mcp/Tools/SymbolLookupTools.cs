using System.ComponentModel;
using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;
using ModelContextProtocol.Server;

namespace CodeRag.Mcp.Tools;

[McpServerToolType]
public static class SymbolLookupTools
{
    [McpServerTool(Name = "find_symbol", ReadOnly = true, Idempotent = true)]
    [Description("Look up a symbol by short name or fully-qualified name. Pure SQL lookup (no embeddings). Returns matching chunks ordered shortest-FQN first. Use this when you know the name and want exact navigation.")]
    public static Task<IReadOnlyList<SymbolHit>> FindSymbol(
        ICodeIndexLookupService lookup,
        McpServerConfig config,
        [Description("Short name (e.g. 'UserService') or fully-qualified name")] string nameOrFullyQualifiedName,
        [Description("Optional kind filter: method, class, property, field, ...")] string? kind = null,
        [Description("Max number of hits to return")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return lookup.FindSymbol(config.IndexDatabasePath, nameOrFullyQualifiedName, kind, limit, cancellationToken);
    }

    [McpServerTool(Name = "list_implementations", ReadOnly = true, Idempotent = true)]
    [Description("List all chunks that directly implement the given interface (by fully-qualified name). Pure SQL lookup. Note: matches direct interfaces only, not transitive — a class extending a base that implements I will not match.")]
    public static Task<IReadOnlyList<SymbolHit>> ListImplementations(
        ICodeIndexLookupService lookup,
        McpServerConfig config,
        [Description("Fully-qualified interface name (e.g. 'MyApp.Services.IUserService')")] string interfaceFullyQualifiedName,
        [Description("Max number of hits to return")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return lookup.ListImplementations(config.IndexDatabasePath, interfaceFullyQualifiedName, limit, cancellationToken);
    }

    [McpServerTool(Name = "list_attributed_with", ReadOnly = true, Idempotent = true)]
    [Description("List all chunks decorated with the given attribute (by fully-qualified name). Pure SQL lookup. Useful for finding all [Obsolete], [TestFixture], [HttpGet], etc. usages.")]
    public static Task<IReadOnlyList<SymbolHit>> ListAttributedWith(
        ICodeIndexLookupService lookup,
        McpServerConfig config,
        [Description("Fully-qualified attribute name (e.g. 'System.ObsoleteAttribute')")] string attributeFullyQualifiedName,
        [Description("Max number of hits to return")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return lookup.ListAttributedWith(config.IndexDatabasePath, attributeFullyQualifiedName, limit, cancellationToken);
    }
}
