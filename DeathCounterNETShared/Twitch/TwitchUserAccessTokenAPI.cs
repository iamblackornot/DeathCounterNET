using System;
using System.Collections.Concurrent;
using TwitchLib.Api.Helix.Models.Clips.GetClips;

namespace DeathCounterNETShared.Twitch
{
    internal class TwitchUserAccessTokenAPI : TwitchAccessTokenAPI
    {
        static readonly int MAKE_CLIP_TRY_COUNT = 2;
        static readonly int MAKE_CLIP_INTERVAL_IN_MILLISECONDS = 2 * 1000;

        static readonly int GET_CLIP_TRIES_TIMEOUT = 3;
        static readonly int GET_CLIP_RETRY_INTERVAL_IN_MILLISECONDS = 1 * 5000;

        static readonly int VALIDATE_TOKEN_INTERVAL = 15 * 60 * 1000;

        private ConcurrentDictionary<string, DateTime> _tokenLastValidated;

        public TwitchUserAccessTokenAPI(TwitchCredentials credentials) : base(credentials)
        {
            _tokenLastValidated = new();
        }
        private async Task<Result<bool>> ValidateTokenIfNeededAsync(string userAccessToken)
        {
            if(_tokenLastValidated.TryGetValue(userAccessToken, out DateTime lastValidated))
            {
                int msSinceLastValidation = (int)Math.Ceiling((DateTime.Now - lastValidated).TotalMilliseconds);

                if (msSinceLastValidation < VALIDATE_TOKEN_INTERVAL)
                {
                    return new GoodResult<bool>(true);
                }
            }

            var res = await ValidateTokenAsync(userAccessToken);

            if(!res.IsSuccessful) return new BadResult<bool>($"couldn't validate token, reason {res.ErrorMessage}");

            if(res.Data.IsValid)
            {
                _tokenLastValidated.AddOrUpdate(userAccessToken, DateTime.Now, (string key, DateTime value) => { return DateTime.Now; });
            }

            return new GoodResult<bool>(res.Data.IsValid);
        }
        public async Task<Result<string>> MakeClipAsync(string channel, string userAccessToken)
        {
            var validateTokenRes = await ValidateTokenIfNeededAsync(userAccessToken);

            if (!validateTokenRes.IsSuccessful)
            {
                return new BadResult<string>(validateTokenRes.ErrorMessage);
            }

            if(!validateTokenRes.Data)
            {
                return new BadResult<string>("User Access Token is invalid, player should restart the client app and authorize it in order to get new token");
            }

            Result<string> getIdRes = await GetBroadcasterIdAsync(channel);

            if (!getIdRes.IsSuccessful)
            {
                return new BadResult<string>($"couldn't get broadcaster id, reason: {getIdRes.ErrorMessage}");
            }

            Func<Task<Result<string>>> makeClipAction = async () =>
            {
                var resp = await _api.Helix.Clips.CreateClipAsync(getIdRes.Data, userAccessToken);

                if (resp is null || resp.CreatedClips.Length == 0)
                {
                    return new BadResult<string>($"twitch API hasn't returned the clip id, something wrong happened");
                }

                return new GoodResult<string>(resp.CreatedClips[0].Id);
            };

            var makeClipRes = await _executor.RepeatTillMadeItOrTimeoutAsync<string>(
                makeClipAction,
                MAKE_CLIP_INTERVAL_IN_MILLISECONDS,
                MAKE_CLIP_TRY_COUNT);

            if (!makeClipRes.IsSuccessful)
            {
                return makeClipRes;
            }

            Func<Task<Result<Nothing>>> getClipAction = async () =>
            {
                GetClipsResponse resp = await _api.Helix.Clips.GetClipsAsync(new() { makeClipRes.Data });

                if (resp is null || resp.Clips.Length == 0)
                {
                    return new BadResult($"clip not found");
                }

                return new GoodResult();
            };

            var getClipRes = await _executor.RepeatTillMadeItOrTimeoutAsync(
                getClipAction,
                GET_CLIP_RETRY_INTERVAL_IN_MILLISECONDS,
                GET_CLIP_TRIES_TIMEOUT);

            if (!getClipRes.IsSuccessful)
            {
                return new BadResult<string>(
                    $"make_clip request was sent, but after {GET_CLIP_TRIES_TIMEOUT} tries failed to get clip creation confirmation, " +
                    $"reason: {getClipRes.ErrorMessage}");
            }

            return new GoodResult<string>(makeClipRes.Data);
        }

    }
}
