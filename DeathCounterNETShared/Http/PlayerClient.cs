using RestSharp;
using System;
using System.Drawing;

namespace DeathCounterNETShared
{
    internal class PlayerClient : Notifiable
    {
        static readonly int CHECK_SERVER_INTERVAL = 2 * 1000;

        static readonly int UPDATE_CAPTION_INTERVAL = 200;
        static readonly int UPDATE_CAPTION_TRYCOUNT = 2;

        static readonly int SHOW_TWITCH_REPLAY_INTERVAL = 200;
        static readonly int SHOW_TWITCH_REPLAY_TRYCOUNT = 10;

        EndpointIPAddress _hostEndpoint;
        Executor _executor;
        RestClient _restClient;

        public PlayerClient(EndpointIPAddress hostEndpoint)
        {
            _hostEndpoint = hostEndpoint;
            _restClient = new RestClient($"http://{_hostEndpoint}");

            _executor = Executor
            .GetBuilder()
            .SetDefaultExceptionHandler(DefaultExeceptionHandler)
            .Build();
        }
        public async Task<Result> CheckServer()
        { 
            var resp = await _restClient.ExecuteAsync(new RestRequest("/ping"));
            return RestResponseToResult(resp);
        }
        public async Task<bool> NotifyJoin(string displayedName, int playerSlot)
        {
            var req = new RestRequest("/join", Method.Post);
            var body = new NotifyJoinRequest()
            {
                DisplayedName = displayedName,
                PlayerSlot = playerSlot,
            };
            req.AddJsonBody(body.ToJsonString());

            var resp = await _restClient.ExecuteAsync(req);
            return resp.IsSuccessStatusCode;
        }
        public void UpdatePlayerCaption(int playerSlot, string caption, Color? color = null)
        {
            Task.Run(async () =>
            {
                Func<Task<Result<Nothing>>> action = async () =>
                {
                    return await UpdatePlayerCaptionAsync(playerSlot, caption, color);
                };

                var res = await _executor.RepeatTillMadeItOrTimeoutAsync(action, UPDATE_CAPTION_INTERVAL, UPDATE_CAPTION_TRYCOUNT);

                if(!res.IsSuccessful)
                {
                    OnNotifyError($"failed to send an update player caption request, reason: {res.ErrorMessage}");
                }
            });
        }
        public void ShowTwitchReplay(int playerSlot, string channel, string userAccessToken)
        {
            Task.Run(async () =>
            {
                Func<Task<Result<Nothing>>> action = async () =>
                {
                    return await ShowTwitchReplayAsync(playerSlot, channel, userAccessToken);
                };

                var res = await _executor.RepeatTillMadeItOrTimeoutAsync(action, SHOW_TWITCH_REPLAY_INTERVAL, SHOW_TWITCH_REPLAY_TRYCOUNT);

                if (!res.IsSuccessful)
                {
                    OnNotifyError($"failed to send an show twitch replay request, reason: {res.ErrorMessage}");
                }
            });
        }
        public async Task<Result<Nothing>> UpdatePlayerCaptionAsync(int playerSlot, string caption, Color? color = null)
        {
            var req = new RestRequest("/update_player_caption", Method.Post);
            var body = new UpdatePlayerCaptionRequest()
            {
                PlayerSlot = playerSlot,
                Caption = caption,
                Color = color
            };
            req.AddJsonBody(body.ToJsonString());

            var resp = await _restClient.ExecuteAsync(req);

            return RestResponseToResult(resp);
        }
        public async Task<Result<Nothing>> ShowTwitchReplayAsync(int playerSlot, string channel, string userAccessToken)
        {
            var req = new RestRequest("/show_twitch_replay", Method.Post);
            var body = new TwitchReplayRequest()
            {
                PlayerSlot = playerSlot,
                Channel = channel,
                UserAccessToken = userAccessToken
            };
            req.AddJsonBody(body.ToJsonString());

            var resp = await _restClient.ExecuteAsync(req);

            return RestResponseToResult(resp);
        }
        private Result<Nothing> RestResponseToResult(RestResponse resp)
        {
            if (resp.IsSuccessStatusCode) return new GoodResult();

            if (resp.StatusCode == 0) return new BadResult("host server is down");

            if (resp.Content != null) return new BadResult(resp.Content);

            return new BadResult("smth unxpectedly bad happened");
        }
        public async Task WaitTillServerIsUp()
        {
            while (true)
            {
                var isLive = await CheckServer();

                if (isLive.IsSuccessful) return;

                await Task.Delay(CHECK_SERVER_INTERVAL);
            }
        }
        private Result DefaultExeceptionHandler(Exception ex)
        {
            Logger.AddToLogs("PlayerClient", ex.ToString());
            return new BadResult("smth failed, see more info in logs");
        }
    }
}
