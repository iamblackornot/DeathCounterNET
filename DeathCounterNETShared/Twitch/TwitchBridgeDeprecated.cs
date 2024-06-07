using DeathCounterNETShared.Twitch;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TwitchLib.Api;
using TwitchLib.Api.Auth;
using TwitchLib.Api.Core.Exceptions;
using TwitchLib.Api.Helix.Models.Clips.GetClips;

namespace DeathCounterNETShared.Twitch
{
    internal class TwitchBridge
    {
        static readonly int TOKEN_EXPIRATION_THRESHOLD_IN_SECONDS = 5* 60;
        static readonly int TOKEN_VALIDATION_INTERVAL_IN_SECONDS = 45 * 60;
        static readonly int ONE_MINUTE_TIME_GAP_IN_SECONDS = 60;

        static readonly int MAKE_CLIP_TRY_COUNT = 2;
        static readonly int MAKE_CLIP_INTERVAL_IN_MILLISECONDS = 2 * 1000;

        static readonly int GET_CLIP_TRIES_TIMEOUT = 3;
        static readonly int GET_CLIP_RETRY_INTERVAL_IN_MILLISECONDS = 1 * 5000;

        static readonly int VALIDATE_REFRESH_TOKEN_TRY_COUNT = 10;
        static readonly int VALIDATE_REFRESH_TOKEN_INTERVAL_IN_MILLISECONDS = 1 * 1000;


        private readonly object _lock = new object();
        private readonly AsyncReaderWriterLock arwLocker = new();
        private readonly AsyncLock asyncValidateRefreshUserTokenLocker = new();
        private readonly AsyncLock asyncValidateAndRefreshIfNeededUserTokenLocker = new();

        DateTime? _tokenLastValidatedTime;
        DateTime? _tokenExpirationTime;

        TwitchAPI _api;
        TwitchCredentialsDepr _credentials;

        Dictionary<string, string> _broadCasterIdCache;
        Executor _executor;

        public event EventHandler<UserAccessTokenRefreshedArgs>? UserAccessTokenRefreshed;

        public TwitchBridge(TwitchCredentialsDepr credentials)
        {
            _broadCasterIdCache = new();
            _credentials = credentials;

            _api = new TwitchAPI();
            _api.Settings.ClientId = credentials.ClientID;
            //_api.Settings.AccessToken = credentials.UserAccessToken;

            _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .SetCustomExceptionHandler<BadRequestException>(BadRequestExceptionHandler)
                .SetCustomExceptionHandler<BadTokenException>(BadTokenExceptionHandler)
                .Build();
        }
        public async Task<Result<ValidateAccessTokenResult>> ValidateTokenAsync()
        {
            //using (var releaser = await asyncValidateRefreshUserTokenLocker.LockAsync())
            //{
            //    if (_tokenLastValidatedTime != null && _tokenExpirationTime != null)
            //    {
            //        DateTime now = DateTime.Now;
            //        int secondsSinceLastValidation = (int)Math.Ceiling((now - _tokenLastValidatedTime).Value.TotalSeconds);

            //        if (secondsSinceLastValidation < TOKEN_VALIDATION_INTERVAL_IN_SECONDS)
            //        {
            //            int secondsTillExpiration = (int)Math.Ceiling((_tokenExpirationTime - now).Value.TotalSeconds);
            //            bool isValid = secondsTillExpiration > TOKEN_EXPIRATION_THRESHOLD_IN_SECONDS;
            //            return new GoodResult<ValidateAccessTokenResponse>(new ValidateAccessTokenResponse()
            //            {

            //            });
            //        }
            //    }

                Func<Task<Result<ValidateAccessTokenResponse>>> action = async () =>
                {
                    using (var releaser = await arwLocker.ReaderLockAsync())
                    {
                        var res = await _api.Auth.ValidateAccessTokenAsync(_credentials.UserAccessToken);
                        return new GoodResult<ValidateAccessTokenResponse>(res);
                    }
                };

                var vadidateTokenResponse = 
                    await _executor.RepeatTillMadeItOrTimeoutAsync(
                        action,
                        VALIDATE_REFRESH_TOKEN_INTERVAL_IN_MILLISECONDS,
                        VALIDATE_REFRESH_TOKEN_TRY_COUNT);

                if (!vadidateTokenResponse.IsSuccessful)
                {
                    return new BadResult<ValidateAccessTokenResult>(
                        $"couldn't validate token after {VALIDATE_REFRESH_TOKEN_TRY_COUNT} tries, reason: {vadidateTokenResponse.ErrorMessage}");
                }

                ValidateAccessTokenResult res;
                int expiresInSeconds;

                if (vadidateTokenResponse.Data is null)
                {
                    expiresInSeconds = 0;
                    res = new ValidateAccessTokenResult() { IsValid = false, ExpiresInSeconds = 0 };
                }
                else
                {
                    expiresInSeconds = vadidateTokenResponse.Data.ExpiresIn == 0 ? int.MaxValue : vadidateTokenResponse.Data.ExpiresIn;
                    bool isValidToken = expiresInSeconds > TOKEN_EXPIRATION_THRESHOLD_IN_SECONDS;
                    res = new ValidateAccessTokenResult() { IsValid = isValidToken, Login = vadidateTokenResponse.Data.Login };
                }

                UpdateTokenValidationInfo(expiresInSeconds);

                return new GoodResult<ValidateAccessTokenResult>(res);
            //}
        }
        private void UpdateTokenValidationInfo(int expiresInAPIResponseValue)
        {
            int expiresInSeconds = expiresInAPIResponseValue - ONE_MINUTE_TIME_GAP_IN_SECONDS;
            _tokenLastValidatedTime = DateTime.Now;
            _tokenExpirationTime = DateTime.Now + new TimeSpan(0, 0, expiresInSeconds);
        }
        //public async Task<Result<Nothing>> RefreshUserAccessTokenAsync()
        //{
        //    Func<Task<Result<Nothing>>> action = async () =>
        //    {
        //        RefreshResponse refreshTokenResp = await _api.Auth.RefreshAuthTokenAsync(_credentials.RefreshUserAccessToken, _credentials.ClientSecret);

