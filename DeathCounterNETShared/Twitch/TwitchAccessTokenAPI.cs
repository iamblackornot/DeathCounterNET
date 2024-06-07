using System.Collections.Concurrent;
using System.Net;

namespace DeathCounterNETShared.Twitch
{
    internal class TwitchAccessTokenAPI : TwitchPublicAPI
    {
        protected ConcurrentDictionary<string, string> _broadCasterIdCache;
        public TwitchCredentials Credentials { get; init; }

        public TwitchAccessTokenAPI(TwitchCredentials credentials) 
        {
            _api.Settings.ClientId = credentials.ClientID;
            _api.Settings.Secret = credentials.ClientSecret;
            _api.Settings.AccessToken = credentials.UserAccessToken;

            Credentials = credentials;

            _broadCasterIdCache = new();
        }
        public async Task<Result<string>> GetBroadcasterIdAsync(string channel)
        {
            if (_broadCasterIdCache.ContainsKey(channel))
            {
                return new GoodResult<string>(_broadCasterIdCache[channel]);
            }

            Func<Task<Result<string>>> action = async () =>
            {
                var resp = await _api.Helix.Users.GetUsersAsync(null, new List<string> { channel });

                if (resp.Users.Length == 0)
                {
                    return new BadResult<string>($"no users with name [{channel} found");
                }

                string id = resp.Users[0].Id;
                _broadCasterIdCache.AddOrUpdate(
                    channel, 
                    key => id,
                    (key, currentValue) => id);

                return new GoodResult<string>(id);
            };

            return await _executor.ExecuteAsync(action);
        }
        public async Task<Result<string>> GetUserAccessToken(string authCode)
        {
            Func<Task<Result<string>>> action = async () =>
            {
                var resp = await _api.Auth.GetAccessTokenFromCodeAsync(authCode, _api.Settings.Secret, "http://localhost");

                if (resp is null)
                {
                    return new BadResult<string>($"response is null");
                }

                return new GoodResult<string>(resp.AccessToken);
            };

            return await _executor.ExecuteAsync(action);
        }
    }
}
