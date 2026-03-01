using Jetpack.Api.Services;

namespace Jetpack.Api;

public class Program {
  public static async Task Main(string[] args) {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    builder.WebHost.UseUrls("http://*:8080");
    builder.Services.AddControllers();
    // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOpenApi();

    builder.Services.AddSingleton<IStorageService, MinioStorageService>();
    builder.Services.AddSingleton<PluginMetadataService>();

    WebApplication app = builder.Build();

    // Initialize Metadata Service
    using (IServiceScope scope = app.Services.CreateScope()) {
      PluginMetadataService metadata_service = scope.ServiceProvider
                                                    .GetRequiredService<
                                                      PluginMetadataService>();
      await metadata_service.InitializeAsync();
    }

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment()) {
      app.MapOpenApi();
    }
    app.UseAuthorization();
    app.MapControllers();
    await app.RunAsync();
  }
}