using System.Net.Http.Headers;
using Jetpack.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Jetpack.Tests;

/// <summary>Runs Jetpack in-process against a temporary storage directory.</summary>
public sealed class JetpackFactory : WebApplicationFactory<Program> {
  public const string C_API_KEY = "test-api-key";

  public JetpackFactory(string? storage_path = null) {
    StoragePath = storage_path ??
                  Path.Combine(
                    Path.GetTempPath(),
                    "jetpack-tests",
                    Guid.NewGuid().ToString("N")
                  );
  }

  public string StoragePath { get; }

  /// <summary>Set to false to reuse the storage directory in another instance.</summary>
  public bool DeleteStorageOnDispose { get; set; } = true;

  protected override void ConfigureWebHost(IWebHostBuilder builder) {
    builder.UseEnvironment("Testing");
    builder.UseSetting("Storage:Provider", "FileSystem");
    builder.UseSetting("Storage:Path", StoragePath);
    builder.UseSetting("Jetpack:ApiKey", C_API_KEY);
    builder.UseSetting("Jetpack:RefreshIntervalSeconds", "0");
    builder.UseSetting("Jetpack:LogRequests", "false");
  }

  /// <summary>A client that does not follow redirects, like a careful IDE test.</summary>
  public HttpClient CreateRawClient() {
    return CreateClient(new WebApplicationFactoryClientOptions {
      AllowAutoRedirect = false
    });
  }

  public async Task<HttpResponseMessage> UploadAsync(byte[] archive,
                                                     string file_name,
                                                     string query = "",
                                                     string? api_key = C_API_KEY) {
    using HttpClient client = CreateRawClient();
    using ByteArrayContent content = new(archive);
    content.Headers.ContentType = new MediaTypeHeaderValue(
      "application/octet-stream"
    );
    using HttpRequestMessage request = new(
      HttpMethod.Post,
      $"/api/plugins/upload?filename={Uri.EscapeDataString(file_name)}{query}"
    ) { Content = content };
    if (api_key != null) {
      request.Headers.Add("X-Api-Key", api_key);
    }

    return await client.SendAsync(request);
  }

  public async Task UploadOkAsync(byte[] archive, string file_name,
                                  string query = "") {
    using HttpResponseMessage response = await UploadAsync(
                                           archive,
                                           file_name,
                                           query
                                         );
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
  }

  public override async ValueTask DisposeAsync() {
    await base.DisposeAsync();
    if (!DeleteStorageOnDispose) {
      return;
    }

    try {
      Directory.Delete(StoragePath, true);
    } catch (IOException) {
      // Best effort.
    } catch (UnauthorizedAccessException) {
      // Best effort.
    }
  }
}
