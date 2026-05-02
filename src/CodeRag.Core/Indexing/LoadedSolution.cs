using Microsoft.CodeAnalysis;

namespace CodeRag.Core.Indexing;

public sealed record LoadedSolution(Solution Solution, IReadOnlyList<Project> Projects);
