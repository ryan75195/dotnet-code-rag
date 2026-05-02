using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CodeRag.Core.Indexing.Interfaces;

public interface IChunkExtractor
{
    ImmutableArray<CodeChunk> Extract(
        Compilation compilation,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        CancellationToken cancellationToken);
}
