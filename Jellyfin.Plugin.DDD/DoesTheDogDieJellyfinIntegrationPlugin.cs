using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DDD.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Season = MediaBrowser.Controller.Entities.TV.Season;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.DDD;

public record DddItemStateCache
{
    public Dictionary<Guid, DddItemStateCacheEntry> Items { get; set; } = [];
}

public record DddItemStateCacheEntry
{
    /// <summary>
    /// If the item is newer, rescan after a while to get more accurate ddd info.
    /// </summary>
    public DateTime? RefreshAgainAt { get; set; }
}

/// <summary>
/// The main plugin.
/// </summary>
public class DoesTheDogDieJellyfinIntegrationPlugin : BasePlugin<DddPluginConfiguration>, IHasWebPages, IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DoesTheDogDieJellyfinIntegrationPlugin> _logger;

    private const string ItemStateJsonFileName = "itemStateCache.json";

    private static readonly JsonSerializerOptions _jsonSettings = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip, PropertyNameCaseInsensitive = true };

    private readonly DddItemStateCache itemState;
    private readonly string jsonItemStatePath;

    private static readonly MemoryCache Cache = new(new MemoryDistributedCacheOptions() { SizeLimit = null, });

    /// <inheritdoc />
    public override string Name => "DoesTheDogDie Jellyfin Integration";

    public string Key { get; } = "DDD_";
    public string Category { get; } = "Library";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c84eb73f-949b-45c4-aa0a-294b94030aed");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static DoesTheDogDieJellyfinIntegrationPlugin? Instance { get; private set; }

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
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<DoesTheDogDieJellyfinIntegrationPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        logger.LogInformation("[DDD] Using Cache Folder at {0}", DataFolderPath);
        jsonItemStatePath = Path.Combine(DataFolderPath, ItemStateJsonFileName);
        if (File.Exists(jsonItemStatePath))
        {
            itemState = JsonConvert.DeserializeObject<DddItemStateCache>(File.ReadAllText(jsonItemStatePath));
        }
        else
        {
            itemState = new();
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = (TimeSpan.FromDays(1) / Configuration.BatchesPerDay).Ticks, };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var itemsToIgnore = itemState!.Items
            .Where(i => i.Value.RefreshAgainAt == null || i.Value.RefreshAgainAt > DateTime.Now)
            .Select(i => i.Key)
            .ToArray();

        var items = _libraryManager.QueryItems(new InternalItemsQuery()
        {
            Limit = Configuration.BatchRequestAmount,
            HasImdbId = true,
            ExcludeItemIds = itemsToIgnore,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
            Recursive = true,
            DtoOptions = new DtoOptions(false) { EnableImages = false, Fields = [ItemFields.AirTime, ItemFields.ProviderIds, ItemFields.Overview], },
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
        });

        var index = 0;
        foreach (var item in items.Items)
        {
            try
            {
                var url = await GetDddUrlForItemId(item, cancellationToken).ConfigureAwait(false);
                var data = await GetDddData(item, cancellationToken).ConfigureAwait(false);
                if (data is not null)
                {
                    await UpdateItem(cancellationToken, item, url, data).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[DDD] Updating content warnings failed for item {}", item.Id);
            }

            UpdateItemState(item);

            index++;
            progress.Report((index / (double)items.Items.Count) * 100);
        }

        await File.WriteAllTextAsync(jsonItemStatePath, JsonConvert.SerializeObject(itemState, Formatting.Indented), cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    private void UpdateItemState(BaseItem item)
    {
        DateTime? rescanTime = null;
        // New items will be monitored for a set amount of days
        if (item.PremiereDate.HasValue && (DateTime.Now - item.PremiereDate.Value).TotalDays < Configuration.NewMediaRefreshForDays)
        {
            rescanTime = DateTime.Now.AddDays(1).AddHours(1); // More than one day deferred, so the deferred searches dont clog up new additions.
        }

        itemState.Items[item.Id] = new() { RefreshAgainAt = rescanTime, };
    }

    private async Task UpdateItem(CancellationToken cancellationToken, BaseItem item, string? url, IEnumerable<DddTopicItemStats> data)
    {
        // Strip old CWs
        var alreadyContainsCwsIndex = item.Overview.IndexOf("<p id=\"ddd-container\"", StringComparison.CurrentCulture);
        if (alreadyContainsCwsIndex >= 0)
        {
            item.Overview = item.Overview.Substring(0, alreadyContainsCwsIndex);
        }

        string MakeTopicSpan(DddTopicItemStats stat) => $"<span class=\"ddd-element\" title=\"{stat.Comment}\">" +
                                                        $"{stat.Topic.Name}" +
                                                        (string.IsNullOrWhiteSpace(stat.Comment) ? string.Empty : "<span style=\"font-size: 0.7rem; vertical-align: super;\">?</span>") +
                                                        "</span>";

        item.Overview += $@"<p id=""ddd-container"" style=""margin-top: 0; margin-bottom: 0.5rem;""><a style=""text-decoration: none;"" href=""{url}"" target=""_blank"">
                                    <b>Content Warnings:</b><br>"
                         + data.Aggregate(string.Empty, (acc, stats) => acc + MakeTopicSpan(stats) + ", ").TrimEnd(' ', ',')
                         + "</a></p>";

        // Write item changes to DB
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

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

    private async Task<string?> GetDddUrlForItemId(BaseItem item, CancellationToken cancellationToken)
    {
        using var httpClient = GetHttpClient();

        if (item is not (Video or Season or Series))
        {
            return null;
        }

        var (imdbId, index1, index2) = GetIdsForItem(item);

        var dddRes = await SearchDddItemByImdbId(imdbId, httpClient, cancellationToken).ConfigureAwait(false);
        var id = dddRes?.Items?.FirstOrDefault()?.Id;
        if (id == null)
        {
            return null;
        }

        var indexParamString = GetEpisodeIndexParameter(index1, index2);

        return $"{Instance!.Configuration.DddApiUrl}media/{id}?{indexParamString}";
    }

    private async Task<IEnumerable<DddTopicItemStats>?> GetDddData(BaseItem item, CancellationToken cancellationToken)
    {
        using var httpClient = GetHttpClient();

        if (item is not (Video or Season or Series))
        {
            return null;
        }

        var (imdbId, index1, index2) = GetIdsForItem(item);

        _logger.LogInformation("imdb: {0}", imdbId);

        var dddRes = await SearchDddItemByImdbId(imdbId, httpClient, cancellationToken).ConfigureAwait(false);

        var dddId = dddRes?.Items?.FirstOrDefault();
        _logger.LogInformation("dddId: {0}", dddId?.Id);

        if (dddId == null)
        {
            return null;
        }

        var sorted = await LoadDddItemTopics(cancellationToken, index1, index2, dddId, httpClient);

        foreach (var topic in sorted)
        {
            _logger.LogInformation("ddd: {0}, ({1}, {2}, \"{3}\")", topic.Topic.Name, topic.YesSum, topic.NoSum, topic.Topic.Supporters);
        }

        return sorted;
    }

    private static (string? ImdbId, int? Index1, int? Index2) GetIdsForItem(BaseItem item)
    {
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);

        int? index1 = null;
        int? index2 = null;

        switch (item)
        {
            case Episode episode:
                index2 = episode.IndexNumber;
                index1 = episode.Season.IndexNumber;
                imdbId = episode.Series.GetProviderId(MetadataProvider.Imdb);
                break;
            case Season season:
                index1 = season.IndexNumber;
                imdbId = season.Series.GetProviderId(MetadataProvider.Imdb);
                break;
        }

        return (imdbId, index1, index2);
    }

    private HttpClient GetHttpClient()
    {
        var httpClient = _httpClientFactory.CreateClient("DoesTheDogDie");
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", Instance?.Configuration.DddApiKey);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.BaseAddress = new Uri(Instance!.Configuration.DddApiUrl);
        return httpClient;
    }

    private async Task<DddSearchResponse?> SearchDddItemByImdbId(string? imdbId, HttpClient httpClient, CancellationToken cancellationToken)
    {
        _logger.LogInformation("/dddsearch?imdb={0}", imdbId);
        return await Cache.GetOrCreateAsync<DddSearchResponse>("search_ddd_imdbid_" + imdbId, async (entry) =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
                return await httpClient.GetFromJsonAsync<DddSearchResponse>($"/dddsearch?imdb={imdbId}", _jsonSettings, cancellationToken).ConfigureAwait(false);
            }
        ).ConfigureAwait(false);
    }

    private async Task<List<DddTopicItemStats>> LoadDddItemTopics(CancellationToken cancellationToken, int? index1, int? index2, DddSearchResponseItem dddId, HttpClient httpClient)
    {
        var indexParamString = GetEpisodeIndexParameter(index1, index2);
        _logger.LogInformation("/media/{0}?{1}", dddId.Id, indexParamString);

        return await Cache.GetOrCreateAsync<List<DddTopicItemStats>>("load_ddd_itemid" + dddId.Id, async (entry) =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);

                var ddd = await httpClient.GetFromJsonAsync<DddMediaResponse>($"/media/{dddId.Id}?{indexParamString}", cancellationToken).ConfigureAwait(false);

                var sorted = (ddd?.TopicItemStats ?? [])
                    .OrderByDescending(t => t.Topic.Supporters).ThenBy(t => t.YesSum)
                    .Where(topic => topic.YesSum > topic.NoSum && topic.Topic.IsVisible)
                    .ToList();
                return sorted;
            }
        ).ConfigureAwait(false);
    }

    private static string GetEpisodeIndexParameter(int? index1, int? index2)
    {
        return index1 != null ? "index1=" + index1 + "&index2=" + (index2 ?? -1) : string.Empty;
    }
}

public record DddMediaResponse
{
    public DddSearchResponseItem? Item { get; set; }
    public IEnumerable<DddTopicItemStats>? TopicItemStats { get; set; }
}

public record DddTopicItemStats
{
    public int YesSum { get; set; }
    public int NoSum { get; set; }
    public string ItemName { get; set; }
    public string Comment { get; set; }
    public bool Verified { get; set; }
    public DddTopic Topic { get; set; }
}

public record DddTopic
{
    public int Id { get; set; }
    public bool IsVisible { get; set; }
    public string Name { get; set; }
    public string NotName { get; set; }
    public string DoesName { get; set; }
    public string ListName { get; set; }
    public int Supporters { get; set; }
}

public record DddSearchResponse
{
    public IEnumerable<DddSearchResponseItem>? Items { get; set; }
}

public record DddSearchResponseItem
{
    public int Id { get; set; }
}
