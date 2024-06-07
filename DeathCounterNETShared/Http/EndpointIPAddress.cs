using Newtonsoft.Json;
using System.Net;

namespace DeathCounterNETShared
{
    public class EndpointIPAddress
    {
        private IPEndPoint _endpoint;
        public string IP => _endpoint.Address.ToString();
        public int Port => _endpoint.Port;
        [JsonConstructor]
        public EndpointIPAddress(string ip, int port)
        {
            _endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        }
        public EndpointIPAddress(IPAddress ip, int port)
        {
            _endpoint = new IPEndPoint(ip, port);
        }
        public EndpointIPAddress(int port)
        {
            _endpoint = new IPEndPoint(IPAddress.Loopback, port);
        }
        public EndpointIPAddress(IPEndPoint endpoint)
        {
            _endpoint = endpoint;
        }
        public static implicit operator EndpointIPAddress(IPEndPoint? endpoint)
        {
            return new EndpointIPAddress(endpoint!);
        }

        public static bool TryParse(string? value, out EndpointIPAddress? endpoint)
        {
            endpoint = null;

            if(string.IsNullOrWhiteSpace(value)) { return false; }

            if (IPEndPoint.TryParse(value, out var ipEndpoint))
            {
                endpoint = ipEndpoint;
                return true;
            }

            return false;
        }
        public override string ToString()
        {
            return $"{IP}:{Port}";
        }
    }
}
