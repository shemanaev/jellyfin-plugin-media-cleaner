using System;
using System.Collections.Generic;
using MediaCleaner.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace MediaCleaner
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Media Cleaner";

        public override Guid Id => Guid.Parse("607fee77-97eb-41fe-bf22-26844d99ffb0");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

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
                    Name = "MediaCleaner_commons_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.commons.js"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Users",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.users.html"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Users_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.users.js"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Locations",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.locations.html"
                },
                new PluginPageInfo
                {
                    Name = "MediaCleaner_Locations_js",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Web.locations.js"
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
                }
            };
        }
    }
}
