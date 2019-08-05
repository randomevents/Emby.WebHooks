using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Emby.WebHooks.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.WebHooks
{

    public class WebHooks : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerApplicationHost _appHost;

        private List<DeviceState> deviceState = new List<DeviceState>();
        public class DeviceState
        {
            public string deviceId { get; set; }
            public string lastState { get; set; }
        }

        public DeviceState getDeviceState(string deviceId)
        {
            var c = deviceState.Where(x => x.deviceId == deviceId).FirstOrDefault();
            if (c == null)
            {
                c = new DeviceState { deviceId = deviceId };
                deviceState.Add(c);
            }
            return c;
        }

        public static WebHooks Instance { get; private set; }

        public string Name
        {
            get { return "WebHooks"; }
        }

        public WebHooks(ISessionManager sessionManager, ILogger  logger, ILibraryManager libraryManager, IServerApplicationHost appHost)
        {
            _logger = logger;
            _libraryManager = libraryManager; //ItemRemoved
            _sessionManager = sessionManager; //AuthenticationFailed SessionStarted SessionEnded SessionActivity AuthenticationSucceeded
            _appHost = appHost;

            Instance = this;
        }

        public void Dispose()
        {
            //Unbind events
            _sessionManager.PlaybackStart -= PlaybackStart;
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            _sessionManager.PlaybackProgress -= PlaybackProgress;

            _libraryManager.ItemAdded -= ItemAdded;
        }

        public void Run()
        {
            _sessionManager.PlaybackStart += PlaybackStart;
            _sessionManager.PlaybackStopped += PlaybackStopped;
            _sessionManager.PlaybackProgress += PlaybackProgress;

            _libraryManager.ItemAdded += ItemAdded;
        }

        private void ItemAdded(object sender, ItemChangeEventArgs e)
        {
            //Only concerned with video and audio files
            if (e.Item.IsVirtualItem ||
                e.Item.MediaType != MediaType.Video && e.Item.MediaType != MediaType.Audio) return;
            
            var iType = e.Item.GetType();
            var hooks = hooksByType(iType).Where(h => h.onItemAdded);

            // Check if any hooks are configured
            if (!hooks.Any()) return;
            {
                _logger.Debug("{0} WebHooks Event(s): Item Added", hooks.Count().ToString());

                foreach (var hook in hooks)
                {
                    SendHook(hook, ReplaceAddedEventKeywords(hook, e.Item));
                }
            }
        }
        private void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (e.IsPaused && getDeviceState(e.DeviceId).lastState != "Paused" && getDeviceState(e.DeviceId).lastState != "Stopped")
            {
                PlaybackPause(sender, e);
            }
            else if (e.IsPaused == false && getDeviceState(e.DeviceId).lastState == "Paused")
            {
                PlaybackResume(sender, e);
            }

        }

        private void PlaybackPause(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Paused";          

            var iType = e.Item.GetType();
            var hooks = hooksByType(iType).Where(i => i.onPause);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackResume(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Resumed";

            var iType = e.Item.GetType();
            var hooks = hooksByType(iType).Where(i => i.onResume);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Playing";

            var iType = e.Item.GetType();
            var hooks = hooksByType(iType).Where(i => i.onPlay);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackStopped(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Stopped";

            var iType = e.Item.GetType();
            var hooks = hooksByType(iType).Where(i => i.onStop);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackMessage(IEnumerable<PluginConfiguration.Hook> hooks, PlaybackProgressEventArgs e, string action)
        {
            getDeviceState(e.DeviceId).lastState = action;

            if (!hooks.Any()) return;
            
            _logger.Debug("{0} WebHooks Event(s): {1}", hooks.Count().ToString(), action);

            foreach (var hook in hooks)
            {
                string msgString = ReplacePlaybackEventKeywords(hook, _sessionManager.GetSession(e.DeviceId, e.ClientName, ""), e, action);
                if (msgString != "_redundant_")
                {
                    SendHook(hook, msgString);
                }
            }
        }

        private IEnumerable<PluginConfiguration.Hook> hooksByType(Type type)
        {
            _logger.Debug("Checking hook with {0}", type.Name);
            return Plugin.Instance.Configuration.Hooks.Where(h =>
                    h.withMovies && type == typeof(Movie) ||
                    h.withEpisodes && type == typeof(Episode) ||
                    h.withSongs && type == typeof(Audio)
            );
        }

        private async void SendHook(PluginConfiguration.Hook h, string jsonString)
        {
            _logger.Debug("WebHook sent to {0}", h.URL);
            _logger.Debug("{0}", jsonString);

            using (var client = new HttpClient())
            {
                    var httpContent = new StringContent(jsonString, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(h.URL, httpContent);
                    var responseString = await response.Content.ReadAsStringAsync();
                    _logger.Debug("{0}", responseString);
            }
        }

        private string ReplaceAddedEventKeywords(PluginConfiguration.Hook hooks, BaseItem e)
        {
            return ReplaceBaseKeywords(hooks.msgServerEventHook, e, "Added");
        }

        private string ReplacePlaybackEventKeywords(PluginConfiguration.Hook hooks, SessionInfo sessionInfo, PlaybackProgressEventArgs playbackData, string trigger)
        {
            if ((trigger == "Paused" || trigger == "Resumed") && string.IsNullOrEmpty(sessionInfo.NowPlayingItem.ToString())) return "_redundant_";

            string playbackTicks = (string.IsNullOrEmpty(playbackData.PlaybackPositionTicks.ToString())) ? sessionInfo.PlayState.PositionTicks.ToString() : playbackData.PlaybackPositionTicks.ToString();

            string msgPlayback = ReplaceBaseKeywords(hooks.msgPlaybackEventHook, sessionInfo.FullNowPlayingItem, trigger);

            return msgPlayback
                .Replace("{{UserID}}", sessionInfo.UserId)
                .Replace("{{UserName}}", sessionInfo.UserName)
                .Replace("{{DeviceID}}", sessionInfo.DeviceId)
                .Replace("{{SessionPlaybackPositionTicks}}", playbackTicks);
        }

        private string ReplaceBaseKeywords(string inStr, BaseItem e, string trigger)
        {
            return inStr
                .Replace("{{Event}}",  trigger)
                .Replace("{{ItemType}}", e.GetType().Name)
                .Replace("{{ItemName}}", e.Name)
                .Replace("{{ItemNameParent}}", e.Parent.Name)
                .Replace("{{ItemNameGrandparent}}", e.Parent.Parent.Name)
                .Replace("{{ItemID}}", e.Id.ToString())
                .Replace("{{ItemRunTimeTicks}}", e.RunTimeTicks.GetValueOrDefault().ToString())
                .Replace("{{ItemIndex}}", e.IndexNumber.GetValueOrDefault().ToString())
                .Replace("{{ItemParentIndex}}", e.ParentIndexNumber.GetValueOrDefault().ToString())
                .Replace("{{ItemDateAdded}}", e.DateCreated.ToString())
                .Replace("{{ItemYear}}", e.ProductionYear.GetValueOrDefault().ToString())
                .Replace("{{ItemGenre}}",  string.Join(",", e.Genres))
                .Replace("{{ServerID}}",  _appHost.SystemId)
                .Replace("{{ServerName}}",  _appHost.FriendlyName)
                .Replace("{{TimeStamp}}",  DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        }
    }
}
