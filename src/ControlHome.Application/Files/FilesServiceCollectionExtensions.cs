using Microsoft.Extensions.DependencyInjection;

namespace ControlHome.Application.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>Registers the file feature's application services (the <see cref="IImageProcessor"/> comes from Infrastructure).</summary>
    public static IServiceCollection AddFiles(this IServiceCollection services)
    {
        services.AddScoped<FileService>();
        return services;
    }
}
