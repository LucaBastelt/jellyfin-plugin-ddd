using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Jellyfin.Plugin.DDD.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.DDD;

/// <summary>
/// The main plugin.
/// </summary>
public class DoesTheDogDieJellyfinIntegrationPlugin : BasePlugin<DddPluginConfiguration>, IHasWebPages
{
    private readonly ILogger<DoesTheDogDieJellyfinIntegrationPlugin> _logger;

    private int _retryCount = 0;
    private const int MaxRetries = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="DoesTheDogDieJellyfinIntegrationPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="libraryManager">LibManager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="httpClientFactory">HttpClientFactory.</param>
    public DoesTheDogDieJellyfinIntegrationPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<DoesTheDogDieJellyfinIntegrationPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        this._logger = logger;

        RegisterScript();
    }

    /// <inheritdoc />
    public override string Name => "DoesTheDogDie Jellyfin Integration";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c84eb73f-949b-45c4-aa0a-294b94030aed");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static DoesTheDogDieJellyfinIntegrationPlugin? Instance { get; private set; }

    /// <summary>
    /// Returns pages.
    /// </summary>
    /// <returns>Pages.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo { Name = this.Name, EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", this.GetType().Namespace) }
        ];
    }

    public void RegisterScript()
    {
        try
        {
            // Find the JavaScript Injector assembly
            Assembly? jsInjectorAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains("Jellyfin.Plugin.JavaScriptInjector") ?? false);

            if (jsInjectorAssembly != null)
            {
                // Get the PluginInterface type
                Type? pluginInterfaceType = jsInjectorAssembly.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");

                var resourceName = "Jellyfin.Plugin.DDD.Resources.ddd-description.js";
                string scriptContent;
                using (var stream = GetType().Assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream is null)
                    {
                        _logger.LogWarning("[DDD] Embedded resource '{Resource}' not found.", resourceName);
                        return;
                    }

                    using var reader = new StreamReader(stream);
                    scriptContent = reader.ReadToEnd();
                }

                if (pluginInterfaceType != null)
                {
                    // Create the registration payload
                    var scriptRegistration = new JObject
                    {
                        { "id", $"{Id}-ddd" }, // Unique ID for your script
                        { "name", "DoesTheDogDie" },
                        { "script", scriptContent },
                        { "enabled", true },
                        { "requiresAuthentication", true }, // Set to true if script should only run for logged-in users
                        { "pluginId", Id.ToString() },
                        { "pluginName", Name },
                        { "pluginVersion", Version.ToString() },
                    };

                    // Register the script
                    var registerResult = pluginInterfaceType.GetMethod("RegisterScript")?.Invoke(null, new object[] { scriptRegistration });

                    if (registerResult is bool success && success)
                    {
                        _logger.LogInformation("Successfully registered JavaScript with JavaScript Injector plugin.");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to register JavaScript with JavaScript Injector plugin. RegisterScript returned false.");
                    }
                }
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            // JS Injector's singleton is not ready yet — retry after all plugins have initialised.
            if (_retryCount >= MaxRetries)
            {
                _logger.LogWarning("[JellyFlare] JS Injector not ready after {MaxRetries} retries — banner script will not be injected.", MaxRetries);
                return;
            }

            _retryCount++;
            _logger.LogInformation("[JellyFlare] JS Injector not ready yet, retrying in 5 s (attempt {Attempt}/{MaxRetries})...", _retryCount, MaxRetries);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                RegisterScript();
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to register JavaScript with JavaScript Injector plugin.");
        }
    }

    public void UnregisterYourScripts()
    {
        try
        {
            // Find the JavaScript Injector assembly
            Assembly? jsInjectorAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains("Jellyfin.Plugin.JavaScriptInjector") ?? false);

            if (jsInjectorAssembly != null)
            {
                Type? pluginInterfaceType = jsInjectorAssembly.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");

                if (pluginInterfaceType != null)
                {
                    var unregisterResult = pluginInterfaceType.GetMethod("UnregisterAllScriptsFromPlugin")?.Invoke(null, new object[] { Id.ToString() });

                    // or if you want to unregister a specific script
                    //pluginInterfaceType.GetMethod("UnregisterScript")?.Invoke(null, new object[] { $"{Id}-my-script" }); // -> returns bool, so adjust the result handling accordingly

                    if (unregisterResult is int removedCount)
                    {
                        _logger?.LogInformation("Successfully unregistered {Count} script(s) from JavaScript Injector plugin.", removedCount);
                    }
                    else
                    {
                        _logger?.LogWarning("Failed to unregister scripts from JavaScript Injector plugin. Method returned unexpected value.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to unregister JavaScript scripts.");
        }
    }
}
