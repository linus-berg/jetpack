namespace Jetpack.Api;

public static class ConfigurationExtensions {
  private const string C_API_KEY_VARIABLE_NAME_ = "JETPACK_API_KEY";
  private const string C_API_KEY_CONFIGURATION_NAME_ = "ApiKey";

  public static string? GetApiKey(this IConfiguration configuration) {
    return Environment.GetEnvironmentVariable(C_API_KEY_VARIABLE_NAME_) ??
           configuration[C_API_KEY_CONFIGURATION_NAME_];
  }
}