        //        UpdateTokenValidationInfo(refreshTokenResp.ExpiresIn);

        //        TwitchCredentialsDepr newCredentials = new()
        //        {
        //            ClientID = _credentials.ClientID,
        //            ClientSecret = _credentials.ClientSecret,
        //            UserAccessToken = refreshTokenResp.AccessToken,
        //            RefreshUserAccessToken = refreshTokenResp.RefreshToken,
        //        };

        //        _credentials = newCredentials;

        //        var eventArgs = UserAccessTokenRefreshedArgs
        //            .GetBuilder()
        //            .SetNewUserAccessToken(refreshTokenResp.AccessToken)
        //            .SetNewRefreshUserAccessToken(refreshTokenResp.RefreshToken)
        //            .Build();

        //        UserAccessTokenRefreshed?.Invoke(this, eventArgs);

        //        return new GoodResult<Nothing>(new Nothing());
        //    };
        //    using (var releaser = await asyncValidateRefreshUserTokenLocker.LockAsync())
        //    {
        //        var refreshTokenRes = await _executor.RepeatTillMadeItOrTimeoutAsync(
        //                action,
        //                VALIDATE_REFRESH_TOKEN_INTERVAL_IN_MILLISECONDS,
        //                VALIDATE_REFRESH_TOKEN_TRY_COUNT);

        //        if (!refreshTokenRes.IsSuccessful)
        //        {
        //            return new BadResult<Nothing>($"couldn't refresh user acces token after {VALIDATE_REFRESH_TOKEN_TRY_COUNT} tries, " +
        //                $"reason: {refreshTokenRes.ErrorMessage}");
        //        }

        //        return new GoodResult();
        //    }
        //}
        //public async Task<Result> RefreshUserAccesTokenIfNeededAsync()
        //{
        //    using (var releaser = await asyncValidateAndRefreshIfNeededUserTokenLocker.LockAsync())
        //    {
        //        //Console.WriteLine($"starting user token validation (" +
        //        //    Environment.NewLine +
        //        //    $"user_token = {_credentials.UserAccessToken}, " +
        //        //    Environment.NewLine +
        //        //    $"refresh_token = {_credentials.RefreshUserAccessToken})");

        //        var validateTokeRes = await ValidateTokenAsync();

        //        //Console.WriteLine("finished user token validation " +
        //        //    $"(token is valid = {validateTokeRes.IsSuccessful && validateTokeRes.Data})");

        //        if (!validateTokeRes.IsSuccessful)
        //        {
        //            return new BadResult(validateTokeRes.ErrorMessage);
        //        }

        //        if (validateTokeRes.Data.IsValid == true) { return new GoodResult(); }

        //        //Console.WriteLine("starting user token refresh");

        //        Result refreshTokenResult = await RefreshUserAccessTokenAsync();

