using CodeRag.Core.Indexing;
using CodeRag.Core.Indexing.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<ISourceTextHashingService, SourceTextHashingService>();
        return services;
    }
}
