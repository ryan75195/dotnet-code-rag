using System.Collections.Immutable;

namespace CodeRag.Analyzers;

internal static class AnalyzerConstants
{
    internal static readonly ImmutableHashSet<string> ExcludedMethodNames =
        ImmutableHashSet.Create(
            "Dispose", "DisposeAsync", "ToString", "Equals",
            "GetHashCode", "GetType", "Finalize",
            "Start", "StartAsync", "StopAsync", "ExecuteAsync");
}
