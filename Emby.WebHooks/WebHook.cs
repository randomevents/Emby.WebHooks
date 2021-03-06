using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Sync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Emby.WebHooks.Configuration;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.System;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Notifications;
using System.Threading;
using MediaBrowser.Controller.Entities;

namespace Emby.WebHooks
{

    public class WebHooks : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserManager _userManager;
        private readonly ISyncManager _syncManager;
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClient _httpClient;
        private readonly INetworkManager _networkManager;
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
                c = new DeviceState() { deviceId = deviceId };
                deviceState.Add(c);
            }
            return c;
        }

        public static WebHooks Instance { get; private set; }

        public string Name
        {
            get { return "WebHooks"; }
        }

        public WebHooks(ISessionManager sessionManager, IHttpClient httpClient, ILogManager logManager, IUserManager userManager, ILibraryManager libraryManager, INetworkManager networkManager, IServerApplicationHost appHost, ISyncManager syncManager)
        {
            _logger = logManager.GetLogger(Plugin.Instance.Name);
            _libraryManager = libraryManager; //ItemRemoved
            _sessionManager = sessionManager; //AuthenticationFailed SessionStarted SessionEnded SessionActivity AuthenticationSucceeded
            _userManager = userManager; //UserLockedOut
            _syncManager = syncManager; //SyncJobItemCreated SyncJobCreated SyncJobCancelled SyncJobItemCancelled
            _httpClient = httpClient;
            _networkManager = networkManager;
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
            if (
                e.Item.IsVirtualItem == false &&
                (e.Item.MediaType == "Video" || e.Item.MediaType == "Audio")
                )
            {
                var iType = _libraryManager.GetContentType(e.Item);
                var hooks = hooksByType(iType).Where(h => h.onItemAdded);

                if (hooks.Count() > 0)
                {
                    _logger.Debug("{0} WebHooks Event(s): Item Added", hooks.Count().ToString());

                    foreach (var h in hooks)
                    {
                        SendHook(h, buildJson_Added(h, e.Item, "Added"));
                    }
                }
            }
        }
        private void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            var iType = _libraryManager.GetContentType(e.Item);

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

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onPause);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackResume(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Resumed";

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onResume);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Playing";

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onPlay);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackStopped(object sender, PlaybackProgressEventArgs e)
        {
            string action = "Stopped";

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onStop);

            PlaybackMessage(hooks, e, action);
        }

        private void PlaybackMessage(IEnumerable<PluginConfiguration.Hook> hooks, PlaybackProgressEventArgs e, string action)
        {
            getDeviceState(e.DeviceId).lastState = action;

            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} WebHooks Event(s): {1}", hooks.Count().ToString(), action);

                foreach (var h in hooks)
                {
                    string msgString = buildJson_Playback(h, _sessionManager.GetSession(e.DeviceId.ToString(), e.ClientName, ""), e, action);
                    if (msgString != "_redundant_")
                    {
                        SendHook(h, msgString);
                    }
                }
            }
        }

        public IEnumerable<PluginConfiguration.Hook> hooksByType(string type)
        {
            return Plugin.Instance.Configuration.Hooks.Where(h =>
                    (h.withMovies && type == "movies") ||
                    (h.withEpisodes && type == "tvshows") ||
                    (h.withSongs && type == "music")
            );
        }

        public async Task<bool> SendHook(PluginConfiguration.Hook h, string jsonString)
        {
            _logger.Debug("WebHook sent to {0}", h.URL);
            _logger.Debug(jsonString);

            using (var client = new HttpClient())
            {
                    var httpContent = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(h.URL, httpContent);
                    var responseString = await response.Content.ReadAsStringAsync();
                    _logger.Debug(response.StatusCode.ToString());
            }
            return true;
        }

        public string buildJson_Added(PluginConfiguration.Hook hooks, BaseItem e, string trigger)
        {
            string msgAdded = buildJson_BaseItem(hooks.removeQuotes, hooks.msgHook, e);

            return msgAdded.Replace("{Event}", testString(hooks.removeQuotes, trigger, true)).
            Replace("{ServerID}", testString(hooks.removeQuotes, _appHost.SystemId, true)).
            Replace("{ServerName}", testString(hooks.removeQuotes, _appHost.FriendlyName, true)).

            Replace("{TimeStamp}", testString(hooks.removeQuotes, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), true));
        }

        public string buildJson_Playback(PluginConfiguration.Hook hooks, SessionInfo sessionInfo, PlaybackProgressEventArgs playbackData, string trigger)
        {
            if ((trigger == "Paused" || trigger == "Resumed") && string.IsNullOrEmpty(sessionInfo.NowPlayingItem.ToString())) return "_redundant_";

            string playbackTicks = (string.IsNullOrEmpty(playbackData.PlaybackPositionTicks.ToString())) ? sessionInfo.PlayState.PositionTicks.ToString() : playbackData.PlaybackPositionTicks.ToString();

            string msgPlayback = buildJson_BaseItem(hooks.removeQuotes, hooks.msgHook, sessionInfo.FullNowPlayingItem);

            return msgPlayback.Replace("{Event}", testString(hooks.removeQuotes, trigger, true)).
            Replace("{ServerID}", testString(hooks.removeQuotes, _appHost.SystemId, true)).
            Replace("{ServerName}", testString(hooks.removeQuotes, _appHost.FriendlyName, true)).
            
            Replace("{UserID}", testString(hooks.removeQuotes, sessionInfo.UserId.ToString(), true)).
            Replace("{UserName}", testString(hooks.removeQuotes, sessionInfo.UserName, true)).
            
            Replace("{AppName}", testString(hooks.removeQuotes, sessionInfo.AppName, true)).
            Replace("{DeviceID}", testString(hooks.removeQuotes, sessionInfo.DeviceId.ToString(), true)).
            Replace("{DeviceName}", testString(hooks.removeQuotes, sessionInfo.DeviceName, true)).
            Replace("{DeviceIP}", testString(hooks.removeQuotes, sessionInfo.RemoteEndPoint.ToString(), true)).

            Replace("{SessionID}", testString(hooks.removeQuotes, sessionInfo.Id, true)).
            Replace("{SessionPlaybackPositionTicks}", testString(hooks.removeQuotes, playbackTicks, false)).
            Replace("{TimeStamp}", testString(hooks.removeQuotes, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), true));
        }

        public string buildJson_BaseItem(bool removeQuotes, string inStr, BaseItem e) {
            return inStr.Replace("{ItemType}", testString(removeQuotes, _libraryManager.GetContentType(e), true)).
            Replace("{ItemName}", testString(removeQuotes, e.Name, true)).
            Replace("{ItemNameParent}", testString(removeQuotes, e.Parent.Name, true)).
            Replace("{ItemNameGrandparent}", testString(removeQuotes, e.Parent.Parent.Name, true)).
            Replace("{ItemID}", testString(removeQuotes, e.Id.ToString(), true)).
            Replace("{ItemRunTimeTicks}", testString(removeQuotes, e.RunTimeTicks.ToString(), false)).
            Replace("{ItemIndex}", testString(removeQuotes, e.IndexNumber.ToString(), false)).
            Replace("{ItemParentIndex}", testString(removeQuotes, e.ParentIndexNumber.ToString(), false)).
            Replace("{ItemCriticRating}", testString(removeQuotes, e.CriticRating.ToString(), false)).
            Replace("{ItemCommunityRating}", testString(removeQuotes, e.CommunityRating.ToString(), false)).
            Replace("{ItemPremiereDate}", testString(removeQuotes, e.PremiereDate.ToString(), true)).
            Replace("{ItemDateAdded}", testString(removeQuotes, e.DateCreated.ToString(), true)).
            Replace("{ItemYear}", testString(removeQuotes, e.ProductionYear.ToString(), false)).
            Replace("{ItemBitrate}", testString(removeQuotes, e.TotalBitrate.ToString(), false)).
            Replace("{ItemGenre}", testString(removeQuotes, string.Join(",", e.Genres), true));
        }

        public string testString(bool removeQuotes, string inStr, bool isString)
        {
            if (removeQuotes)
            {
                if (string.IsNullOrEmpty(inStr))
                {
                    if (isString) return "";

                    return "0";
                }
                else
                {
                    return inStr;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(inStr))
                {
                    if (isString) return "\"\"";

                    return "0";
                }
                else
                {
                    if (isString) return "\"" + inStr + "\"";

                    return inStr;
                }
            }
        }
    }
}
