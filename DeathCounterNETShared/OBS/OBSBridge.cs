using Newtonsoft.Json.Linq;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using OBSWebsocketDotNet.Types;
using System.Drawing;

namespace DeathCounterNETShared
{
    internal class OBSBridge : Notifiable
    {
        static readonly int REMOTE_SERVER_REFUSED_CONNECTION_RESULT = -2147467259;
        static readonly int DEFAULT_FORECOLOR = 0xEFEFEF;

        static readonly string INSTANT_REPLAY_HK_NAME = "instant_replay.trigger";
        static readonly string LOCAL_REPLAY_SCENE_NAME = "Local Replay Scene";

        static readonly int DEFAULT_LOCAL_REPLAY_TIMEOUT = 30 * 1000;
        static readonly int DEFAULT_LOCAL_REPLAY_DELAY = 10 * 1000;

        OBSWebsocket _obsWebSocket;

        private readonly object _connectionLock = new object();
        private bool _isConnecting = false;

        private readonly object _localReplayLock = new object();
        private DateTime _lastLocalReplayTimestamp = DateTime.MinValue;

        private readonly object _currentSceneLock = new object();
        private DateTime _lastCurrProgramSceneNameUpdateTime = DateTime.Now;
        private string? _currProgramSceneNameCached;

        private readonly object _transitionLock = new object();
        private bool _isSceneTransitionActive = false;


        OBSBridgeOptions _options;
        TaskCompletionSource<bool>? _tcsConnection;
        Executor _executor;

        public OBSBridgeOptions Options { get => _options; }

        public DateTime LastCurrentSceneChange
        {
            get
            {
                lock (_currentSceneLock) { return _lastCurrProgramSceneNameUpdateTime; }
            }
        }
        public bool IsSceneTransitionActive
        {
            get
            {
                lock (_transitionLock) { return _isSceneTransitionActive; }
            }
        }
        public bool IsConnecting 
        {
            get
            {
                lock (_connectionLock) { return _isConnecting; }
            }
        }
        public bool IsConnected => _obsWebSocket.IsConnected;
        public string Destination => _options.Destination ?? "Unknown";

        public event EventHandler? Connected;
        public event EventHandler<ObsDisconnectionInfo>? Disconnected;

        public OBSBridge(OBSBridgeOptions options)
        {
            this._options = options;

            _obsWebSocket = new OBSWebsocket();
            _obsWebSocket.Connected += ObsWebSocket_Connected;
            _obsWebSocket.Disconnected += ObsWebSocket_Disconnected;

            _obsWebSocket.SceneTransitionStarted += _obsWebSocket_SceneTransitionStarted;
            _obsWebSocket.CurrentProgramSceneChanged += _obsWebSocket_CurrentProgramSceneChanged;

            _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .Build();
        }

        private void _obsWebSocket_CurrentProgramSceneChanged(object? sender, OBSWebsocketDotNet.Types.Events.ProgramSceneChangedEventArgs e)
        {
            CacheSceneChangedData(e.SceneName, DateTime.Now);

            lock (_transitionLock)
            {
                _isSceneTransitionActive = false;
            }
        }
        private void _obsWebSocket_SceneTransitionStarted(object? sender, OBSWebsocketDotNet.Types.Events.SceneTransitionStartedEventArgs e)
        {
            lock(_transitionLock)
            {
                _isSceneTransitionActive = true;
            }
        }
        private string GetTwitchClipURL(string clipId)
        {
            return $"https://clips.twitch.tv/embed?autoplay=1&clip={clipId}&parent=absolute";
        }
        public Task<bool> ConnectAsync()
        {
            if (_obsWebSocket.IsConnected) return Task.FromResult(true);

            if(_options.Endpoint is null) return Task.FromResult(false);           

            lock (_connectionLock)
            {
                if (_isConnecting && _tcsConnection is not null) return _tcsConnection.Task;

                _tcsConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _isConnecting = true;
            }

            _obsWebSocket.ConnectAsync($"ws://{_options.Endpoint.IP}:{_options.Endpoint.Port}", _options.Password ?? string.Empty);

            return _tcsConnection.Task;
        }

