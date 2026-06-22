using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaCleaner.Compatibility;
using MediaCleaner.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner")]
public class TagController : ControllerBase
{
    private static readonly BaseItemKind[] TaggableItemTypes =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.Audio,
        BaseItemKind.AudioBook,
    ];

    private readonly ILogger<TagController> _logger;
    private readonly ILibraryManager _libraryManager;

    public TagController(
        ILogger<TagController> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public class ReplaceTagRequest
    {
        public string? OldTag { get; set; }
        public string? NewTag { get; set; }
    }

    [HttpPost("PreviewTagReplacement")]
    public ActionResult PreviewTagReplacement([FromBody] ReplaceTagRequest request)
    {
        if (!TryValidateRequest(request, out var error))
        {
            _logger.LogWarning("Invalid tags provided. OldTag: '{OldTag}', NewTag: '{NewTag}'", request?.OldTag ?? "(null)", request?.NewTag ?? "(null)");
            return BadRequest(error);
        }

        try
        {
            var data = BuildReplacementData(request!);
            var plan = data.Plan;
            return Ok(new
            {
                Success = true,
                OldTag = plan.OldTag,
                NewTag = plan.NewTag,
                UpdatedCount = plan.UpdatedCount,
                SkippedCount = plan.SkippedCount,
                ErrorCount = plan.ErrorCount,
                TotalProcessed = plan.TotalProcessed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error previewing tag replacement '{OldTag}' -> '{NewTag}'", request!.OldTag, request.NewTag);
            return StatusCode(500, $"Error previewing tag replacement: {ex.Message}");
        }
    }

    [HttpPost("ReplaceTag")]
    public async Task<ActionResult> ReplaceTag([FromBody] ReplaceTagRequest request)
    {
        if (!TryValidateRequest(request, out var error))
        {
            _logger.LogWarning("Invalid tags provided. OldTag: '{OldTag}', NewTag: '{NewTag}'", request?.OldTag ?? "(null)", request?.NewTag ?? "(null)");
            return BadRequest(error);
        }

        _logger.LogInformation("Starting tag replacement: '{OldTag}' -> '{NewTag}'", request.OldTag, request.NewTag);
        try
        {
            var data = BuildReplacementData(request);
            var plan = data.Plan;
            var itemsById = data.Items.ToDictionary(item => item.Id.ToString("N"), StringComparer.OrdinalIgnoreCase);

            var updatedCount = 0;
            var errorCount = 0;
            foreach (var update in plan.Updates)
            {
                try
                {
                    var item = itemsById[update.ItemId];
                    item.Tags = update.Tags.ToArray();
                    _logger.LogDebug("Updating item '{ItemName}' (ID: {ItemId}) with new tags: [{Tags}]", item.Name, item.Id, string.Join(", ", item.Tags));
                    await JellyfinCompatibility.UpdateMetadataAsync(_libraryManager, item, CancellationToken.None);
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating tags for item '{ItemId}'", update.ItemId);
                    errorCount++;
                }
            }

            _logger.LogInformation("Tag replacement completed: '{OldTag}' -> '{NewTag}'. Stats: Updated: {Updated}, Skipped: {Skipped}, Errors: {Errors}", request.OldTag, request.NewTag, updatedCount, plan.SkippedCount, errorCount);
            return Ok(new
            {
                Success = true,
                OldTag = request.OldTag,
                NewTag = request.NewTag,
                UpdatedCount = updatedCount,
                SkippedCount = plan.SkippedCount,
                ErrorCount = errorCount,
                TotalProcessed = plan.TotalProcessed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replacing tags '{OldTag}' -> '{NewTag}'", request.OldTag, request.NewTag);
            return StatusCode(500, $"Error replacing tags: {ex.Message}");
        }
    }

    private static bool TryValidateRequest(ReplaceTagRequest? request, out string error)
    {
        if (string.IsNullOrWhiteSpace(request?.OldTag) || string.IsNullOrWhiteSpace(request.NewTag))
        {
            error = "Old tag and new tag must be provided";
            return false;
        }

        if (string.Equals(request.OldTag.Trim(), request.NewTag.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = "Old tag and new tag must be different";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private ReplacementData BuildReplacementData(ReplaceTagRequest request)
    {
        var itemsWithTag = GetItemsWithTag(request.OldTag!);
        var planner = new TagReplacementPlanner();
        var plan = planner.Plan(
            request.OldTag!.Trim(),
            request.NewTag!.Trim(),
            itemsWithTag.Select(item => new TagReplacementItem(item.Id.ToString("N"), item.Tags)).ToList());
        return new ReplacementData(plan, itemsWithTag);
    }

    private List<BaseItem> GetItemsWithTag(string oldTag)
    {
        var normalizedOldTag = oldTag.Trim();
        _logger.LogDebug("Querying items with tag '{OldTag}'", normalizedOldTag);
        var allItems = JellyfinCompatibility.GetItemList(
            _libraryManager,
            new InternalItemsQuery
            {
                IncludeItemTypes = TaggableItemTypes,
                IsVirtualItem = false,
            });
        var itemsWithTag = allItems.Where(item =>
            item.Tags != null &&
            item.Tags.Contains(normalizedOldTag, StringComparer.OrdinalIgnoreCase)
        ).ToList();
        _logger.LogInformation("Found {Count} items with tag '{OldTag}'", itemsWithTag.Count, normalizedOldTag);
        return itemsWithTag;
    }

    private sealed record ReplacementData(TagReplacementPlan Plan, IReadOnlyList<BaseItem> Items);
}
