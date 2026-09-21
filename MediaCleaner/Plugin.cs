using System;
using System.Collections.Generic;
using MediaCleaner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaCleaner.LeavingSoon;

namespace MediaCleaner
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        internal const string CommonsPageName = "MediaCleaner_commons_js";
        private readonly string _webIndexPath;

        public override string Name => "Media Cleaner";

        public override Guid Id => Guid.Parse("607fee77-97eb-41fe-bf22-26844d99ffb0");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _webIndexPath = System.IO.Path.Combine(applicationPaths.WebPath, "index.html");
        }

        public static Plugin? Instance { get; private set; }

        internal WebClientInjectionStatus WebClientInjectionStatus =>
            LeavingSoonWebClientInstaller.Current?.Status ?? new WebClientInjectionStatus(
                WebClientInjectionState.NotChecked,
                WebClientInjectionMethod.None,
                _webIndexPath,
                false);

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var updated = (PluginConfiguration)configuration;
            var current = Configuration.LeavingSoon ?? new LeavingSoonConfiguration();
            updated.LeavingSoon ??= new LeavingSoonConfiguration();
            updated.LeavingSoon.NoticeGeneration = current.Enabled == updated.LeavingSoon.Enabled
                ? NormalizeNoticeGeneration(current.NoticeGeneration)
                : Guid.NewGuid().ToString("N");
            base.UpdateConfiguration(updated);
        }

        private static string NormalizeNoticeGeneration(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "initial" : value;

        public override void OnUninstalling()
        {
            LeavingSoonWebClientInstaller.Current?.Remove();
            base.OnUninstalling();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "MediaCleaner",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.general.html"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.general.js"
                },
                new PluginPageInfo
                {
                    Name = CommonsPageName,
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.commons.js"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Advanced",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.advanced.html"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Advanced_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.advanced.js"
                },

                new PluginPageInfo
                {
                    Name = "MediaCleaner_Troubleshooting",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.troubleshooting.html"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Troubleshooting_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.troubleshooting.js"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_LeavingSoonItemMenu_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.leavingSoonItemMenu.js"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_LeavingSoonReview",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.leavingSoonReview.html"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_LeavingSoonReview_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.leavingSoonReview.js"
                }
            };
        }
    }
}
