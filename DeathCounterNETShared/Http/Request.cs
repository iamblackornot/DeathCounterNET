using Newtonsoft.Json;
using System.Drawing;

namespace DeathCounterNETShared
{
    public abstract class JsonBody<T>
    {
        public string ToJsonString()
        {
            return JsonConvert.SerializeObject(this);
        }
        public static bool TryParse(string? json, out T res)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                res = default!;
                return false;
            }

            res = JsonConvert.DeserializeObject<T>(json)!;

            if (res is null) return false;

            return true;
        }
    }
    public class PlaceHolderJsonBody<T> : JsonBody<T>
    {
        public PlaceHolderJsonBody() { }
    }
    public class BaseResponse : BaseResponse<Nothing> { }
    public class BaseResponse<T> : JsonBody<T>
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
    }
    public class TestRequestBody : JsonBody<TestRequestBody>
    {
        public string? Channel { get; init; }
    }
    public class GetUserAccessTokenRequest : JsonBody<GetUserAccessTokenRequest>
    {
        public string? AuthCode { get; init; }
    }

    public class GetUserAccessTokenResponse : BaseResponse<GetUserAccessTokenResponse>
    {
        public string? UserAccessToken { get; init; }
    }
    public class NotifyJoinRequest : JsonBody<NotifyJoinRequest>
    {
        public string? DisplayedName { get; init; }
        public int? PlayerSlot { get; init; }
    }
    public class UpdatePlayerCaptionRequest : JsonBody<UpdatePlayerCaptionRequest>
    {
        public int PlayerSlot { get; init; }
        public string? Caption { get; init; }
        public Color? Color { get; init; }
    }
    public class TwitchReplayRequest : JsonBody<TwitchReplayRequest>
    {
        public int PlayerSlot { get; init; }
        public string? Channel { get; init; }
        public string? UserAccessToken { get; init; }
    }
}
