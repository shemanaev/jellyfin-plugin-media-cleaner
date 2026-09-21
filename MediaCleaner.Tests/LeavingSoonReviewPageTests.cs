using System.IO;
using System.Reflection;
using FluentAssertions;

namespace MediaCleaner.Tests;

public sealed class LeavingSoonReviewPageTests
{
    [Fact]
    public void ReviewPageUsesRegisteredCommonsModuleAndProvidesTabsHost()
    {
        var assembly = typeof(Plugin).Assembly;
        var script = ReadResource(assembly, "MediaCleaner.Web.leavingSoonReview.js");
        var commons = ReadResource(assembly, "MediaCleaner.Web.commons.js");
        var page = ReadResource(assembly, "MediaCleaner.Web.leavingSoonReview.html");

        script.Should().Contain($"name: '{Plugin.CommonsPageName}'");
        commons.Should().Contain("name: 'Leaving Soon keeps'");
        page.Should().Contain("id=\"navigationTabs\"");
    }

    [Fact]
    public void LeavingSoonSettingsBelongToAdvancedPage()
    {
        var assembly = typeof(Plugin).Assembly;
        var generalPage = ReadResource(assembly, "MediaCleaner.Web.general.html");
        var generalScript = ReadResource(assembly, "MediaCleaner.Web.general.js");
        var advancedPage = ReadResource(assembly, "MediaCleaner.Web.advanced.html");
        var advancedScript = ReadResource(assembly, "MediaCleaner.Web.advanced.js");

        generalPage.Should().NotContain("id=\"MediaCleanerLeavingSoonEnabled\"");
        generalScript.Should().NotContain("config.LeavingSoon =");
        advancedPage.Should().Contain("id=\"MediaCleanerLeavingSoonEnabled\"");
        advancedScript.Should().Contain("config.LeavingSoon =");
        advancedScript.Should().Contain("MediaCleaner/LeavingSoon/Admin/Refresh");
        generalPage.Should().NotContain("MediaCleanerLeavingSoonTasksLink");
        advancedPage.Should().NotContain("MediaCleanerLeavingSoonTasksLink");
    }

    [Fact]
    public void GeneralPageShowsOnlyLeavingSoonRefreshStatus()
    {
        var assembly = typeof(Plugin).Assembly;
        var page = ReadResource(assembly, "MediaCleaner.Web.general.html");
        var script = ReadResource(assembly, "MediaCleaner.Web.general.js");

        page.Should().Contain("Last Leaving Soon refresh");
        script.Should().NotContain("warning(s)");
        script.Should().NotContain("personal protection(s)");
    }

    [Fact]
    public void GeneralPageShowsWebClientInjectionStatus()
    {
        var assembly = typeof(Plugin).Assembly;
        var page = ReadResource(assembly, "MediaCleaner.Web.general.html");
        var script = ReadResource(assembly, "MediaCleaner.Web.general.js");

        page.Should().Contain("id=\"MediaCleanerWebClientInjectionState\"");
        script.Should().Contain("WebClientInjectionState");
        script.Should().Contain("WebClientInjectionMethod");
        script.Should().Contain("File Transformation");
        script.Should().Contain("JavaScript Injector");
        script.Should().Contain("startup filter");
        script.Should().Contain("index.html fallback");
        script.Should().Contain("Install failed:");
        script.Should().Contain("index.html was not found");
    }

    [Fact]
    public void ReviewPageDisclosesGlobalRemoveAllScopeAndCount()
    {
        var script = ReadResource(typeof(Plugin).Assembly, "MediaCleaner.Web.leavingSoonReview.js");

        script.Should().Contain("NoticeProtectionCount");
        script.Should().Contain("Remove all ${protectionCount}");
        script.Should().Contain("across all users");
        script.Should().Contain("hidden by the current search and other pages");
        script.Should().Contain("page._mcNoticeNames = new Map");
        script.Should().NotContain("data-item-name=");
    }

    [Fact]
    public void WebClientModuleAddsNativeDeadlineTextOnlyToOwnedCollectionCards()
    {
        var script = ReadResource(typeof(Plugin).Assembly, "MediaCleaner.Web.leavingSoonItemMenu.js");

        script.Should().Contain(".collectionItems .collectionItemsContainer .card[data-id]");
        script.Should().Contain("/Collections/${encodeURIComponent(pageId)}/Cards");
        script.Should().Contain("IsOwned");
        script.Should().Contain("DeleteAfterUtc");
        script.Should().Contain("cardText cardText-secondary");
        script.Should().Contain("`${days}d left`");
        script.Should().Contain("`${hours}h left`");
        script.Should().Contain("'<1h left'");
        script.Should().Contain("'Due now'");
        script.Should().Contain("'Kept'");
        script.Should().Contain("collectionDeadlineSpacing = '3em'");
        script.Should().Contain("card.querySelector('.cardFooter') || cardBox");
        script.Should().Contain("cardTextCentered");
        script.Should().Contain("style.setProperty('margin-bottom', collectionDeadlineSpacing, 'important')");
        script.Should().Contain("scheduleActionSheetViewportFit(container)");
        script.Should().Contain("dialog.getBoundingClientRect()");
        script.Should().Contain("scroller.style.setProperty('max-height'");
        script.Should().Contain("container.querySelector('[data-id=\"removefromcollection\"]')");
        script.Should().Contain("button.removeAttribute('data-id')");
        script.Should().Contain("if (!nativeRemove) container.appendChild(button)");
        script.Should().Contain("scheduleActionSheetViewportFit(container)");
        script.Should().Contain("'Keep remaining until watched'");
        script.Should().Contain("'CanRemove', 'canRemove'");
        script.Should().Contain("querySelectorAll('[data-media-cleaner-leaving-soon-action]')");
    }

    [Fact]
    public void WebClientModuleAddsPlainDeadlineTextToActiveItemDetails()
    {
        var script = ReadResource(typeof(Plugin).Assembly, "MediaCleaner.Web.leavingSoonItemMenu.js");

        script.Should().Contain(".itemDetailPage:not(.hide) .itemMiscInfo-primary");
        script.Should().Contain("line.className = 'mediaInfoItem'");
        script.Should().NotContain("line.className = 'mediaInfoItem mediaInfoText'");
        script.Should().Contain("`Deletes in ${Math.ceil(remaining / day)}d`");
        script.Should().Contain("`Deletes in ${Math.ceil(remaining / hour)}h`");
        script.Should().Contain("'Deletes within 1h'");
        script.Should().Contain("'Kept until watched'");
        script.Should().Contain("'Protected by another user'");
        script.Should().Contain("resetDetailDeadlineContext()");
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        stream.Should().NotBeNull($"{name} should be embedded in the plugin assembly");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
