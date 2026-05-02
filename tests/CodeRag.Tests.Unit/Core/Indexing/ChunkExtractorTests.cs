using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeRag.Tests.Unit.Core.Indexing;

[TestFixture]
public class ChunkExtractorTests
{
    private IChunkExtractor _extractor = null!;

    [SetUp]
    public void SetUp()
    {
        _extractor = new ChunkExtractor(new SourceTextHashingService());
    }

    [Test]
    public void Should_emit_one_chunk_for_a_top_level_class()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo { }
        ");

        var classChunk = chunks.Should().ContainSingle(c => c.SymbolKind == SymbolKinds.Class).Subject;
        classChunk.SymbolDisplayName.Should().Be("Foo");
        classChunk.FullyQualifiedSymbolName.Should().Be("Acme.Foo");
        classChunk.ContainingNamespace.Should().Be("Acme");
        classChunk.Accessibility.Should().Be(Accessibilities.Public);
    }

    [Test]
    public void Should_capture_class_modifiers()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public abstract class Foo { }
            public sealed class Bar { }
        ");

        var foo = chunks.Single(c => c.SymbolDisplayName == "Foo");
        foo.Modifiers.IsAbstract.Should().BeTrue();
        var bar = chunks.Single(c => c.SymbolDisplayName == "Bar");
        bar.Modifiers.IsSealed.Should().BeTrue();
    }

    [Test]
    public void Should_distinguish_record_class_from_record_struct()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public record Person(string Name);
            public record struct Point(int X, int Y);
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.RecordClass && c.SymbolDisplayName == "Person");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.RecordStruct && c.SymbolDisplayName == "Point");
    }

    [Test]
    public void Should_capture_struct_interface_enum_delegate()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public struct Vec { }
            public interface IFoo { }
            public enum Kind { A, B }
            public delegate int Adder(int a, int b);
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Struct && c.SymbolDisplayName == "Vec");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Interface && c.SymbolDisplayName == "IFoo");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Enum && c.SymbolDisplayName == "Kind");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Delegate && c.SymbolDisplayName == "Adder");
    }

    [Test]
    public void Should_capture_base_type_and_implemented_interfaces()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public interface IFoo { }
            public class Base { }
            public class Derived : Base, IFoo { }
        ");

        var derived = chunks.Single(c => c.SymbolDisplayName == "Derived");
        derived.BaseTypeFullyQualifiedName.Should().Be("Acme.Base");
        derived.ImplementedInterfaceFullyQualifiedNames.Should().Contain("Acme.IFoo");
    }

    [Test]
    public void Should_emit_method_chunks_with_signature_and_return_type()
    {
        var chunks = ExtractFrom(@"
            using System.Threading.Tasks;
            namespace Acme;
            public class Foo
            {
                public async Task<int> RunAsync(string arg) => 0;
            }
        ");

        var method = chunks.Single(c => c.SymbolKind == SymbolKinds.Method);
        method.SymbolDisplayName.Should().Be("RunAsync");
        method.ReturnTypeFullyQualifiedName.Should().StartWith("System.Threading.Tasks.Task");
        method.Modifiers.IsAsync.Should().BeTrue();
        method.Parameters.Should().HaveCount(1);
        method.Parameters[0].Name.Should().Be("arg");
        method.Parameters[0].TypeFullyQualifiedName.Should().Be("string");
    }

    [Test]
    public void Should_capture_parameter_modifiers()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo
            {
                public void Mix(ref int a, out int b, in int c, params int[] d) { b = 0; }
            }
        ");

        var method = chunks.Single(c => c.SymbolKind == SymbolKinds.Method);
        method.Parameters[0].Modifier.Should().Be("ref");
        method.Parameters[1].Modifier.Should().Be("out");
        method.Parameters[2].Modifier.Should().Be("in");
        method.Parameters[3].Modifier.Should().Be("params");
    }

    [Test]
    public void Should_emit_constructor_chunk()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo { public Foo(int x) { } }
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Constructor);
    }

    [Test]
    public void Should_emit_operator_and_conversion_operator_chunks()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo
            {
                public static Foo operator +(Foo a, Foo b) => a;
                public static implicit operator int(Foo f) => 0;
            }
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Operator);
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.ConversionOperator);
    }

    [Test]
    public void Should_emit_indexer_chunk()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo { public int this[int i] => 0; }
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Indexer);
    }

    [Test]
    public void Should_emit_property_chunk_with_type()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo { public int Count { get; init; } }
        ");

        var prop = chunks.Single(c => c.SymbolKind == SymbolKinds.Property);
        prop.ReturnTypeFullyQualifiedName.Should().Be("int");
        prop.SymbolDisplayName.Should().Be("Count");
    }

    [Test]
    public void Should_emit_field_chunk_with_type_and_readonly_modifier()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo { private readonly int _count = 0; }
        ");

        var field = chunks.Single(c => c.SymbolKind == SymbolKinds.Field);
        field.ReturnTypeFullyQualifiedName.Should().Be("int");
        field.Modifiers.IsReadonly.Should().BeTrue();
    }

    [Test]
    public void Should_emit_event_chunk_with_delegate_type()
    {
        var chunks = ExtractFrom(@"
            using System;
            namespace Acme;
            public class Foo { public event EventHandler? OnSomething; }
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Event && c.SymbolDisplayName == "OnSomething");
    }

    [Test]
    public void Should_capture_attribute_with_string_argument()
    {
        var chunks = ExtractFrom(@"
            using System;
            namespace Acme;
            public class Foo
            {
                [Obsolete(""use bar instead"")]
                public void Run() { }
            }
        ");

        var method = chunks.Single(c => c.SymbolKind == SymbolKinds.Method);
        var attr = method.Attributes.Should().ContainSingle().Subject;
        attr.AttributeFullyQualifiedName.Should().Be("System.ObsoleteAttribute");
        attr.AttributeArgumentsJson.Should().Contain("use bar instead");
    }

    [Test]
    public void Should_emit_one_chunk_per_partial_class_declaration()
    {
        var src1 = @"
            namespace Acme;
            public partial class Foo { public void A() { } }
        ";
        var src2 = @"
            namespace Acme;
            public partial class Foo { public void B() { } }
        ";

        var tree1 = CSharpSyntaxTree.ParseText(src1, path: "C:/repo/Foo.A.cs");
        var tree2 = CSharpSyntaxTree.ParseText(src2, path: "C:/repo/Foo.B.cs");
        var compilation = CSharpCompilation.Create(
            "Test",
            new[] { tree1, tree2 },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        var chunks1 = _extractor.Extract(compilation, tree1, "T", "T", "C:/repo", CancellationToken.None);
        var chunks2 = _extractor.Extract(compilation, tree2, "T", "T", "C:/repo", CancellationToken.None);

        chunks1.Single(c => c.SymbolKind == SymbolKinds.Class).Modifiers.IsPartial.Should().BeTrue();
        chunks1.Where(c => c.SymbolKind == SymbolKinds.Method).Should().HaveCount(1);
        chunks2.Where(c => c.SymbolKind == SymbolKinds.Method).Should().HaveCount(1);
    }

    private ImmutableArray<CodeChunk> ExtractFrom(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "C:/repo/src/Test.cs");
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        return _extractor.Extract(compilation, tree, "TestProject", "TestAssembly", "C:/repo", CancellationToken.None);
    }
}
