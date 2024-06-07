using DeathCounterNETShared;
using DeathCounterNETShared.Twitch;
using System.Net;

namespace DeathCounterNETServer
{
    internal class ServerApplication : AppBase<Settings>
    {
        static readonly string SETTINGS_PATH = "settings.ini";
        static readonly int DEFAULT_OBS_WEBSOCKET_PORT = 4455;
        static readonly int DEFAULT_LOCAL_SERVER_PORT = 3366;
        static readonly int DEFAULT_CLIP_CREATION_DELAY_IN_SECONDS = 20;

        Settings? _settings;
        TwitchUserAccessTokenAPI? _twitchAPI;
        OBSDirector? _obsDirector;
        public ServerApplication() : base(SETTINGS_PATH)
        {
        }
        public async Task StartAsync()
        {
            {
                var res = LoadSettings();
                if(!res.IsSuccessful)
                {
                    ConsoleHelper.PrintError("failed to load app settings");
                    return;
                }
                _settings = res.Data;
            }

            if(_settings.TwitchCredentials is null)
            {
                ConsoleHelper.PrintError("no twitch credentials found in settings file");
                return;
            }

            if (_settings.TwitchCredentials.ClientID is null)
            {
                ConsoleHelper.PrintError("twitch Client ID is required");
                return;
            }

            if (_settings.TwitchCredentials.ClientSecret is null)
            {
                ConsoleHelper.PrintError("twitch Client Secret is required");
                return;
            }

            if (_settings.TwitchCredentials.RedirectUri is null)
            {
                ConsoleHelper.PrintError("twitch Redirect Uri is required");
                return;
            }

            _twitchAPI = new(_settings.TwitchCredentials);
            string mainSceneName = string.Format(_settings.MainSceneName ?? string.Empty, _settings.MaxPlayers);

            ConsoleHelper.PrintInfo($"Max Players is [{_settings.MaxPlayers}]");
            ConsoleHelper.PrintInfo($"Clip Creation Delay is [{_settings.ClipCreationDelayInSeconds}s]");
            ConsoleHelper.PrintInfo($"Main Scene is [{mainSceneName}]");
            ConsoleHelper.PrintInfo($"Player Caption Source Name pattern is [{_settings.PlayerCaptionSourceNamePattern}]");
            ConsoleHelper.PrintInfo($"Twitch Replay Browser Source name is [{_settings.TwitchClipReplayBrowserSourceName}]");

            var obsOptions = new OBSBridgeOptions()
            {
                Destination = "Host",
                Endpoint = new EndpointIPAddress(IPAddress.Loopback, _settings.OBSWebSocketPort ?? DEFAULT_OBS_WEBSOCKET_PORT),
                Password = _settings.OBSWebSocketPassword,
                MainSceneName = mainSceneName,
                PlayerCaptionSourceNamePattern = _settings.PlayerCaptionSourceNamePattern,
                TwitchClipReplayBrowserSourceName = _settings.TwitchClipReplayBrowserSourceName,
                MaxPlayers = _settings.MaxPlayers,
            };

            _obsDirector = new OBSDirector(obsOptions);
            _obsDirector.NotifyInfo += NotifyInfo;
            _obsDirector.NotifyError += NotifyError;

            {
                var res = await _obsDirector.Start();

                if(!res.IsSuccessful)
                {
                    ConsoleHelper.PrintError($"couldn't start OBS Director, reason: {res.ErrorMessage}");
                    return;
                }
            }

            ConsoleHelper.PrintInfo("started OBS Director");

            ConsoleHelper.PrintInfo("starting local HTTP server...");

            var ip = IPAddress.Loopback;
            var server = new HttpServer(new EndpointIPAddress(ip, DEFAULT_LOCAL_SERVER_PORT));
            server.NotifyInfo += NotifyInfo;
            server.NotifyInfo += NotifyError;

            server.AddEndpoint(HttpMethod.Post, "/test", TestEndpoint);
            server.AddEndpoint(HttpMethod.Post, "/get_user_access_token", GetUserAccessTokenEndpoint);
            server.AddEndpoint(HttpMethod.Get, "/ping", PingEndpoint);
            server.AddEndpoint(HttpMethod.Post, "/join", NotifyJoinEndpoint);
            server.AddEndpoint(HttpMethod.Post, "/update_player_caption", UpdatePlayerCaptionEndpoint);
            server.AddEndpoint(HttpMethod.Post, "/show_twitch_replay", ShowTwitchReplayEndpoint);

            Result serverStartRes = server.Start();

            if (!serverStartRes.IsSuccessful)
            {
                ConsoleHelper.PrintError($"failed to start server, reason {serverStartRes.ErrorMessage}");
                ConsoleHelper.PrintError($"application can't start without http server");
                return;
            }

            ConsoleHelper.PrintInfo($"server started listening at port {DEFAULT_LOCAL_SERVER_PORT}");
            ConsoleHelper.PrintInfo($"now everything is set up!");
            await server.Join();
        }
        private bool TryDeserializeJsonBodyOrSendBadRequest<T>(Request req, Response resp, out T body) where T : JsonBody<T>
        {
            body = default!;

            if (req.ContentType is null || !req.ContentType.Contains("application/json"))
            {
                resp.Send(HttpStatusCode.BadRequest, "expected application/json content-type");
                return false;
            }

            if (!JsonBody<T>.TryParse(req.Body, out body))
            {
                resp.Send(HttpStatusCode.BadRequest, "couldn't parse json object");
                return false;
            }

            return true;
        }
        private void NotifyJoinEndpoint(Request req, Response resp)
        {
            if (!TryDeserializeJsonBodyOrSendBadRequest(req, resp, out NotifyJoinRequest body)) return;

            ConsoleHelper.PrintInfo($"player {body.PlayerSlot} [{body.DisplayedName}] joined");
            resp.Send(HttpStatusCode.OK);
        }
        private void UpdatePlayerCaptionEndpoint(Request req, Response resp)
        {
            if (!TryDeserializeJsonBodyOrSendBadRequest(req, resp, out UpdatePlayerCaptionRequest body)) return;

            if(body.PlayerSlot <= 0)
            {
                resp.Send(HttpStatusCode.BadRequest, "appropriate playerSlot required");
                return;
            }

            _obsDirector?.UpdatePlayerCaption(body.PlayerSlot, body.Caption, body.Color);

            resp.Send(HttpStatusCode.OK);
        }
        private void ShowTwitchReplayEndpoint(Request req, Response resp)
        {
            if (!TryDeserializeJsonBodyOrSendBadRequest(req, resp, out TwitchReplayRequest body)) return;

            if (body.PlayerSlot <= 0)
            {
                resp.Send(HttpStatusCode.BadRequest, "appropriate playerSlot required");
                return;
            }

            if(string.IsNullOrWhiteSpace(body.Channel))
            {
                resp.Send(HttpStatusCode.BadRequest, "channel required");
                return;
            }

            if (string.IsNullOrWhiteSpace(body.UserAccessToken))
            {
                resp.Send(HttpStatusCode.BadRequest, "UserAccessToken required");
                return;
            }

            Task.Run(() => MakeTwitchClip(
                body.Channel, 
                _settings?.ClipCreationDelayInSeconds ?? DEFAULT_CLIP_CREATION_DELAY_IN_SECONDS,
                body.UserAccessToken,
                body.PlayerSlot)
            );

            resp.Send(HttpStatusCode.OK);
        }
        private void PingEndpoint(Request req, Response resp)
        {
            resp.Send(HttpStatusCode.OK, "pong");
        }