        //        if (!refreshTokenResult.IsSuccessful)
        //        {
        //            return new BadResult(refreshTokenResult.ErrorMessage);
        //        }

        //        //Console.WriteLine($"finished user token refresh " +
        //        //    Environment.NewLine +
        //        //    $"(new_user_token = {_credentials.UserAccessToken}, " +
        //        //    Environment.NewLine +
        //        //    $"new_refresh_token = {_credentials.RefreshUserAccessToken})");

        //        return new GoodResult();
        //    }
        //}

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
                //_broadCasterIdCache.AddOrUpdate(
                //    channel,
                //    key => id,
                //    (key, currentValue) => id);

                return new GoodResult<string>(id);
            };

            return await _executor.ExecuteAsync(action);
        }
        public async Task<Result<string>> MakeClipAsync(string channel)
        {
            Result<string> getIdRes = await GetBroadcasterIdAsync(channel);

            if(!getIdRes.IsSuccessful)
            {
                return new BadResult<string>($"couldn't get broadcaster id, reason: {getIdRes.ErrorMessage}");
            }

            Func<Task<Result<string>>> makeClipAction = async () =>
            {
                var resp = await _api.Helix.Clips.CreateClipAsync(getIdRes.Data, _credentials.UserAccessToken);

                if(resp is null || resp.CreatedClips.Length == 0)
                {
                    return new BadResult<string>($"twitch API hasn't returned the clip id, something wrong happened");
                }

                return new GoodResult<string>(resp.CreatedClips[0].Id);
            };

            var makeClipRes = await _executor.RepeatTillMadeItOrTimeoutAsync<string>(
                makeClipAction, 
                MAKE_CLIP_INTERVAL_IN_MILLISECONDS,
                MAKE_CLIP_TRY_COUNT);

            if(!makeClipRes.IsSuccessful)
            {
                return makeClipRes;
            }

            Func<Task<Result<Nothing>>> getClipAction = async () =>
            {
                GetClipsResponse resp = await _api.Helix.Clips.GetClipsAsync(new() { makeClipRes.Data } );

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

            if(!getClipRes.IsSuccessful)
            {
                return new BadResult<string>(
                    $"make_clip request was sent, but after {GET_CLIP_TRIES_TIMEOUT} tries failed to get clip creation confirmation, " +
                    $"reason: {getClipRes.ErrorMessage}");
            }

            return new GoodResult<string>(makeClipRes.Data);
        }
        
        private Result BadTokenExceptionHandler(Exception ex)
        {
            return new Result(false, ex.Message);
        }
        private Result BadRequestExceptionHandler(Exception ex)
        {
            return new Result(false, ex.Message);
        }
        private Result DefaultExceptionHandler(Exception ex)
        {
            Logger.AddToLogs(ex.ToString());
            return new Result(false, "twitch api bridge action failed, more info in logs");
        }
    }
    internal class TwitchCredentialsDepr
    {
        public string? ClientID { get; init; }
        public string? ClientSecret { get; init; }
        public string? UserAccessToken { get; init; }
        public string? RefreshUserAccessToken { get; init; }
    }
    internal partial class UserAccessTokenRefreshedArgs : EventArgs
    {
        public string? NewUserAccessToken { get; private set; }
        public string? NewRefreshUserAccessToken { get; private set; }
        private UserAccessTokenRefreshedArgs() { }
    }
    internal partial class UserAccessTokenRefreshedArgs : EventArgs
    {
        public static INewUserAccessTokenRequired GetBuilder()
        {
            return new Builder();
        }
        public interface IBuildable
        {
            UserAccessTokenRefreshedArgs Build();
        }
        public interface INewUserAccessTokenRequired
        {
            INewRefreshUserAccessToken SetNewUserAccessToken(string newUserAccessToken);
        }
        public interface INewRefreshUserAccessToken
        {
            IBuildable SetNewRefreshUserAccessToken(string newUserAccessToken);
        }
        protected class Builder : INewUserAccessTokenRequired, INewRefreshUserAccessToken, IBuildable
        {
            private UserAccessTokenRefreshedArgs _args = new();
            public INewRefreshUserAccessToken SetNewUserAccessToken(string newUserAccessToken)
            {
                _args.NewUserAccessToken = newUserAccessToken;
                return this;
            }
            public IBuildable SetNewRefreshUserAccessToken(string newRefreshUserAccessToken)
            {
                _args.NewRefreshUserAccessToken = newRefreshUserAccessToken;
                return this;
            }
            public UserAccessTokenRefreshedArgs Build()
            {
                return _args;
            }
        }
    }
}
