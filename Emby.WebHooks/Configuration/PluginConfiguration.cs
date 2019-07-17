using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Emby.WebHooks.Configuration

{

    public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration

    {
        public PluginConfiguration()

        {
            Hooks = new Hook[] { };
        }

        public Hook[] Hooks { get; set; }

        public class Hook

        {
            public String Name { get; set; }
            public string URL { get; set; }
            public bool onPlay { get; set; }
            public bool onPause { get; set; }
            public bool onStop { get; set; }
            public bool onResume { get; set; }
            public bool onItemAdded { get; set; }

            public bool withMovies { get; set; }
            public bool withEpisodes { get; set; }
            public bool withSongs { get; set; }

            public string msgPlaybackEventHook { get; set; }
            public string msgServerEventHook { get; set; }

        }

    }

}
