using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<ISourceTextHashingService, SourceTextHashingService>();
        services.AddSingleton<IChunkExtractor, ChunkExtractor>();
        services.AddSingleton<IReconciliationService, ReconciliationService>();
        services.AddTransient<IWorkspaceLoadingService, MsBuildWorkspaceLoadingService>();
        services.AddSingleton<IGitDiffService, CliGitDiffService>();
        services.AddSingleton<IEmbeddingClient>(_ =>
            new OpenAIEmbeddingClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "sk-not-configured"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Func<string, IIndexStore>>(_ => path => new SqliteIndexStore(path));
        services.AddTransient<IndexingDependencies>();
        services.AddTransient<IIndexingService, IndexingService>();
        services.AddTransient<IQueryService, QueryService>();
        return services;
    }
}
