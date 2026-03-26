using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DDD;

[ApiController]
[Route("/plugin/ddd")]
public class DddController(
    ILibraryManager libraryManager,
    IHttpClientFactory httpClientFactory,
    ILogger<DddController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions _jsonSettings = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip, PropertyNameCaseInsensitive = true };

    private static readonly MemoryCache Cache = new(new MemoryDistributedCacheOptions() { SizeLimit = null });

    [HttpPost("{itemId}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<DddApiResponseModel>>> GetDddHtml([FromRoute] Guid itemId, CancellationToken ct)
    {
        var data = await GetDddData(itemId, ct).ConfigureAwait(false);

        if (data == null)
        {
            return NotFound();
        }

        var models = data.Select(t => new DddApiResponseModel() { Name = t.Topic.Name, Comment = t.Comment });

        return Ok(models);
    }

    [HttpPost("{itemId}/dddUrl")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<string>> GetDddUrl([FromRoute] Guid itemId, CancellationToken ct)
    {
        var url = await GetDddUrlForItemId(itemId, ct).ConfigureAwait(false);

        if (url == null)
        {
            return NotFound();
        }

        return Ok(url);
    }

    private async Task<string?> GetDddUrlForItemId(Guid itemId, CancellationToken cancellationToken)
    {
        var httpClient = GetHttpClient();
        var item = libraryManager.GetItemById(itemId);

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

        return $"{DoesTheDogDieJellyfinIntegrationPlugin.Instance!.Configuration.DddApiUrl}media/{id}?{indexParamString}";
    }

    private async Task<IEnumerable<DddTopicItemStats>?> GetDddData(Guid itemId, CancellationToken cancellationToken)
    {
        var httpClient = GetHttpClient();
        var item = libraryManager.GetItemById(itemId);

        if (item is not (Video or Season or Series))
        {
            return null;
        }

        var (imdbId, index1, index2) = GetIdsForItem(item);

        logger.LogInformation("imdb: {0}", imdbId);

        var dddRes = await SearchDddItemByImdbId(imdbId, httpClient, cancellationToken).ConfigureAwait(false);

        var dddId = dddRes?.Items.FirstOrDefault();
        logger.LogInformation("dddId: {0}", dddId?.Id);

        if (dddId == null)
        {
            return null;
        }

        var sorted = await LoadDddItemTopics(cancellationToken, index1, index2, dddId, httpClient);

        foreach (var topic in sorted)
        {
            logger.LogInformation("ddd: {0}, ({1}, {2}, \"{3}\")", topic.Topic.Name, topic.YesSum, topic.NoSum, topic.Topic.Supporters);
        }

        return sorted;

    }

    private static (string? imdbId, int? index1, int? index2) GetIdsForItem(BaseItem item)
    {
        var imdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);

        int? index1 = null;
        int? index2 = null;

        if (item is Episode episode)
        {
            index2 = episode.IndexNumber;
            index1 = episode.Season.IndexNumber;
            imdbId = episode.Series.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
        }
        else if (item is Season season)
        {
            index1 = season.IndexNumber;
            imdbId = season.Series.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
        }

        return (imdbId, index1, index2);
    }

    private HttpClient GetHttpClient()
    {
        var httpClient = httpClientFactory.CreateClient("DoesTheDogDie");
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", DoesTheDogDieJellyfinIntegrationPlugin.Instance?.Configuration.DddApiKey);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.BaseAddress = new Uri(DoesTheDogDieJellyfinIntegrationPlugin.Instance!.Configuration.DddApiUrl);
        return httpClient;
    }

    private async Task<DddSearchResponse?> SearchDddItemByImdbId(string? imdbId, HttpClient httpClient, CancellationToken cancellationToken)
    {
#pragma warning disable CA2254
        logger.LogInformation($"/dddsearch?imdb={imdbId}");
        return await Cache.GetOrCreateAsync<DddSearchResponse>("search_ddd_imdbid_" + imdbId, async (entry) =>
            await httpClient.GetFromJsonAsync<DddSearchResponse>(
                $"/dddsearch?imdb={imdbId}",
                _jsonSettings,
                cancellationToken).ConfigureAwait(false)
        ).ConfigureAwait(false);
    }

    private async Task<List<DddTopicItemStats>> LoadDddItemTopics(CancellationToken cancellationToken, int? index1, int? index2, DddSearchResponseItem dddId, HttpClient httpClient)
    {
        var indexParamString = GetEpisodeIndexParameter(index1, index2);
        logger.LogInformation($"/media/{dddId.Id}?{indexParamString}");

        return await Cache.GetOrCreateAsync<List<DddTopicItemStats>>("load_ddd_itemid" + dddId.Id, async (entry) =>
            {
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

public record DddApiResponseModel
{
    public string Name { get; set; }
    public string Comment { get; set; }
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
