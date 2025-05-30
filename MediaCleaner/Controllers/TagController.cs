using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaCleaner.Controllers;

[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("MediaCleaner")]
public class TagController : ControllerBase
{
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

    [HttpPost("ReplaceTag")]
    public async Task<ActionResult> ReplaceTag([FromBody] ReplaceTagRequest request)
    {
        if (string.IsNullOrEmpty(request.OldTag) || string.IsNullOrEmpty(request.NewTag))
        {
            _logger.LogWarning("Invalid tags provided. OldTag: '{OldTag}', NewTag: '{NewTag}'", request?.OldTag ?? "(null)", request?.NewTag ?? "(null)");
            return BadRequest("Old tag and new tag must be provided");
        }

        _logger.LogInformation("Starting tag replacement: '{OldTag}' -> '{NewTag}'", request.OldTag, request.NewTag);
        try
        {
            _logger.LogDebug("Querying items with tag '{OldTag}'", request.OldTag);
            var allItems = _libraryManager.GetItemList(new InternalItemsQuery());
            var itemsWithTag = allItems.Where(item => 
                item.Tags != null && 
                item.Tags.Contains(request.OldTag, StringComparer.OrdinalIgnoreCase)
            ).ToList();
            _logger.LogInformation("Found {Count} items with tag '{OldTag}'", itemsWithTag.Count, request.OldTag);

            int updatedCount = 0;
            int errorCount = 0;
            int skippedCount = 0;
            foreach (var item in itemsWithTag)
            {
                try 
                {
                    if (item.Tags == null)
                    {
                        _logger.LogDebug("Item '{ItemName}' (ID: {ItemId}) has no tags collection, skipping", item.Name, item.Id);
                        skippedCount++;
                        continue;
                    }
                    var tags = item.Tags.ToList();
                    string exactOldTag = tags.FirstOrDefault(t => string.Equals(t, request.OldTag, StringComparison.OrdinalIgnoreCase)) ?? request.OldTag;
                    _logger.LogDebug("Processing item '{ItemName}' (ID: {ItemId}) - original tags: [{Tags}]", item.Name, item.Id, string.Join(", ", tags));
                    if (tags.Contains(exactOldTag))
                    {
                        tags.Remove(exactOldTag);
                        if (!tags.Any(t => string.Equals(t, request.NewTag, StringComparison.OrdinalIgnoreCase)))
                        {
                            tags.Add(request.NewTag);
                        }
                        item.Tags = tags.ToArray();
                        _logger.LogDebug("Updating item '{ItemName}' (ID: {ItemId}) with new tags: [{Tags}]", item.Name, item.Id, string.Join(", ", item.Tags));
                        await _libraryManager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, CancellationToken.None);
                        updatedCount++;
                    }
                    else
                    {
                        _logger.LogWarning("Item '{ItemName}' (ID: {ItemId}) was returned in query but doesn't have tag '{OldTag}'", item.Name, item.Id, request.OldTag);
                        skippedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating tags for item '{ItemName}' (ID: {ItemId})", item.Name, item.Id);
                    errorCount++;
                }
            }
            _logger.LogInformation("Tag replacement completed: '{OldTag}' -> '{NewTag}'. Stats: Updated: {Updated}, Skipped: {Skipped}, Errors: {Errors}", request.OldTag, request.NewTag, updatedCount, skippedCount, errorCount);
            return Ok(new
            {
                Success = true,
                OldTag = request.OldTag,
                NewTag = request.NewTag,
                UpdatedCount = updatedCount,
                SkippedCount = skippedCount,
                ErrorCount = errorCount,
                TotalProcessed = itemsWithTag.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replacing tags '{OldTag}' -> '{NewTag}'", request.OldTag, request.NewTag);
            return StatusCode(500, $"Error replacing tags: {ex.Message}");
        }
    }
}
