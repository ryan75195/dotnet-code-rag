using System.Collections.Immutable;

namespace CodeRag.Core.Indexing;

public sealed record CodeChunk(
    string ContainingProjectName,
    string ContainingAssemblyName,
    string RelativeFilePath,
    int StartLineNumber,
    int EndLineNumber,
    string SymbolKind,
    string SymbolDisplayName,
    string SymbolSignatureDisplay,
    string FullyQualifiedSymbolName,
    string? ContainingNamespace,
    string? ParentSymbolFullyQualifiedName,
    string Accessibility,
    ChunkModifiers Modifiers,
    string? BaseTypeFullyQualifiedName,
    string? ReturnTypeFullyQualifiedName,
    int? ParameterCount,
    string? DocumentationCommentXml,
    string? XmlDocSummary,
    string SourceText,
    string SourceTextHash,
    ImmutableArray<ChunkAttribute> Attributes,
    ImmutableArray<string> ImplementedInterfaceFullyQualifiedNames,
    ImmutableArray<ChunkParameter> Parameters,
    ImmutableArray<ChunkGenericTypeParameter> GenericTypeParameters);
