using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wlrix.Common.Logging;
using ZLogger;

namespace Wlrix.Common;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Extension to add ZLogger outputs and configuration.
    /// </summary>
    /// <param name="services">DI service collection.</param>
    /// <param name="appSlug">The application slug, used for log file naming.</param>
    /// <returns><paramref name="services"/> with ZLogger configuration applied.</returns>
    public static IServiceCollection AddZLogger(this IServiceCollection services, string appSlug) =>
        services.AddLogging(x =>
        {
            x.ClearProviders();

            // TODO: This can be set from the configuration, we would like to give more granular control similar
            //  to how hosted apps and ASP.NET are configured with appsettings.json like this:
            //  {
            //      "Logging": {
            //          "LogLevel": {
            //              "Default": "Information",
            //              "Microsoft": "Warning"
            //          }
            //      }
            //  }
            x.SetMinimumLevel(LogLevel.Information);

            x.AddZLoggerConsole(o =>
            {
                o.ConfigureEnableAnsiEscapeCode = false;
                o.UseFormatter(() => new LogConsoleFormatter());
            });
            x.AddZLoggerFile((o, _) =>
            {
                o.FileShared = true;
                o.UseFormatter(() => new LogFileFormatter());
                return Path.Combine(ApplicationPaths.EnsureDirectory(ApplicationPaths.AppData), $"wlrix.{appSlug}.log");
            });
        });
}
