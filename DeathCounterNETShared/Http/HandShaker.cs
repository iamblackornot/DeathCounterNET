using RestSharp;
using System;
using System.Collections.Generic;
using System.Text;

namespace DeathCounterNETShared
{
    internal class HandShaker
    {
        static readonly int CheckInterval = 2 * 1000;
        public HandShaker() { }

        public async Task<bool> CheckServer(EndpointIPAddress endpoint)
        {
            RestClient restClient = new RestClient($"http://{endpoint}");
            var resp = await restClient.ExecuteAsync(new RestRequest("/ping"));
            return resp.IsSuccessStatusCode;
        }
        public async Task WaitTillServerIsUp(EndpointIPAddress endpoint)
        {
            while(true)
            {
                var isLive = await CheckServer(endpoint);

                if (isLive) return;

                await Task.Delay(CheckInterval);
            }
        }
    }

}
