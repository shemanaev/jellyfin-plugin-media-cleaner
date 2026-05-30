using System.Reflection;
using System.Runtime.Serialization;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Collections;
using MediaCleaner.Controllers;
using MediaCleaner.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace MediaCleaner.Tests;

public class LeavingSoonDashboardTests
{
    [Fact]
    public void Leaving_soon_endpoint_is_elevation_gated_and_read_only()
    {
        var controllerType = RequiredType("MediaCleaner.Controllers.LeavingSoonController");

        var authorize = Assert.Single(controllerType.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
        Assert.Contains(controllerType.GetCustomAttributes<RouteAttribute>(), route => route.Template == "MediaCleaner/LeavingSoon");

        var actions = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        var action = Assert.Single(actions);
        Assert.Contains(action.GetCustomAttributes<HttpGetAttribute>(), _ => true);
        Assert.DoesNotContain(action.GetCustomAttributes<HttpMethodAttribute>(), IsMutatingHttpVerb);
    }

    [Fact]
    public void Leaving_soon_endpoint_uses_only_read_dependencies()
    {
        var controllerType = RequiredType("MediaCleaner.Controllers.LeavingSoonController");
        var forbidden = new[]
        {
            typeof(ICollectionManager),
            typeof(ArrDeletionService),
            typeof(ArrDeletionPlanner),
            typeof(RadarrClient),
            typeof(SonarrClient)
        };

        var dependencies = controllerType
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(controllerType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(field => field.FieldType))
            .Concat(controllerType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(property => property.PropertyType))
            .ToList();

        foreach (var dependency in dependencies)
        {
            Assert.DoesNotContain(forbidden, forbiddenType => forbiddenType.IsAssignableFrom(dependency));
            Assert.False(
                dependency.Namespace?.StartsWith("MediaCleaner.Integrations", StringComparison.Ordinal) == true,
                $"Leaving Soon endpoint must not depend on Arr integration type {dependency.FullName}.");
        }
    }

    [Fact]
    public void Leaving_soon_response_contract_exposes_only_dashboard_safe_fields()
    {
        var responseType = RequiredType("MediaCleaner.Controllers.LeavingSoonResponse");
        var itemType = RequiredType("MediaCleaner.Controllers.LeavingSoonItemDto");

        Assert.Equal(
            ["CollectionExists", "CollectionId", "CollectionName", "Items", "TotalCount"],
            PublicPropertyNames(responseType));
        Assert.Equal(
            ["DateCreated", "Id", "Name", "ProductionYear", "SeasonName", "SeriesName", "Type"],
            PublicPropertyNames(itemType));

        var unsafeTerms = new[] { "ApiKey", "Arr", "Deletion", "External", "File", "Path", "Plan", "Provider" };
        Assert.DoesNotContain(
            responseType.GetProperties().Concat(itemType.GetProperties()),
            property => unsafeTerms.Any(term => property.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Leaving_soon_dashboard_tab_is_registered_with_plugin_pages_and_tabs()
    {
        var pages = CreateUninitialisedPlugin().GetPages().ToList();
        Assert.Contains(pages, page => page.Name == "MediaCleaner_LeavingSoon" && page.EmbeddedResourcePath == "MediaCleaner.Web.leavingsoon.html");
        Assert.Contains(pages, page => page.Name == "MediaCleaner_LeavingSoon_js" && page.EmbeddedResourcePath == "MediaCleaner.Web.leavingsoon.js");

        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();
        Assert.Contains("MediaCleaner.Web.leavingsoon.html", resources);
        Assert.Contains("MediaCleaner.Web.leavingsoon.js", resources);

        using var commonsStream = typeof(Plugin).Assembly.GetManifestResourceStream("MediaCleaner.Web.commons.js");
        Assert.NotNull(commonsStream);
        using var reader = new StreamReader(commonsStream);
        var commons = reader.ReadToEnd();

        Assert.Contains("MediaCleaner_LeavingSoon", commons);
        Assert.Contains("Leaving Soon", commons);
        Assert.Contains("TabLeavingSoon", commons);
    }

    private static Type RequiredType(string fullName) =>
        Type.GetType($"{fullName}, MediaCleaner", throwOnError: false)
        ?? throw new Xunit.Sdk.XunitException($"Expected type {fullName} to exist.");

    private static string[] PublicPropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order()
            .ToArray();

    private static bool IsMutatingHttpVerb(HttpMethodAttribute attribute) =>
        attribute.HttpMethods.Any(method => method is "POST" or "PUT" or "PATCH" or "DELETE");

    private static Plugin CreateUninitialisedPlugin()
    {
#pragma warning disable SYSLIB0050
        return (Plugin)FormatterServices.GetUninitializedObject(typeof(Plugin));
#pragma warning restore SYSLIB0050
    }
}
