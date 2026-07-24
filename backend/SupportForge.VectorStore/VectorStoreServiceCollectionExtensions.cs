using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.VectorStore.Chroma;

namespace SupportForge.VectorStore;

public static class VectorStoreServiceCollectionExtensions
{
    public static IServiceCollection AddVectorStore(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["VectorStore:Provider"] ?? "Chroma";

        if (provider == "Chroma")
        {
            services.Configure<ChromaOptions>(config.GetSection("VectorStore:Chroma"));
            services.AddHttpClient<IVectorStoreService, ChromaVectorStoreService>();
        }
        else
        {
            throw new NotSupportedException($"Vector store provider '{provider}' is not registered yet (Pinecone lands Day 7).");
        }

        return services;
    }
}
