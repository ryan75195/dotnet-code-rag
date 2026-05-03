using System.Collections.Immutable;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CodeRag.Core.Indexing.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeRag.Core.Indexing;

public sealed class ChunkExtractor : IChunkExtractor
{
    private static readonly SymbolDisplayFormat FullyQualifiedNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut);

    private readonly ISourceTextHashingService _hashingService;

    public ChunkExtractor(ISourceTextHashingService hashingService)
    {
        _hashingService = hashingService;
    }

    public ImmutableArray<CodeChunk> Extract(
        Compilation compilation,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        CancellationToken cancellationToken)
    {
        if (!IsInRepository(syntaxTree.FilePath, repositoryRootPath))
        {
            return ImmutableArray<CodeChunk>.Empty;
        }
        if (IsObviouslyGenerated(syntaxTree.FilePath))
        {
            return ImmutableArray<CodeChunk>.Empty;
        }
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot(cancellationToken);
        var builder = ImmutableArray.CreateBuilder<CodeChunk>();
        ExtractTypeDeclarations(root, semanticModel, syntaxTree, projectName, assemblyName, repositoryRootPath, builder, cancellationToken);
        ExtractDelegateDeclarations(root, semanticModel, syntaxTree, projectName, assemblyName, repositoryRootPath, builder, cancellationToken);
        ExtractMemberDeclarations(root, semanticModel, syntaxTree, projectName, assemblyName, repositoryRootPath, builder, cancellationToken);
        return builder.ToImmutable();
    }

    private static bool IsInRepository(string? filePath, string repositoryRootPath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }
        var fullFile = Path.GetFullPath(filePath);
        var fullRoot = Path.GetFullPath(repositoryRootPath);
        return fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObviouslyGenerated(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }
        var normalised = filePath.Replace('\\', '/');
        return normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalised.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalised.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    }

    private void ExtractMemberDeclarations(
        SyntaxNode root,
        SemanticModel semanticModel,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        ImmutableArray<CodeChunk>.Builder builder,
        CancellationToken cancellationToken)
    {
        foreach (var memberDecl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ShouldSkipMemberDeclaration(memberDecl))
            {
                continue;
            }
            if (memberDecl is BaseFieldDeclarationSyntax fieldDecl)
            {
                ExtractFieldVariables(fieldDecl, semanticModel, syntaxTree, projectName, assemblyName, repositoryRootPath, builder, cancellationToken);
                continue;
            }
            var symbol = semanticModel.GetDeclaredSymbol(memberDecl, cancellationToken);
            if (symbol is null || symbol.IsImplicitlyDeclared)
            {
                continue;
            }
            var chunk = TryBuildMemberChunk(symbol, memberDecl, syntaxTree, projectName, assemblyName, repositoryRootPath);
            if (chunk is not null)
            {
                builder.Add(chunk);
            }
        }
    }

    private void ExtractFieldVariables(
        BaseFieldDeclarationSyntax fieldDecl,
        SemanticModel semanticModel,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        ImmutableArray<CodeChunk>.Builder builder,
        CancellationToken cancellationToken)
    {
        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
            if (symbol is null || symbol.IsImplicitlyDeclared)
            {
                continue;
            }
            var chunk = TryBuildMemberChunk(symbol, fieldDecl, syntaxTree, projectName, assemblyName, repositoryRootPath);
            if (chunk is not null)
            {
                builder.Add(chunk);
            }
        }
    }

    private static bool ShouldSkipMemberDeclaration(MemberDeclarationSyntax memberDecl)
    {
        return memberDecl is BaseTypeDeclarationSyntax
            or DelegateDeclarationSyntax
            or NamespaceDeclarationSyntax
            or FileScopedNamespaceDeclarationSyntax
            or EnumMemberDeclarationSyntax;
    }

    private void ExtractTypeDeclarations(
        SyntaxNode root,
        SemanticModel semanticModel,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        ImmutableArray<CodeChunk>.Builder builder,
        CancellationToken cancellationToken)
    {
        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol symbol)
            {
                continue;
            }
            builder.Add(BuildTypeChunk(symbol, typeDecl, syntaxTree, projectName, assemblyName, repositoryRootPath));
        }
    }

    private void ExtractDelegateDeclarations(
        SyntaxNode root,
        SemanticModel semanticModel,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        ImmutableArray<CodeChunk>.Builder builder,
        CancellationToken cancellationToken)
    {
        foreach (var delegateDecl in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (semanticModel.GetDeclaredSymbol(delegateDecl, cancellationToken) is not INamedTypeSymbol symbol)
            {
                continue;
            }
            builder.Add(BuildTypeChunk(symbol, delegateDecl, syntaxTree, projectName, assemblyName, repositoryRootPath));
        }
    }

    private CodeChunk BuildTypeChunk(
        INamedTypeSymbol symbol,
        SyntaxNode declaration,
        SyntaxTree tree,
        string projectName,
        string assemblyName,
        string repositoryRootPath)
    {
        var location = BuildLocation(declaration, tree, repositoryRootPath);
        var sourceText = BuildTypeSourceText(declaration);
        var modifiers = BuildModifiers(symbol) with { IsPartial = ResolveIsPartialDeclaration(declaration) };
        return new CodeChunk(
            ContainingProjectName: projectName,
            ContainingAssemblyName: assemblyName,
            RelativeFilePath: location.RelativeFilePath,
            StartLineNumber: location.StartLineNumber,
            EndLineNumber: location.EndLineNumber,
            SymbolKind: ResolveTypeKind(symbol),
            SymbolDisplayName: symbol.Name,
            SymbolSignatureDisplay: symbol.ToDisplayString(),
            FullyQualifiedSymbolName: ToFullyQualifiedName(symbol),
            ContainingNamespace: ResolveContainingNamespace(symbol),
            ParentSymbolFullyQualifiedName: symbol.ContainingType is null ? null : ToFullyQualifiedName(symbol.ContainingType),
            Accessibility: ResolveAccessibility(symbol.DeclaredAccessibility),
            Modifiers: modifiers,
            BaseTypeFullyQualifiedName: ResolveBaseType(symbol),
            ReturnTypeFullyQualifiedName: null,
            ParameterCount: null,
            DocumentationCommentXml: NullIfEmpty(symbol.GetDocumentationCommentXml()),
            XmlDocSummary: ResolveXmlDocSummary(symbol),
            SourceText: sourceText,
            SourceTextHash: _hashingService.Hash(sourceText),
            Attributes: BuildAttributes(symbol),
            ImplementedInterfaceFullyQualifiedNames: symbol.AllInterfaces.Select(ToFullyQualifiedName).ToImmutableArray(),
            Parameters: ImmutableArray<ChunkParameter>.Empty,
            GenericTypeParameters: BuildGenerics(symbol));
    }

    private static ChunkLocation BuildLocation(SyntaxNode declaration, SyntaxTree tree, string repositoryRootPath)
    {
        var span = declaration.GetLocation().GetLineSpan();
        return new ChunkLocation(
            ToRelativePath(tree.FilePath, repositoryRootPath),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1);
    }

    private static string ToFullyQualifiedName(ISymbol symbol)
    {
        var name = symbol.ToDisplayString(FullyQualifiedNameFormat);
        const string globalPrefix = "global::";
        return name.StartsWith(globalPrefix, StringComparison.Ordinal) ? name[globalPrefix.Length..] : name;
    }

    private static string? ResolveContainingNamespace(INamedTypeSymbol symbol)
    {
        if (symbol.ContainingNamespace is null || symbol.ContainingNamespace.IsGlobalNamespace)
        {
            return null;
        }
        return symbol.ContainingNamespace.ToDisplayString();
    }

    private static string? ResolveBaseType(INamedTypeSymbol symbol)
    {
        if (symbol.BaseType is null || symbol.BaseType.SpecialType == SpecialType.System_Object)
        {
            return null;
        }
        return ToFullyQualifiedName(symbol.BaseType);
    }

    private static string ResolveTypeKind(INamedTypeSymbol symbol)
    {
        if (symbol.IsRecord && symbol.TypeKind == TypeKind.Class)
        {
            return SymbolKinds.RecordClass;
        }
        if (symbol.IsRecord && symbol.TypeKind == TypeKind.Struct)
        {
            return SymbolKinds.RecordStruct;
        }
        return symbol.TypeKind switch
        {
            TypeKind.Class => SymbolKinds.Class,
            TypeKind.Struct => SymbolKinds.Struct,
            TypeKind.Interface => SymbolKinds.Interface,
            TypeKind.Enum => SymbolKinds.Enum,
            TypeKind.Delegate => SymbolKinds.Delegate,
            _ => SymbolKinds.Class,
        };
    }

    private static string ResolveAccessibility(Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => Accessibilities.Public,
        Microsoft.CodeAnalysis.Accessibility.Internal => Accessibilities.Internal,
        Microsoft.CodeAnalysis.Accessibility.Protected => Accessibilities.Protected,
        Microsoft.CodeAnalysis.Accessibility.Private => Accessibilities.Private,
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Accessibilities.ProtectedInternal,
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Accessibilities.PrivateProtected,
        _ => Accessibilities.Internal,
    };

    private static ChunkModifiers BuildModifiers(ISymbol symbol)
    {
        var isAsync = symbol is IMethodSymbol method && method.IsAsync;
        var isExtensionMethod = symbol is IMethodSymbol extensionMethod && extensionMethod.IsExtensionMethod;
        var isReadonly = symbol is IFieldSymbol field && field.IsReadOnly;
        var isGeneric = ResolveIsGeneric(symbol);
        return new ChunkModifiers(
            IsStatic: symbol.IsStatic,
            IsAbstract: symbol.IsAbstract,
            IsSealed: symbol.IsSealed,
            IsVirtual: symbol.IsVirtual,
            IsOverride: symbol.IsOverride,
            IsAsync: isAsync,
            IsPartial: false,
            IsReadonly: isReadonly,
            IsExtern: symbol.IsExtern,
            IsUnsafe: false,
            IsExtensionMethod: isExtensionMethod,
            IsGeneric: isGeneric);
    }

    private static bool ResolveIsGeneric(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol type => type.IsGenericType,
            IMethodSymbol method => method.IsGenericMethod,
            _ => false,
        };
    }

    private static bool ResolveIsPartialDeclaration(SyntaxNode declaration)
    {
        return declaration is TypeDeclarationSyntax typeDeclaration
            && typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    private static bool ResolveIsPartialMember(MemberDeclarationSyntax declaration)
    {
        return declaration is MethodDeclarationSyntax method
            && method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
    }

    private static ImmutableArray<ChunkAttribute> BuildAttributes(ISymbol symbol)
    {
        var builder = ImmutableArray.CreateBuilder<ChunkAttribute>();
        foreach (var data in symbol.GetAttributes())
        {
            var name = data.AttributeClass is null ? "<unknown>" : ToFullyQualifiedName(data.AttributeClass);
            string? args = null;
            if (data.ConstructorArguments.Length > 0 || data.NamedArguments.Length > 0)
            {
                var positional = data.ConstructorArguments.Select(a => JsonSerializer.Serialize(CoerceTypedConstant(a))).ToArray();
                args = "[" + string.Join(",", positional) + "]";
            }
            builder.Add(new ChunkAttribute(name, args));
        }
        return builder.ToImmutable();
    }

    private static object? CoerceTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull)
        {
            return null;
        }
        if (constant.Kind == TypedConstantKind.Array)
        {
            return constant.Values.Select(CoerceTypedConstant).ToArray();
        }
        return constant.Value;
    }

    private static ImmutableArray<ChunkGenericTypeParameter> BuildGenerics(INamedTypeSymbol symbol)
    {
        var builder = ImmutableArray.CreateBuilder<ChunkGenericTypeParameter>();
        for (int i = 0; i < symbol.TypeParameters.Length; i++)
        {
            var parameter = symbol.TypeParameters[i];
            builder.Add(new ChunkGenericTypeParameter(i, parameter.Name, BuildGenericConstraints(parameter)));
        }
        return builder.ToImmutable();
    }

    private static string? BuildGenericConstraints(ITypeParameterSymbol parameter)
    {
        if (!parameter.HasReferenceTypeConstraint
            && !parameter.HasValueTypeConstraint
            && !parameter.HasConstructorConstraint
            && parameter.ConstraintTypes.Length == 0)
        {
            return null;
        }
        var list = new List<string>();
        if (parameter.HasReferenceTypeConstraint)
        {
            list.Add("class");
        }
        if (parameter.HasValueTypeConstraint)
        {
            list.Add("struct");
        }
        if (parameter.HasConstructorConstraint)
        {
            list.Add("new()");
        }
        foreach (var constraintType in parameter.ConstraintTypes)
        {
            list.Add(ToFullyQualifiedName(constraintType));
        }
        return "[" + string.Join(",", list.Select(s => $"\"{s}\"")) + "]";
    }

    private static string BuildTypeSourceText(SyntaxNode declaration)
    {
        if (declaration is TypeDeclarationSyntax type)
        {
            var header = type.WithMembers(default).NormalizeWhitespace().ToFullString();
            var memberSignatures = string.Join(Environment.NewLine, type.Members.Select(SimplifyMemberSignature));
            return header + Environment.NewLine + memberSignatures;
        }
        return declaration.ToFullString();
    }

    private static string SimplifyMemberSignature(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax method => method
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace()
                .ToFullString(),
            PropertyDeclarationSyntax prop => prop.NormalizeWhitespace().ToFullString(),
            ConstructorDeclarationSyntax ctor => ctor
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                .NormalizeWhitespace()
                .ToFullString(),
            _ => member.NormalizeWhitespace().ToFullString(),
        };
    }

    private static string ToRelativePath(string absolutePath, string repositoryRoot)
    {
        var relative = Path.GetRelativePath(repositoryRoot, absolutePath);
        return relative.Replace('\\', '/');
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private CodeChunk? TryBuildMemberChunk(
        ISymbol symbol,
        MemberDeclarationSyntax declaration,
        SyntaxTree tree,
        string projectName,
        string assemblyName,
        string repositoryRootPath)
    {
        var kind = ResolveMemberKind(symbol, declaration);
        if (kind is null)
        {
            return null;
        }
        var location = BuildLocation(declaration, tree, repositoryRootPath);
        var sourceText = declaration.NormalizeWhitespace().ToFullString();
        var details = ResolveMemberDetails(symbol);
        var modifiers = BuildModifiers(symbol) with { IsPartial = ResolveIsPartialMember(declaration) };
        return new CodeChunk(
            ContainingProjectName: projectName,
            ContainingAssemblyName: assemblyName,
            RelativeFilePath: location.RelativeFilePath,
            StartLineNumber: location.StartLineNumber,
            EndLineNumber: location.EndLineNumber,
            SymbolKind: kind,
            SymbolDisplayName: symbol.Name,
            SymbolSignatureDisplay: symbol.ToDisplayString(),
            FullyQualifiedSymbolName: ToFullyQualifiedName(symbol),
            ContainingNamespace: ResolveMemberContainingNamespace(symbol),
            ParentSymbolFullyQualifiedName: symbol.ContainingType is null ? null : ToFullyQualifiedName(symbol.ContainingType),
            Accessibility: ResolveAccessibility(symbol.DeclaredAccessibility),
            Modifiers: modifiers,
            BaseTypeFullyQualifiedName: null,
            ReturnTypeFullyQualifiedName: details.ReturnType,
            ParameterCount: details.ParameterCount,
            DocumentationCommentXml: NullIfEmpty(symbol.GetDocumentationCommentXml()),
            XmlDocSummary: ResolveXmlDocSummary(symbol),
            SourceText: sourceText,
            SourceTextHash: _hashingService.Hash(sourceText),
            Attributes: BuildAttributes(symbol),
            ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
            Parameters: details.Parameters,
            GenericTypeParameters: BuildMemberGenerics(symbol));
    }

    private static MemberDetails ResolveMemberDetails(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => new MemberDetails(
                method.ReturnsVoid ? null : ToFullyQualifiedTypeName(method.ReturnType),
                BuildParameters(method.Parameters),
                method.Parameters.Length),
            IPropertySymbol property when property.IsIndexer => new MemberDetails(
                ToFullyQualifiedTypeName(property.Type),
                BuildParameters(property.Parameters),
                property.Parameters.Length),
            IPropertySymbol property => new MemberDetails(
                ToFullyQualifiedTypeName(property.Type),
                ImmutableArray<ChunkParameter>.Empty,
                null),
            IFieldSymbol field => new MemberDetails(
                ToFullyQualifiedTypeName(field.Type),
                ImmutableArray<ChunkParameter>.Empty,
                null),
            IEventSymbol evt => new MemberDetails(
                ToFullyQualifiedTypeName(evt.Type),
                ImmutableArray<ChunkParameter>.Empty,
                null),
            _ => new MemberDetails(null, ImmutableArray<ChunkParameter>.Empty, null),
        };
    }

    private static string? ResolveMemberKind(ISymbol symbol, MemberDeclarationSyntax declaration) => (symbol, declaration) switch
    {
        (IMethodSymbol, ConversionOperatorDeclarationSyntax) => SymbolKinds.ConversionOperator,
        (IMethodSymbol, OperatorDeclarationSyntax) => SymbolKinds.Operator,
        (IMethodSymbol, ConstructorDeclarationSyntax) => SymbolKinds.Constructor,
        (IMethodSymbol, DestructorDeclarationSyntax) => SymbolKinds.Destructor,
        (IMethodSymbol, _) => SymbolKinds.Method,
        (IPropertySymbol, IndexerDeclarationSyntax) => SymbolKinds.Indexer,
        (IPropertySymbol, _) => SymbolKinds.Property,
        (IFieldSymbol, _) => SymbolKinds.Field,
        (IEventSymbol, _) => SymbolKinds.Event,
        _ => null,
    };

    private static ImmutableArray<ChunkParameter> BuildParameters(ImmutableArray<IParameterSymbol> parameters)
    {
        var builder = ImmutableArray.CreateBuilder<ChunkParameter>();
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            builder.Add(new ChunkParameter(
                Ordinal: i,
                Name: parameter.Name,
                TypeFullyQualifiedName: ToFullyQualifiedTypeName(parameter.Type),
                Modifier: ResolveParameterModifier(parameter),
                HasDefaultValue: parameter.HasExplicitDefaultValue));
        }
        return builder.ToImmutable();
    }

    private static string? ResolveParameterModifier(IParameterSymbol parameter)
    {
        if (parameter.IsParams)
        {
            return "params";
        }
        return parameter.RefKind switch
        {
            RefKind.Ref => "ref",
            RefKind.Out => "out",
            RefKind.In => "in",
            _ => null,
        };
    }

    private static ImmutableArray<ChunkGenericTypeParameter> BuildMemberGenerics(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method)
        {
            return ImmutableArray<ChunkGenericTypeParameter>.Empty;
        }
        var builder = ImmutableArray.CreateBuilder<ChunkGenericTypeParameter>();
        for (int i = 0; i < method.TypeParameters.Length; i++)
        {
            builder.Add(new ChunkGenericTypeParameter(i, method.TypeParameters[i].Name, null));
        }
        return builder.ToImmutable();
    }

    private static string ToFullyQualifiedTypeName(ITypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        const string globalPrefix = "global::";
        return name.StartsWith(globalPrefix, StringComparison.Ordinal) ? name[globalPrefix.Length..] : name;
    }

    private static string? ResolveMemberContainingNamespace(ISymbol symbol)
    {
        if (symbol.ContainingNamespace is null || symbol.ContainingNamespace.IsGlobalNamespace)
        {
            return null;
        }
        return symbol.ContainingNamespace.ToDisplayString();
    }

    private static string? ResolveXmlDocSummary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }
        try
        {
            var document = XDocument.Parse(xml);
            var summary = document.Descendants("summary").FirstOrDefault()?.Value?.Trim();
            return string.IsNullOrEmpty(summary) ? null : summary;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private sealed record ChunkLocation(string RelativeFilePath, int StartLineNumber, int EndLineNumber);

    private sealed record MemberDetails(string? ReturnType, ImmutableArray<ChunkParameter> Parameters, int? ParameterCount);
}
