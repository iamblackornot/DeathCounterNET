using OBSWebsocketDotNet.Communication;
using System;
using System.Collections.Concurrent;
using System.Drawing;

namespace DeathCounterNETShared
{
    internal class OBSDirector : Notifiable
    {
        static readonly int REPLAY_COOLDOWN_IN_SECONDS = 5;
        static readonly int LOOP_INTERVAL = 250;

        static readonly int SHOW_REPLAY_TRY_COUNT = 5;
        static readonly int SHOW_REPLAY_TRY_INTERVAL = 2 * 1000;

        static readonly int STOP_WAIT_TIMEOUT = 10 * 1000;

        OBSBridge _hostOBSBridge;
        OBSBridgeController _controller;
        ConcurrentQueue<Replay> _replayQueue;

        private volatile bool _toStop = false;

        private Executor _executor;
        private Task? _loopTask;
        private CancellationTokenSource? _cts;

        public OBSDirector(OBSBridgeOptions options)
        {
            _hostOBSBridge = new OBSBridge(options);


            _controller = new();
            _controller.ReconnectInitiated += _controller_ReconnectInitiated;
            _controller.Add(_hostOBSBridge);

            _replayQueue = new();

            _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .Build();
        }

        public async Task<Result> Start()
        {
            OnNotifyInfo("[OBS Director] connecting to OBS websocket...");

            await _hostOBSBridge.ConnectTillMadeItAsync();

            OnNotifyInfo("[OBS Director] connected to OBS websocket");

            _hostOBSBridge.Connected += _hostOBSBridge_Connected;
            _hostOBSBridge.Disconnected += _hostOBSBridge_Disconnected;

            OnNotifyInfo("[OBS Director] validating obs setup...");

            var res = ValidateOBSSetup();

            if (!res.IsSuccessful) return new BadResult($"couldn't validate the OBS setup, reason: {res.ErrorMessage}");

            OnNotifyInfo("[OBS Director] setup is ok");

            Func<Result<Nothing>> startAction = () =>
            {
                _toStop = false;
                _cts = new CancellationTokenSource();

                _loopTask = Task.Run(async () => await _executor.ExecuteAsync(MainLoop), _cts.Token);

                return new GoodResult();
            };

            return _executor.Execute(startAction);
        }
        public async Task Stop()
        {
            if (_toStop || _loopTask is null || _cts is null) return;

            _toStop = true;
            await _loopTask.WaitAsync(new TimeSpan(STOP_WAIT_TIMEOUT));

            if (!_loopTask.IsCompleted)
            {
                _cts.Cancel();
                await _loopTask.WaitAsync(_cts.Token);
            }

            _cts = null;
        }
        public void UpdatePlayerCaption(int playerSlot, string? caption, Color? color)
        {
            var res = _hostOBSBridge.UpdatePlayerCaption(playerSlot, caption ?? string.Empty, color);

            if(!res.IsSuccessful)
            {
                OnNotifyError($"[OBS Director] failed to update player caption, reason: {res.ErrorMessage}");
            }
        }
        public void EnqueueReplay(Replay replay) 
        {
            _replayQueue.Enqueue(replay); 
        }
        private Result ValidateOBSSetup()
        {
            {
                var res = _hostOBSBridge.HasMainScene();

                if (!res.IsSuccessful) return new BadResult(res.ErrorMessage);
                if (!res.Data) return new BadResult($"Main Scene [{_hostOBSBridge.Options.MainSceneName}] is missing");
            }

            {
                string? replayBSName = _hostOBSBridge.Options.TwitchClipReplayBrowserSourceName;

                if (string.IsNullOrEmpty(replayBSName))
                    return new BadResult("Twitch Replay Browser Source name is not specified");

                string? captionPattern = _hostOBSBridge.Options.PlayerCaptionSourceNamePattern;

                if (string.IsNullOrEmpty(captionPattern))
                    return new BadResult("Player Caption Source Name Pattern is not specified");

                int maxPlayers = _hostOBSBridge.Options.MaxPlayers;

                if(maxPlayers <= 0)
                    return new BadResult($"Max Players value [{maxPlayers}] is not acceptable");

                List<string> itemsToCheck = new();

                itemsToCheck.Add(replayBSName);

                for(int i = 0; i < maxPlayers; i++)
                {
                    itemsToCheck.Add(string.Format(captionPattern, i + 1));
                }

                var res = _hostOBSBridge.HasItems(itemsToCheck);

                if (!res.IsSuccessful) return new BadResult(res.ErrorMessage);
            }

            return new GoodResult();
        }
        private async Task MainLoop()
        {
            Executor _clipExecutor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .Build();

            await _hostOBSBridge.ConnectTillMadeItAsync();

            while (!_toStop)
            {
                await Task.Delay(LOOP_INTERVAL);

                _controller.DoKeepAliveWork();

                if (!_hostOBSBridge.IsConnected) continue;
                if (_replayQueue.IsEmpty) continue;
                if (_hostOBSBridge.IsSceneTransitionActive) continue;


                int secondsSinceLastSceneChange = (int)Math.Ceiling((DateTime.Now - _hostOBSBridge.LastCurrentSceneChange).TotalSeconds);

                if (secondsSinceLastSceneChange < REPLAY_COOLDOWN_IN_SECONDS) continue;

                var isMainSceneActiveRes = _hostOBSBridge.IsMainSceneActive();

                if(!isMainSceneActiveRes.IsSuccessful)
                {
                    OnNotifyError($"[OBS Director] failed to check whether main scene is active, reason: {isMainSceneActiveRes.ErrorMessage}");
                    continue;
                }

                if (!isMainSceneActiveRes.Data) continue;

                if(!_replayQueue.TryPeek(out var replay))
                {
                    continue;
                }

                var showReplayRes = await _executor.RepeatTillMadeItOrTimeout(() => replay.Play(_hostOBSBridge), SHOW_REPLAY_TRY_INTERVAL, SHOW_REPLAY_TRY_COUNT);

                if(!showReplayRes.IsSuccessful) 
                {
                    OnNotifyError($"[OBS Director] failed to show a replay after {SHOW_REPLAY_TRY_COUNT} tries");
                    continue;
                }

                OnNotifyInfo($"[OBS Director] showing replay for {replay.PlayerName}");

                if (!_replayQueue.TryDequeue(out replay))
                {
                    OnNotifyError($"[OBS Director] failed to dequeue replay");
                    continue;
                };
                
            }
        }
        private Result DefaultExceptionHandler(Exception ex)
        {
            Logger.AddToLogs(ex.ToString());
            return new Result(false, "obs director action failed, more info in logs");
        }
        private void _hostOBSBridge_Disconnected(object? sender, ObsDisconnectionInfo e)
        {
            OnNotifyError($"[OBS Director] disconnected from host's obs websocket, reason: {e.DisconnectReason}");
        }

        private void _hostOBSBridge_Connected(object? sender, EventArgs e)
        {
            OnNotifyInfo($"[OBS Director] connected to host's obs websocket");
        }

        private void _controller_ReconnectInitiated(object? sender, ReconnectInfoArgs e)
        {
            OnNotifyInfo($"[OBS Director] trying to reconnect to host's obs websocket...");
        }
    }

    public enum ReplayType
    {
        Local = 0,
        Twitch = 1,
    }
    internal abstract class Replay
    {
        public int PlayerSlot { get; init; }
        public abstract Result<Nothing> Play(OBSBridge obsBridge);
        public virtual string PlayerName { get => $"Player {PlayerSlot}"; }
    }
    internal class TwitchReplay : Replay
    {
        public string ClipId { get; }
        public string Channel { get; }
        public TwitchReplay(string clipId, string channel, int playerSlot)
        {
            ClipId = clipId;
            Channel = channel;
            PlayerSlot = playerSlot;
        }
        public override Result<Nothing> Play(OBSBridge obsBridge)
        {
            return new Result<Nothing>(obsBridge.ShowTwitchClipReplay(ClipId, PlayerSlot));
        }
        public override string PlayerName { get => $"{base.PlayerName} ({Channel})"; }
    }
}
