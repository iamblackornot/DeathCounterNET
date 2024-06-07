// See https://aka.ms/new-console-template for more information
using DeathCounterNETServer;

var app = new ServerApplication();
await app.StartAsync();

Console.WriteLine("press any key to close...");
Console.ReadKey();

