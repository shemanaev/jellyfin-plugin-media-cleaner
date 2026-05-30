using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaCleaner.Controllers;

public sealed record LeavingSoonResponse(
    bool CollectionExists,
    Guid? CollectionId,
    string CollectionName,
    IReadOnlyList<LeavingSoonItemDto> Items,
    int TotalCount);

public sealed record LeavingSoonItemDto(
    DateTime DateCreated,
    Guid Id,
    string Name,
    int? ProductionYear,
    string? SeasonName,
    string? SeriesName,
    string Type);

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner/LeavingSoon")]
public class LeavingSoonController(ILibraryManager libraryManager) : ControllerBase
{
    [HttpGet]
    public ActionResult<LeavingSoonResponse> Get()
    {
        var collection = LeavingSoonCollectionService.GetExistingCollection(libraryManager);

        if (collection is null)
        {
            return Ok(new LeavingSoonResponse(
                false,
                null,
                LeavingSoonCollectionService.CollectionName,
                [],
                0));
        }

        var query = new InternalItemsQuery
        {
            CollapseBoxSetItems = false,
            Recursive = true,
            Parent = collection
        };
        var items = collection.GetItems(query)
            .Items
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Select(ToDto)
            .ToList();

        return Ok(new LeavingSoonResponse(
            true,
            collection.Id,
            LeavingSoonCollectionService.CollectionName,
            items,
            items.Count));
    }

    private static LeavingSoonItemDto ToDto(BaseItem item) =>
        new(
            item.DateCreated,
            item.Id,
            item.Name ?? string.Empty,
            item.ProductionYear,
            GetSeasonName(item),
            GetSeriesName(item),
            item.GetType().Name);

    private static string? GetSeasonName(BaseItem item) =>
        item switch
        {
            Episode episode => episode.SeasonName,
            Season season => season.Name,
            _ => null
        };

    private static string? GetSeriesName(BaseItem item) =>
        item switch
        {
            Episode episode => episode.SeriesName,
            Season season => season.SeriesName,
            Series series => series.Name,
            _ => null
        };
}
