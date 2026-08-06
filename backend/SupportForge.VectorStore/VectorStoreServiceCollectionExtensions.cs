using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.VectorStore.Chroma;
using SupportForge.VectorStore.Pinecone;

namespace SupportForge.VectorStore;

public static class VectorStoreServiceCollectionExtensions
{
    public static IServiceCollection AddVectorStore(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["VectorStore:Provider"] ?? "Chroma";

        switch (provider)
        {
            case "Chroma":
                services.Configure<ChromaOptions>(config.GetSection("VectorStore:Chroma"));
                services.AddHttpClient<IVectorStoreService, ChromaVectorStoreService>();
                break;
            case "Pinecone":
                services.Configure<PineconeOptions>(config.GetSection("VectorStore:Pinecone"));
                services.AddHttpClient<IVectorStoreService, PineconeVectorStoreService>();
                break;
            default:
                throw new NotSupportedException($"Vector store provider '{provider}' is not registered. Supported: Chroma, Pinecone.");
        }

        return services;
    }
}
