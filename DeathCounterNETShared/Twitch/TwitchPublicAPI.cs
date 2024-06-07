using TwitchLib.Api;
using TwitchLib.Api.Auth;
using TwitchLib.Api.Core.Exceptions;

namespace DeathCounterNETShared.Twitch
{
    internal class TwitchPublicAPI
    {
        static readonly int TOKEN_EXPIRATION_THRESHOLD_IN_SECONDS = 5 * 60;

        static readonly int VALIDATE_REFRESH_TOKEN_TRY_COUNT = 10;
        static readonly int VALIDATE_REFRESH_TOKEN_INTERVAL_IN_MILLISECONDS = 1 * 1000;

        protected TwitchAPI _api;
        protected Executor _executor;
        public TwitchPublicAPI()
        {
            _api = new TwitchAPI();

            _executor = Executor
                .GetBuilder()
                .SetDefaultExceptionHandler(DefaultExceptionHandler)
                .SetCustomExceptionHandler<BadScopeException>(BadScopeExceptionHandler)
                .SetCustomExceptionHandler<BadTokenException>(BadTokenExceptionHandler)
                .SetCustomExceptionHandler<ClientIdAndOAuthTokenRequired>(ClientIdAndOAuthTokenRequiredHandler)
                .Build();
        }
        public async Task<Result<ValidateAccessTokenResult>> ValidateTokenAsync(string? token)
        {
            if (token is null) return new GoodResult<ValidateAccessTokenResult>(new ValidateAccessTokenResult() { IsValid = false });

            Func<Task<Result<ValidateAccessTokenResponse>>> action = async () =>
            {
                var res = await _api.Auth.ValidateAccessTokenAsync(token);
                return new GoodResult<ValidateAccessTokenResponse>(res);
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

            if (vadidateTokenResponse.Data is null)
            {
                return new GoodResult<ValidateAccessTokenResult>(new() { IsValid = false });
            }

            int expiresInSeconds = vadidateTokenResponse.Data.ExpiresIn == 0 ? int.MaxValue : vadidateTokenResponse.Data.ExpiresIn;
            bool isValidToken = expiresInSeconds > TOKEN_EXPIRATION_THRESHOLD_IN_SECONDS;

            return new GoodResult<ValidateAccessTokenResult>(new ValidateAccessTokenResult()
            {
                IsValid = isValidToken,
                ExpiresInSeconds = expiresInSeconds,
                Login = vadidateTokenResponse.Data.Login,
                Scopes = vadidateTokenResponse.Data.Scopes
            });
        }
        private Result BadTokenExceptionHandler(Exception ex)
        {
            return new Result(false, ex.Message);
        }
        private Result BadScopeExceptionHandler(Exception ex)
        {
            return new Result(false, "Access Token either is invalid or doesn't have the right scope");
        }
        private Result ClientIdAndOAuthTokenRequiredHandler(Exception ex)
        {
            return new Result(false, "Client_ID and Client_Secret are required");
        }
        private Result DefaultExceptionHandler(Exception ex)
        {
            Logger.AddToLogs(ex.ToString());
            return new Result(false, "twitch api bridge action failed, more info in logs");
        }
    }
}
