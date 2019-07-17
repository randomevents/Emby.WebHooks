using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

namespace Emby.WebHooks
{
    public class Plugin : MediaBrowser.Common.Plugins.BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name
        {
            get
            {
                return "WebHooks";
            }
        }

        public override string Description
        {
            get
            {
                return "WebHooks handler for Emby";
            }
        }

        public static Plugin Instance { get; private set; }

        private Guid _id = new Guid("426eb1da-a1de-4ae2-bb79-cb7acb302997");
        public override Guid Id
        {
            get { return _id; }
        }


        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
                }

            };
        }
    }
}
