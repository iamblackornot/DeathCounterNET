namespace DeathCounterNETShared.Twitch
{
    internal class TwitchCredentials
    {
        public string? ClientID { get; init; }
        public string? ClientSecret { get; init; }
        public string? UserAccessToken { get; init; }
        public string? RedirectUri { get; init; }
    }
}