        private void NotifyInfo(object? sender, NotifyArgs e)
        {
            ConsoleHelper.PrintInfo($"{e.Message}");
        }

        private void NotifyError(object? sender, NotifyArgs e)
        {
            ConsoleHelper.PrintError($"{e.Message}");
        }
        private async Task GetUserAccessTokenEndpoint(Request req, Response resp)
        {
            if (!TryDeserializeJsonBodyOrSendBadRequest(req, resp, out GetUserAccessTokenRequest body)) return;

            if (string.IsNullOrWhiteSpace(body?.AuthCode))
            {
                resp.Send(HttpStatusCode.BadRequest, "AuthCode is required");
                return;
            }

            if(_twitchAPI is null)
            {
                resp.Send(HttpStatusCode.InternalServerError, "twitchAPI is not initialized");
                return;
            }

            var res = await _twitchAPI.GetUserAccessToken(body.AuthCode);

            var respBody = new GetUserAccessTokenResponse()
            {
                Success = res.IsSuccessful,
                ErrorMessage = res.ErrorMessage,
                UserAccessToken = res.Data
            };

            resp.Send(HttpStatusCode.OK, respBody.ToJsonString() ?? string.Empty);
        }
        private void TestEndpoint(Request req, Response resp)
        {
            if (!TryDeserializeJsonBodyOrSendBadRequest(req, resp, out TestRequestBody body)) return;

            if (string.IsNullOrWhiteSpace(body?.Channel))
            {
                resp.Send(HttpStatusCode.BadRequest, "expected non-null 'channel' parameter");
                return;
            }
        }
        private async void MakeTwitchClip(string channel, int delayInSeconds, string userAccessToken, int playerSlot)
        {
            await Task.Delay(delayInSeconds * 1000);

            var res = await _twitchAPI!.MakeClipAsync(channel, userAccessToken);

            if (!res.IsSuccessful)
            {
                ConsoleHelper.PrintError($"failed to make a twitch clip for [{channel}], reason {res.ErrorMessage}");
            }

            _obsDirector?.EnqueueReplay(new TwitchReplay(res.Data, channel, playerSlot));
        }

        private void _twitchBridge_UserAccessTokenRefreshed(object? sender, UserAccessTokenRefreshedArgs e)
        {
            ConsoleHelper.PrintInfo($"refreshed twitchAPI user token");

            if(_settings is not null)
            {
                ConsoleHelper.PrintInfo($"saving new token to disk...");

                Settings newSettings = new()
                {
                    TwitchCredentials = new TwitchCredentials()
                    {
                        ClientID = _settings.TwitchCredentials?.ClientID,
                        ClientSecret = _settings.TwitchCredentials?.ClientSecret,
                        UserAccessToken = e.NewUserAccessToken,
                        //RefreshUserAccessToken = e.NewRefreshUserAccessToken,
                    }
                };

                _settings = newSettings;

                var saveSettingsRes = SaveSettings(_settings);

                if(!saveSettingsRes.IsSuccessful)
                {
                    ConsoleHelper.PrintError($"failed to save new token to disk, reason: {saveSettingsRes.ErrorMessage}");
                }

                ConsoleHelper.PrintInfo("new token is saved");
            }


        }
    }
}
