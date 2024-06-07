using DeathCounterNETShared;
using DeathCounterNETShared.Twitch;

namespace DeathCounterNETServer
{
    internal class Settings
    {
        public TwitchCredentials? TwitchCredentials { get; init; }
        public string? OBSWebSocketPassword { get; init; }
        public int? OBSWebSocketPort { get; init; }
        public int MaxPlayers { get; init; }
        public int ClipCreationDelayInSeconds { get; set; }
        public string? PlayerCaptionSourceNamePattern { get; set; }
        public string? MainSceneName { get; init; }
        public string? TwitchClipReplayBrowserSourceName { get; init; }
    }
}