        public async Task ConnectTillMadeItAsync()
        {
            if (_obsWebSocket.IsConnected) return;

            while (!await ConnectAsync())
            {
                await Task.Delay(1000);
            }
        }
        public Result ShowTwitchClipReplay(string clipId, int playerSlot)
        {
            if(_options.TwitchClipReplayBrowserSourceName is null)
            {
                return new BadResult($"TwitchClipReplayBrowserSourceName is required");
            }
            if (!_obsWebSocket.IsConnected)
            {
                return new BadResult($"no websocket connection to {Destination}");
            }
            if (IsSceneTransitionActive)
            {
                return new BadResult($"can't play during scene transition");
            }

            Func<Result<Nothing>> action = () =>
            {
                _obsWebSocket.SetInputSettings(
                    $"{_options.TwitchClipReplayBrowserSourceName}",
                    new JObject()
                    {
                        new JProperty("url", GetTwitchClipURL(clipId))
                    });

                SendMessageToAdvancedSceneSwitcher($"twitch_replay_player{playerSlot}");

                return new GoodResult();
            };

            return _executor.Execute(action);
        }

        public Result UpdatePlayerCaption(int playerSlot, string text)
        {
            return UpdatePlayerCaption(playerSlot, text, DEFAULT_FORECOLOR);
        }

        public Result<Nothing> UpdatePlayerCaption(int playerSlot, string text, int colorHexValue)
        {
            if (_options.PlayerCaptionSourceNamePattern is null)
            {
                return new BadResult($"PlayerCaptionSourceNamePattern is required");
            }

            if (!_obsWebSocket.IsConnected) 
            { 
                return new BadResult<Nothing>($"no websocket connection to {Destination}"); 
            }

            Func<Result<Nothing>> action = () =>
            {
                _obsWebSocket?.SetInputSettings(
                    string.Format(_options.PlayerCaptionSourceNamePattern, playerSlot),
                    new JObject()
                    {
                        new JProperty("text", text),
                        new JProperty("color", colorHexValue)
                    }
                );

                return new GoodResult();
            };

            return _executor.Execute(action);

        }
        public Result UpdatePlayerCaption(int playerSlot, string text, Color? color)
        {
            if(!color.HasValue) return UpdatePlayerCaption(playerSlot, text, DEFAULT_FORECOLOR);

            byte[] data = new byte[4]
            {
                color.Value.R,
                color.Value.G,
                color.Value.B,
                0,
            };

            int colorHexValue = BitConverter.ToInt32(data);

            return UpdatePlayerCaption(playerSlot, text, colorHexValue);
        }
        public Result<string> GetCurrentProgramSceneName()
        {
            if (!string.IsNullOrEmpty(_currProgramSceneNameCached)) return new GoodResult<string>(_currProgramSceneNameCached);

            var updateCurrSceneNameRes = UpdateCurrentProgramSceneName();
            
            if(!updateCurrSceneNameRes.IsSuccessful)
            {
                return new BadResult<string>(updateCurrSceneNameRes.ErrorMessage);
            }

            return new GoodResult<string>(_currProgramSceneNameCached!);
        }

