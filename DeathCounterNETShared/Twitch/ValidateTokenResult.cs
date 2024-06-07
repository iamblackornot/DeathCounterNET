using System;
using System.Collections.Generic;
using System.Text;

namespace DeathCounterNETShared.Twitch
{
    public class ValidateAccessTokenResult
    {
        public bool IsValid { get; init; } = false;
        public int ExpiresInSeconds { get; init; } = 0;
        public string? Login { get; init; }
        public List<string>? Scopes { get; init; }
    }
}
