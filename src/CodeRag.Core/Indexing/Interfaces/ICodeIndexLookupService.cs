namespace CodeRag.Core.Indexing.Interfaces;

public interface ICodeIndexLookupService
{
    Task<IReadOnlyList<SymbolHit>> FindSymbol(
        string indexDatabasePath,
        string nameOrFullyQualifiedName,
        string? symbolKind,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolHit>> ListImplementations(
        string indexDatabasePath,
        string interfaceFullyQualifiedName,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SymbolHit>> ListAttributedWith(
        string indexDatabasePath,
        string attributeFullyQualifiedName,
        int limit,
        CancellationToken cancellationToken);
}
