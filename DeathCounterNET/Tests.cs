using DeathCounterNETShared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeathCounterNET
{
    internal class Tests
    {
        public static void TestCaptionUpdateSpam()
        {
            PlayerClient client = new PlayerClient(new EndpointIPAddress(3366));
            int playerCount = 4;

            for (int i = 0; i < 1000; i++)
            {
                List<Task<Result<Nothing>>> tasks = new(playerCount);

                for (int playerNum = 1; playerNum <= playerCount; playerNum++)
                {
                    tasks.Add(client.UpdatePlayerCaptionAsync(playerNum, $"loh{playerNum} = {i}"));
                }

                Task.WhenAll(tasks).Wait();

                for (int playerNum = 1; playerNum <= playerCount; playerNum++)
                {
                    Result res = tasks[playerNum - 1].Result;

                    if (res.IsSuccessful)
                    {
                        ConsoleHelper.PrintError($"failed playerNum[{playerNum}], i = {i}, reason: {res.ErrorMessage}");
                    }
                }

                Thread.Sleep(1);
            }
        }
        public static void TestAsyncUpdateCaption()
        {
            PlayerClient client = new PlayerClient(new EndpointIPAddress(3366));

            var task = client.UpdatePlayerCaptionAsync(5, $"loh1 = 100");
            task.Wait();

            if(!task.Result.IsSuccessful)
                ConsoleHelper.PrintError($"failed to update player caption, reason: {task.Result.ErrorMessage}");
         
        }
        public static void TestUpdateCaption()
        {
            PlayerClient client = new PlayerClient(new EndpointIPAddress(3366));
            client.NotifyError += (object? sender, NotifyArgs args) =>
            {
                ConsoleHelper.PrintError($"{args.Message}");
            };

            client.UpdatePlayerCaption(5, $"loh1 = 100");

            Thread.Sleep(10000);
        }
    }
}
