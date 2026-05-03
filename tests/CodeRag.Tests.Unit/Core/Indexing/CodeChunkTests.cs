using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class CodeChunkTests
{
    [Test]
    public void Should_construct_with_all_required_fields()
    {
        var chunk = new CodeChunk(
            ContainingProjectName: "CodeRag.Core",
            ContainingAssemblyName: "CodeRag.Core",
            RelativeFilePath: "src/Foo.cs",
            StartLineNumber: 1,
            EndLineNumber: 10,
            SymbolKind: SymbolKinds.Class,
            SymbolDisplayName: "Foo",
            SymbolSignatureDisplay: "public class Foo",
            FullyQualifiedSymbolName: "CodeRag.Core.Foo",
            ContainingNamespace: "CodeRag.Core",
            ParentSymbolFullyQualifiedName: null,
            Accessibility: Accessibilities.Public,
            Modifiers: new ChunkModifiers(false, false, false, false, false, false, false, false, false, false, false, false),
            BaseTypeFullyQualifiedName: null,
            ReturnTypeFullyQualifiedName: null,
            ParameterCount: null,
            DocumentationCommentXml: null,
            XmlDocSummary: null,
            SourceText: "public class Foo { }",
            SourceTextHash: "hash",
            Attributes: ImmutableArray<ChunkAttribute>.Empty,
            ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
            Parameters: ImmutableArray<ChunkParameter>.Empty,
            GenericTypeParameters: ImmutableArray<ChunkGenericTypeParameter>.Empty);

        chunk.SymbolKind.Should().Be(SymbolKinds.Class);
        chunk.XmlDocSummary.Should().BeNull();
    }

    [Test]
    public void Should_carry_xml_doc_summary_when_provided()
    {
        var chunk = new CodeChunk(
            ContainingProjectName: "CodeRag.Core",
            ContainingAssemblyName: "CodeRag.Core",
            RelativeFilePath: "src/Foo.cs",
            StartLineNumber: 1,
            EndLineNumber: 10,
            SymbolKind: SymbolKinds.Method,
            SymbolDisplayName: "DoStuff",
            SymbolSignatureDisplay: "void DoStuff()",
            FullyQualifiedSymbolName: "Foo.DoStuff",
            ContainingNamespace: "Foo",
            ParentSymbolFullyQualifiedName: "Foo",
            Accessibility: Accessibilities.Public,
            Modifiers: new ChunkModifiers(false, false, false, false, false, false, false, false, false, false, false, false),
            BaseTypeFullyQualifiedName: null,
            ReturnTypeFullyQualifiedName: null,
            ParameterCount: 0,
            DocumentationCommentXml: "<doc><summary>does the stuff</summary></doc>",
            XmlDocSummary: "does the stuff",
            SourceText: "public void DoStuff() { }",
            SourceTextHash: "hash",
            Attributes: ImmutableArray<ChunkAttribute>.Empty,
            ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
            Parameters: ImmutableArray<ChunkParameter>.Empty,
            GenericTypeParameters: ImmutableArray<ChunkGenericTypeParameter>.Empty);

        chunk.XmlDocSummary.Should().Be("does the stuff");
    }
}
