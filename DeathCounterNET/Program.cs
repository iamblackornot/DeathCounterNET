// See https://aka.ms/new-console-template for more information

using DeathCounterNET;
using DeathCounterNET.Games;
using DeathCounterNETShared;

//GameInjector gameInjector = new ConditionZeroInjector();
GameInjector gameInjector = new OnlyUpInjector();
await gameInjector.StartAsync();
return;