        public Result<bool> IsMainSceneActive()
        {
            if (_options.MainSceneName is null)
            {
                return new BadResult<bool>($"MainSceneName is required");
            }

            var currSceneNameRes = GetCurrentProgramSceneName();

            if (!currSceneNameRes.IsSuccessful)
            {
                return new BadResult<bool>($"failed to get current scene name, reason: {currSceneNameRes.ErrorMessage}");
            }

            return new GoodResult<bool>(_options.MainSceneName == currSceneNameRes.Data);
        }
        public Result<bool> HasMainScene()
        {
            if (string.IsNullOrWhiteSpace(_options.MainSceneName)) return new BadResult<bool>("Main Scene Name is missing");

            return HasScene(_options.MainSceneName);
        }
        public Result<bool> HasScene(string sceneName)
        {
            Func<Result<bool>> action = () =>
            {
                var list = _obsWebSocket.GetSceneList();

                foreach (var item in list.Scenes)
                {
                    if (item.Name == sceneName) return new GoodResult<bool>(true);
                }

                return new GoodResult<bool>(false);
            };

            return _executor.Execute(action);
        }
        public Result<bool> HasItem(string itemName)
        {
            Func<Result<bool>> action = () =>
            {
                var list = _obsWebSocket.GetInputList();

                foreach (var item in list)
                {
                    if (item.InputName == itemName) return new GoodResult<bool>(true);
                }

                return new GoodResult<bool>(false);
            };

            return _executor.Execute(action);
        }
        public Result HasItems(IEnumerable<string> itemsToCheck)
        {
            Func<Result<Nothing>> action = () =>
            {
                HashSet<string> set = new HashSet<string>(itemsToCheck);

                var list = _obsWebSocket.GetInputList();

                foreach (var item in list)
                {
                    if(set.Contains(item.InputName)) set.Remove(item.InputName);
                }

                if(set.Count > 0)
                {
                    return new BadResult($"following items are missing: {string.Join(", ", set)}");
                }

                return new GoodResult();
            };

            return _executor.Execute(action);
        }
        private void SendMessageToAdvancedSceneSwitcher(string message)
        {
            _obsWebSocket.CallVendorRequest(
                "AdvancedSceneSwitcher",
                "AdvancedSceneSwitcherMessage",
                new JObject() { { "message", message } }
            );
        }
        private void CacheSceneChangedData(string newCurrSceneName, DateTime changedTime)
        {
            lock(_currentSceneLock)
            {
                _currProgramSceneNameCached = newCurrSceneName;
                _lastCurrProgramSceneNameUpdateTime = changedTime;
            }

        }
        private Result<Nothing> UpdateCurrentProgramSceneName()
        {
            Func<Result<Nothing>> action = () =>
            {
                string currScene = _obsWebSocket.GetCurrentProgramScene();

                if (string.IsNullOrWhiteSpace(currScene)) return new BadResult("couldn't get current program scene name");

                CacheSceneChangedData(currScene, DateTime.Now);

                return new GoodResult();
            };

            return _executor.Execute(action);
        }
        public async Task<Result> SaveReplayBuffer()
        {
            lock (_localReplayLock)
            {
                if ((DateTime.Now - _lastLocalReplayTimestamp).TotalMilliseconds <= (_options.ReplayTimeout ?? DEFAULT_LOCAL_REPLAY_TIMEOUT))
                {
                    return new Result(false, "replay is on cooldown");
                }

                _lastLocalReplayTimestamp = DateTime.Now;
            }

            await Task.Delay(_options.ReplayDelay ?? DEFAULT_LOCAL_REPLAY_DELAY);

            Func<Result<Nothing>> action = () =>
            {
                var getCurrSceneRes = GetCurrentProgramSceneName();

                if (!getCurrSceneRes.IsSuccessful)
                {
                    return new BadResult(getCurrSceneRes.ErrorMessage);
                }

                if (getCurrSceneRes.Data == LOCAL_REPLAY_SCENE_NAME)
                {
                    return new BadResult("replay scene is busy");
                }

                _obsWebSocket?.TriggerHotkeyByName(INSTANT_REPLAY_HK_NAME);

                return new GoodResult();
            };

            return _executor.Execute(action);
        }
        private void ObsWebSocket_Disconnected(object? sender, ObsDisconnectionInfo e)
        {
            lock (_connectionLock)
            {
                if (_isConnecting)
                {
                    _isConnecting = false;
                    _tcsConnection?.SetResult(false);
                }              
            }

            if(e.WebsocketDisconnectionInfo.Type == Websocket.Client.DisconnectionType.Error)
            {
                if(e.WebsocketDisconnectionInfo.Exception.HResult == REMOTE_SERVER_REFUSED_CONNECTION_RESULT)
                {
                    return;
                }

                Logger.AddToLogs($"OBS_bridge [{_options.Destination}] disconnect exception: {e.WebsocketDisconnectionInfo.Exception}");
            }

            Disconnected?.Invoke(this, e);  
        }
        private void ObsWebSocket_Connected(object? sender, EventArgs e)
        {
            var getCurrProgramSceneRes = UpdateCurrentProgramSceneName();

            if (!getCurrProgramSceneRes.IsSuccessful)
            {
                OnNotifyError($"{Destination}'s OBS couldn't update current program scene when connected");
            }

            lock (_connectionLock)
            {
                _isConnecting = false;
                Connected?.Invoke(this, e);
                _tcsConnection?.SetResult(true);
            }
        }
        private Result DefaultExceptionHandler(Exception ex)
        {
            Logger.AddToLogs(ex.ToString());
            return new Result(false, "obs bridge action failed, more info in logs");
        }
        public class ErrorEventArgs
        {
            public string ErrorMessage { get; init; } = string.Empty;
        }
    }

    public class OBSBridgeOptions
    {
        public string? Destination { get; init; }
        public EndpointIPAddress? Endpoint { get; init; }
        public string? Password { get; init; }
        public int? ReplayTimeout { get; init; }
        public int? ReplayDelay { get; init; }
        public string? PlayerCaptionSourceNamePattern { get; init; }
        public string? MainSceneName { get; init; }
        public string? TwitchClipReplayBrowserSourceName { get; init; }
        public int MaxPlayers { get; init; }
    }
}
