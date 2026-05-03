namespace CodeRag.Core.Indexing;

public sealed record QueryRequest(
    string IndexDatabasePath,
    string Text,
    int TopK,
    QueryFilters Filters);
