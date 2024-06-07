using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net;

namespace DeathCounterNETShared
{
    public class Result
    {
        public bool IsSuccessful { get; private set; }
        public string ErrorMessage { get; private set; }

        public Result(bool isSuccessful, string errorMessage = "")
        {
            IsSuccessful = isSuccessful;
            ErrorMessage = errorMessage;
        }
    }
    public class Result<T> : Result
    {
        public T Data { get; private set; }
        public Result(bool isSuccessful, in T data, string errorMessage = "")
            : base(isSuccessful, errorMessage) { Data = data; }

        public Result(Result rhs) : base(rhs.IsSuccessful, rhs.ErrorMessage)
        {
            Data = default(T)!;
        }
    }
    public class BadResult<T> : Result<T>
    {
        public BadResult(string errorMessage = "") : base(false, default(T)!, errorMessage) { }
    }
    public class GoodResult<T> : Result<T>
    {
        public GoodResult(T data) : base(true, data) { }
    }
    public class GoodResult : Result<Nothing>
    {
        public GoodResult() : base(true, new Nothing { }) { }
    }
    public class BadResult : Result<Nothing>
    {
        public BadResult(string errorMessage = "") : base(false, new Nothing { }, errorMessage) { }
    }
    public static class Utility
    {
        public static IPAddress? StringToIPAddress(string? str)
        {          
            IPAddress.TryParse(str, out var address);
            return address;
        }
    }

    public struct Nothing { }
    public class NotifyArgs : EventArgs
    {
        public string Message { get; init; }

        public NotifyArgs(string message)
        {
            Message = message;
        }
    }
    abstract class Notifiable
    {
        public event EventHandler<NotifyArgs>? NotifyInfo;
        public event EventHandler<NotifyArgs>? NotifyError;
        protected void OnNotifyInfo(string message)
        {
            NotifyInfo?.Invoke(this, new NotifyArgs(message));
        }
        protected void OnNotifyError(string message)
        {
            NotifyError?.Invoke(this, new NotifyArgs(message));
        }
    }
    public class IpEndpointSerializer : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(IPEndPoint));
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if(value is IPEndPoint endPoint)
            {
                JObject jo = new()
                {
                    { "ip", endPoint.Address.ToString() },
                    { "port", endPoint.Port.ToString() }
                };
                jo.WriteTo(writer);
            }
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);

            if(!IPEndPoint.TryParse($"{jo["ip"]}:{jo["port"]}", out IPEndPoint? result))
            {
                return null;
            }
  
            return result;
        }
    }
}
