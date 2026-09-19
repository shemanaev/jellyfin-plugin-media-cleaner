using System;
using System.IO;
using FluentAssertions;
using MediaBrowser.Common.Configuration;
using MediaCleaner.LeavingSoon;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MediaCleaner.Tests;

public sealed class LeavingSoonWebClientInstallerTests
{
    [Fact]
    public void ItemActionScriptIsInstalledBeforeBodyAndIsIdempotent()
    {
        const string original = "<!doctype html>\n<html><body><main>Jellyfin</main></body></html>";

        LeavingSoonWebClientInstaller.EnsureScriptInstalled(original, out var installed).Should().BeTrue();
        installed.Should().Contain(LeavingSoonWebClientInstaller.ScriptStart);
        installed.Should().Contain(LeavingSoonWebClientInstaller.ScriptTag);
        installed.IndexOf(LeavingSoonWebClientInstaller.ScriptTag, StringComparison.Ordinal)
            .Should().BeLessThan(installed.IndexOf("</body>", StringComparison.Ordinal));

        LeavingSoonWebClientInstaller.EnsureScriptInstalled(installed, out var unchanged).Should().BeFalse();
        unchanged.Should().Be(installed);
    }

    [Fact]
    public void ItemActionScriptUsesAssemblySpecificCacheKey()
    {
        var first = LeavingSoonWebClientInstaller.CreateScriptTag(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var second = LeavingSoonWebClientInstaller.CreateScriptTag(Guid.Parse("20000000-0000-0000-0000-000000000002"));

        first.Should().Contain("name=MediaCleaner_LeavingSoonItemMenu_js&v=10000000000000000000000000000001");
        second.Should().Contain("name=MediaCleaner_LeavingSoonItemMenu_js&v=20000000000000000000000000000002");
        second.Should().NotBe(first);
    }

    [Fact]
    public void ItemActionScriptInstallRepairsDuplicatesAndUninstallPreservesWebIndex()
    {
        var owned = $"{LeavingSoonWebClientInstaller.ScriptStart}\n{LeavingSoonWebClientInstaller.ScriptTag}\n{LeavingSoonWebClientInstaller.ScriptEnd}\n";
        var duplicated = $"<html><body>before\n{owned}{owned}</body></html>";

        LeavingSoonWebClientInstaller.EnsureScriptInstalled(duplicated, out var repaired).Should().BeTrue();
        repaired.Split(LeavingSoonWebClientInstaller.ScriptStart).Should().HaveCount(2);

        LeavingSoonWebClientInstaller.EnsureScriptRemoved(repaired, out var removed).Should().BeTrue();
        removed.Should().Be("<html><body>before\n</body></html>");
        LeavingSoonWebClientInstaller.EnsureScriptRemoved(removed, out var unchanged).Should().BeFalse();
        unchanged.Should().Be(removed);
    }

    [Fact]
    public void ItemActionScriptRequiresBodyAndRejectsIncompleteOwnedBlock()
    {
        var noBody = () => LeavingSoonWebClientInstaller.EnsureScriptInstalled("<html></html>", out _);
        noBody.Should().Throw<InvalidDataException>();

        var incomplete = () => LeavingSoonWebClientInstaller.EnsureScriptRemoved(
            $"<html><body>{LeavingSoonWebClientInstaller.ScriptStart}</body></html>", out _);
        incomplete.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void InstallerReportsInstalledStatusAndWhetherIndexChanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "media-cleaner-installer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "index.html"), "<html><body></body></html>");
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(x => x.WebPath).Returns(directory);
            var installer = new LeavingSoonWebClientInstaller(paths.Object, NullLogger.Instance);

            installer.Install();

            installer.Status.State.Should().Be(WebClientInjectionState.Installed);
            installer.Status.Changed.Should().BeTrue();
            installer.Status.IndexPath.Should().Be(Path.Combine(directory, "index.html"));
            installer.Install();
            installer.Status.State.Should().Be(WebClientInjectionState.Installed);
            installer.Status.Changed.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InstallerReportsMissingWebIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "media-cleaner-installer-tests", Guid.NewGuid().ToString("N"));
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(x => x.WebPath).Returns(directory);
        var installer = new LeavingSoonWebClientInstaller(paths.Object, NullLogger.Instance);

        installer.Install();

        installer.Status.State.Should().Be(WebClientInjectionState.MissingWebIndex);
        installer.Status.Error.Should().NotBeNullOrWhiteSpace();
    }
}
