using RestSharp;
using System.Diagnostics;
using System.Net;

namespace DeathCounterNETShared
{
    internal class UserTokenReceiver : Notifiable
    {
        static readonly int DEFAULT_LOCAL_PORT = 3367;
        HttpServer _server;
        Guid _state;
        TaskCompletionSource<Result<string>> _cts;
        public UserTokenReceiver(int? port)
        {
            _server = new(new EndpointIPAddress(port ?? DEFAULT_LOCAL_PORT));
            _server.AddEndpoint(HttpMethod.Get, "/token", EndpointCallback);

            _state = Guid.NewGuid();
            _cts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public async Task<Result<string>> GetToken(string? clientId, List<string> scopes, EndpointIPAddress hostServer)
        {
            if(clientId is null) return new BadResult<string>("ClientId is required");

            var startLocalServerRes = _server.Start();

            if(!startLocalServerRes.IsSuccessful)
            {
                return new BadResult<string>($"couldn't start a local server to get user access token, reason: {startLocalServerRes.ErrorMessage}");
            }

            string url =
                $"https://id.twitch.tv/oauth2/authorize?" +
                $"response_type=code" +
                $"&client_id={clientId}" +
                $"&redirect_uri=http://localhost:{_server.Endpoint.Port}/token" +
                $"&scope={string.Join('+', scopes)}" +
                $"&state={_state}";

            Process.Start(new ProcessStartInfo()
            {
                FileName = url,
                UseShellExecute = true,
            });

            var getAuthCodeRes = await _cts.Task;
            _server.Stop();

            if (!getAuthCodeRes.IsSuccessful)
            {
                return new BadResult<string>($"couldn't get an auth code, reason: {getAuthCodeRes.ErrorMessage}");
            }

            Executor _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(RestRequestExeceptionHandler)
                .Build();

            return await _executor.ExecuteAsync(() => GetUserAccessToken(hostServer, getAuthCodeRes.Data));
        }

        private Result RestRequestExeceptionHandler(Exception ex)
        {
            Logger.AddToLogs("UserTokenReceiver", ex.ToString());
            return new BadResult("failed to send request to host server");
        }

        private async Task<Result<string>> GetUserAccessToken(EndpointIPAddress hostServer, string authCode)
        {
            OnNotifyInfo("got auth code from twitch");
            OnNotifyInfo("checking whether host server is up...");

            HandShaker handShaker = new HandShaker();
            bool isServerUp = await handShaker.CheckServer(hostServer);

            if (!isServerUp)
            {
                OnNotifyInfo("host server is down, waiting for it to go live...");
                await handShaker.WaitTillServerIsUp(hostServer);
            }

            OnNotifyInfo("host server is up, sending auth code to get user access token");

            RestClient client = new RestClient($"http://{hostServer}");
            RestRequest request = new RestRequest("/get_user_access_token", Method.Post);
            request.Timeout = 10 * 1000;
            GetUserAccessTokenRequest body = new() { AuthCode = authCode };
            request.AddJsonBody(body, ContentType.Json);

            var resp = await client.PostAsync<GetUserAccessTokenResponse>(request);

            if (resp is null)
            {
                return new BadResult<string>("coudn't get response from host server");
            }

            if (!resp.Success)
            {
                return new BadResult<string>($"got unsuccessful response, reason: {resp.ErrorMessage}");
            }

            return new GoodResult<string>(resp.UserAccessToken ?? string.Empty);
        }
        private void EndpointCallback(Request req, Response resp)
        {
            _cts.SetResult(DoWork(req, resp));
        }
        private Result<string> DoWork(Request req, Response resp)
        {
            if (req.QueryString is null)
            {
                string message = "expected a query string with code and state parameters";
                resp.Send(HttpStatusCode.BadRequest, message);

                return new BadResult<string>(message);
            }

            if (req.QueryString.TryGetValue("code", out string? code) &&
                req.QueryString.TryGetValue("state", out string? state))
            {

                if (state != _state.ToString())
                {
                    string message = "state doesn't match";
                    resp.Send(HttpStatusCode.BadRequest, message);
                    return new BadResult<string>(message);
                }

                resp.Send(HttpStatusCode.OK);
                return new GoodResult<string>(code);
            }

            if (req.QueryString.TryGetValue("error", out _) &&
                req.QueryString.TryGetValue("error_description", out string? error))
            {
                resp.Send(HttpStatusCode.OK);
                return new BadResult<string>(error.Replace('+', ' '));
            }

            string msg = "bad request";
            resp.Send(HttpStatusCode.BadRequest, msg);

            return new BadResult<string>(msg);
        }
    }

    public class UserAccessTokenResult
    {
        public string Login { get; init; }
        public string Token { get; init; }

        public UserAccessTokenResult(string login, string token)
        {
            Login = login;
            Token = token;
        }
    }
}
