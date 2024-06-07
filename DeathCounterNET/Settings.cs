using DeathCounterNETShared;
using DeathCounterNETShared.Twitch;

namespace DeathCounterNET
{
    internal class Settings
    {
        public EndpointIPAddress? HostServerEndpoint { get; init; }
        public EndpointIPAddress? PlayerWebSocketEndpoint { get; init; }
        public int? LocalListenPort { get; init; }
        public TwitchCredentials TwitchCredentials { get; init; } = new TwitchCredentials();
        public string? DisplayedName { get; init; }
    }
}
