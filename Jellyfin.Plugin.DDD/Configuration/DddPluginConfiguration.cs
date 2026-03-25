using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DDD.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class DddPluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DddPluginConfiguration"/> class.
    /// </summary>
    public DddPluginConfiguration()
    {
        // set default options here
        DddApiKey = "API-Key";
        DddApiUrl = "https://www.doesthedogdie.com";
    }

    /// <summary>
    /// Gets or Sets the API Key used to access the DDD API. The DoesTheDogDie API Key is obtained from the DDD Profile page.
    /// </summary>
    public string DddApiKey { get; set; }

    public string DddApiUrl { get; set; }
}
