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
        services.AddHttpClient();
        services.AddSingleton<IEmbeddingClient>(sp =>
        {
            var raw = Environment.GetEnvironmentVariable("EMBEDDING_PROVIDER")?.Trim();
            var provider = string.IsNullOrEmpty(raw) ? "voyage" : raw;
            return BuildEmbeddingClient(sp, provider);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<Func<string, IIndexStore>>(sp =>
        {
            var embedding = sp.GetRequiredService<IEmbeddingClient>();
            return path => new SqliteIndexStore(path, embedding.VectorDimensions);
        });
        services.AddTransient<IndexingDependencies>();
        services.AddTransient<IIndexingService, IndexingService>();
        services.AddTransient<IQueryService, QueryService>();
        return services;
    }

    private static IEmbeddingClient BuildEmbeddingClient(IServiceProvider sp, string provider)
    {
        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "sk-not-configured";
            return new OpenAIEmbeddingClient(openAiKey);
        }
        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var voyageKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY") ?? "voyage-not-configured";
        return new VoyageEmbeddingClient(http, voyageKey);
    }
}
