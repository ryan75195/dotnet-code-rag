using System.Collections.Immutable;
using CodeRag.Core.Indexing;

namespace CodeRag.Tests.Unit.Core.Indexing;

internal static class TestChunks
{
    public static CodeChunk SampleMethod() => new(
        ContainingProjectName: "CodeRag.Core",
        ContainingAssemblyName: "CodeRag.Core",
        RelativeFilePath: "src/CodeRag.Core/Foo.cs",
        StartLineNumber: 5,
        EndLineNumber: 12,
        SymbolKind: SymbolKinds.Method,
        SymbolDisplayName: "RunAsync",
        SymbolSignatureDisplay: "Task<int> RunAsync(System.Threading.CancellationToken ct)",
        FullyQualifiedSymbolName: "CodeRag.Core.Foo.RunAsync(System.Threading.CancellationToken)",
        ContainingNamespace: "CodeRag.Core",
        ParentSymbolFullyQualifiedName: "CodeRag.Core.Foo",
        Accessibility: Accessibilities.Public,
        Modifiers: new ChunkModifiers(false, false, false, false, false, true, false, false, false, false, false, false),
        BaseTypeFullyQualifiedName: null,
        ReturnTypeFullyQualifiedName: "System.Threading.Tasks.Task<int>",
        ParameterCount: 1,
        DocumentationCommentXml: null,
        SourceText: "public async Task<int> RunAsync(CancellationToken ct) => 0;",
        SourceTextHash: "deadbeef",
        Attributes: ImmutableArray.Create(new ChunkAttribute("System.ObsoleteAttribute", "[\"deprecated\"]")),
        ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
        Parameters: ImmutableArray.Create(new ChunkParameter(0, "ct", "System.Threading.CancellationToken", null, false)),
        GenericTypeParameters: ImmutableArray<ChunkGenericTypeParameter>.Empty);
}
