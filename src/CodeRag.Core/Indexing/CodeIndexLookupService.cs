using CodeRag.Core.Indexing.Interfaces;

namespace CodeRag.Core.Indexing;

internal sealed class CodeIndexLookupService : ICodeIndexLookupService
{
    private readonly Func<string, IIndexStore> _storeFactory;

    public CodeIndexLookupService(Func<string, IIndexStore> storeFactory)
    {
        _storeFactory = storeFactory;
    }

    public async Task<IReadOnlyList<SymbolHit>> FindSymbol(
        string indexDatabasePath,
        string nameOrFullyQualifiedName,
        string? symbolKind,
        int limit,
        CancellationToken cancellationToken)
    {
        using var store = _storeFactory(indexDatabasePath);
        await store.OpenAsync(cancellationToken);
        return await store.FindSymbolByName(nameOrFullyQualifiedName, symbolKind, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<SymbolHit>> ListImplementations(
        string indexDatabasePath,
        string interfaceFullyQualifiedName,
        int limit,
        CancellationToken cancellationToken)
    {
        using var store = _storeFactory(indexDatabasePath);
        await store.OpenAsync(cancellationToken);
        return await store.ListImplementations(interfaceFullyQualifiedName, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<SymbolHit>> ListAttributedWith(
        string indexDatabasePath,
        string attributeFullyQualifiedName,
        int limit,
        CancellationToken cancellationToken)
    {
        using var store = _storeFactory(indexDatabasePath);
        await store.OpenAsync(cancellationToken);
        return await store.ListAttributedWith(attributeFullyQualifiedName, limit, cancellationToken);
    }
}
