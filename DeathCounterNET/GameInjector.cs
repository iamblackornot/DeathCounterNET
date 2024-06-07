using DeathCounterNETShared;
using DeathCounterNETShared.Twitch;
using Memory;
using Newtonsoft.Json;
using System.Data;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Linq;
using System.Net;

namespace DeathCounterNET
{
    internal abstract class GameInjector: AppBase<Settings>
    {
        const string SETTINGS_PATH = "settings.ini";
        static readonly int DEFAULT_UPDATE_INTERVAL = 200;

        protected Settings? _settings;
        protected Options _options;
        private OBSBridgeController _obsBridgeController;
        private OBSBridge? _playerOBSBridge;
        private PlayerClient? _playerClient;
        private int _playerSlot = -1;
        private string? _twitchChannel;
        protected MemoryInjector _memoryInjector;

        protected GameInjector(Options options) : base(SETTINGS_PATH)
        {
            _options = options;
            _memoryInjector = new MemoryInjector();
            _obsBridgeController = new OBSBridgeController();
            _obsBridgeController.ReconnectInitiated += ObsBridgeController_ReconnectInitiated;
        }

        public async Task StartAsync()
        {
            if (! await Init())
            {
                ConsoleHelper.PrintError("failed to initialize the app");
                return;
            }

            try
            {
                if(_options.ReplayType == ReplayType.Local)
                {
                    _playerOBSBridge = await CreateOBSBridgeAndConnectAsync(new OBSBridgeOptions()
                    {
                        Destination = "Player",
                        Endpoint = _settings!.HostServerEndpoint,
                        ReplayTimeout = _options.InstantReplayTimeout,
                        ReplayDelay = _options.InstantReplayDelay,
                    });
                }

                if(_options.ReplayType == ReplayType.Twitch)
                {
                    _playerClient = new PlayerClient(_settings!.HostServerEndpoint!);
                    _playerClient.NotifyInfo += NotifyInfo;
                    _playerClient.NotifyInfo += NotifyError;

                    ConsoleHelper.PrintInfo("checking host's server...");

                    var res = await _playerClient.CheckServer();

                    if(!res.IsSuccessful)
                    {
                        ConsoleHelper.PrintInfo("host's server is down");
                        ConsoleHelper.PrintInfo("waiting for host's server handshake...");

                        await _playerClient.WaitTillServerIsUp();
                    }

                    ConsoleHelper.PrintInfo("host's server is up");
                    await _playerClient.NotifyJoin(_settings.DisplayedName!, _playerSlot);
                }

                AttachProcessLoop();

                if(!DoCustomPostAttachProcessWork())
                {
                    ConsoleHelper.PrintTryRestart();
                }

                ConsoleHelper.PrintInfo("now everything is set up!");

                MainLoopAsync();
            }
            catch(Exception ex)
            {
                Logger.AddToLogs("StartAsync", ex.ToString());
            }
        }
        protected async Task<bool> Init()
        {
            ConsoleHelper.PrintInfo("initializing...");
            bool initRes = true;

            try
            {
                Result<Settings> settingsRes = LoadSettings();

                if(!settingsRes.IsSuccessful) 
                { 
                    ConsoleHelper.PrintError(settingsRes.ErrorMessage);
                    return false; 
                }

                _settings = settingsRes.Data;

                bool toSaveSettings = false;

                EndpointIPAddress? newHostServerEndpoint = null;

                if(_settings.HostServerEndpoint is null)
                {
                    IPAddress ip = ConsoleHelper.PromptValue<IPAddress>("enter host's server ip address", Utility.StringToIPAddress);
                    int port = ConsoleHelper.PromptValue<int>("enter host's server port (by default, 3366)");
                    newHostServerEndpoint = new EndpointIPAddress(ip, port);
                    toSaveSettings = true;
                }

                EndpointIPAddress? newPlayerWebsocketEndpoint = null;

                if (_options.ReplayType == ReplayType.Local && _settings.PlayerWebSocketEndpoint is null)
                {
                    IPAddress ip = IPAddress.Loopback;
                    int port = ConsoleHelper.PromptValue<int>("enter your obs websocket port (by default, 4455)");
                    newPlayerWebsocketEndpoint = new EndpointIPAddress(ip, port);
                    toSaveSettings = true;
                }

                string? newUserAccessToken = null;

                if (_options.ReplayType == ReplayType.Twitch)
                {
                    bool toAskForAuthorization = true;

                    TwitchPublicAPI twitchAPI = new();

                    if (_settings.TwitchCredentials.UserAccessToken is not null)
                    {
                        ConsoleHelper.PrintInfo("found saved TwitchAPI user access token");
                        ConsoleHelper.PrintInfo("validating...");

                        var validateRes = await twitchAPI.ValidateTokenAsync(_settings.TwitchCredentials.UserAccessToken);

                        if (!validateRes.IsSuccessful)
                        {
                            ConsoleHelper.PrintError(validateRes.ErrorMessage);
                            initRes = false;
                        }
                        else if (validateRes.Data.IsValid)
                        {
                            toAskForAuthorization = false;
                            ConsoleHelper.PrintInfo($"token is valid, associated channel is [{validateRes.Data.Login}]");
                            _twitchChannel = validateRes.Data.Login;
                        }
                        else
                        {
                            ConsoleHelper.PrintInfo("token is invalid");
                        }
                    }

                    if (toAskForAuthorization)
                    {
                        ConsoleHelper.PrintInfo("the application needs authorization to make twitch clips of your channel");
                        ConsoleHelper.PrintInfo("you will be redirected to the twitch page where you have to confirm access");
                        ConsoleHelper.PrintInfo("make sure to authorize using your stream account");
                        ConsoleHelper.PrintPrompt("press any key to proceed...");

                        Console.ReadKey();
                        Console.WriteLine();


                        ConsoleHelper.PrintInfo("waiting for confirmation...");

                        UserTokenReceiver receiver = new UserTokenReceiver(_settings.LocalListenPort);
                        receiver.NotifyInfo += NotifyInfo;
                        EndpointIPAddress host = _settings.HostServerEndpoint ?? newHostServerEndpoint!;

                        var getTokenRes = await receiver.GetToken(_settings.TwitchCredentials.ClientID, new() { "clips:edit" }, host);

                        if (!getTokenRes.IsSuccessful)
                        {
                            ConsoleHelper.PrintError($"failed to authorize the app, reason: {getTokenRes.ErrorMessage}");
                            initRes = false;
                        }
                        else
                        {
                            ConsoleHelper.PrintInfo($"got new user access token");
                            newUserAccessToken = getTokenRes.Data;
                            toSaveSettings = true;

                            ConsoleHelper.PrintInfo("validating...");

                            var validateRes = await twitchAPI.ValidateTokenAsync(newUserAccessToken);

                            if (!validateRes.IsSuccessful)
                            {
                                ConsoleHelper.PrintError(validateRes.ErrorMessage);
                                initRes = false;
                            }
                            else if (validateRes.Data.IsValid)
                            {
                                ConsoleHelper.PrintInfo($"token is valid, associated channel is [{validateRes.Data.Login}]");
                                _twitchChannel = validateRes.Data.Login;
                            }
                            else
                            {
                                ConsoleHelper.PrintInfo("new token is invalid, something fucked up really bad ");
                                initRes = false;
                            }
                        }
                    }
                }

                _playerSlot = ConsoleHelper.PromptValue<int>("enter player slot number provided by host");
                ConsoleHelper.PrintInfo($"you've entered slot [{_playerSlot}]");

                bool enterNameLoop = true;

                if (!string.IsNullOrWhiteSpace(_settings!.DisplayedName))
                {
                    ConsoleHelper.PrintInfo($"saved displayed name is [{_settings.DisplayedName}]");

                    enterNameLoop = ConsoleHelper.ShowModalPrompt(
                        new ModalPromptOptions()
                        {
                            Question = "choose how you want to be represented",
                            YesOption = "continue with this name",
                            NoOption = "enter new name",
                        }) == PromptResult.No;
                }

                string? newDisplayedName = null;

                if(enterNameLoop)
                {
                    newDisplayedName = ConsoleHelper.PromptValue("enter displayed name");
                    ConsoleHelper.PrintInfo($"displayed name is [{newDisplayedName}]");
                    toSaveSettings = true;
                }              

                if(toSaveSettings)
                {
                    _settings = new Settings()
                    {
                        HostServerEndpoint = newHostServerEndpoint ?? _settings.HostServerEndpoint,
                        PlayerWebSocketEndpoint = newPlayerWebsocketEndpoint ?? _settings.PlayerWebSocketEndpoint,
                        DisplayedName = newDisplayedName ?? _settings.DisplayedName,
                        LocalListenPort = _settings.LocalListenPort,
                        TwitchCredentials = new TwitchCredentials()
                        {
                            ClientID = _settings.TwitchCredentials.ClientID,
                            UserAccessToken = newUserAccessToken ?? _settings.TwitchCredentials.UserAccessToken
                        }
                    };

                    var res = SaveSettings( _settings );

                    if(res.IsSuccessful)
                    {
                        ConsoleHelper.PrintInfo("saved updated settings to disk");
                    }
                    else
                    {
                        ConsoleHelper.PrintError($"failed to save updated settings to disk, reason: {res.ErrorMessage}");
                    }
                }

                if (_settings is null)
                {
                    ConsoleHelper.PrintError("no settings loaded");
                    return false;
                }

                if (_options.ReplayType == ReplayType.Twitch && _settings.HostServerEndpoint is null)
                {
                    ConsoleHelper.PrintError("host server address is empty");
                    return false;
                }
                if (_options.ReplayType == ReplayType.Local && _settings.PlayerWebSocketEndpoint is null)
                {
                    ConsoleHelper.PrintError("player websocket ip is empty");
                    return false;
                }

                if (!initRes) return false;

                return DoCustomInitWork();
            }
            catch (Exception ex)
            {
                ConsoleHelper.PrintError("something failed during initialization, more info in logs");
                Logger.AddToLogs("main", ex.ToString());
                return false;
            }

        }

        protected async Task<OBSBridge> CreateOBSBridgeAndConnectAsync(OBSBridgeOptions options)
        {
            OBSBridge obsBridge = new OBSBridge(options);
            obsBridge.Connected += OBSBridge_Connected;
            obsBridge.Disconnected += OBSBridge_Disconnected;
            _obsBridgeController.Add(obsBridge);

            ConsoleHelper.PrintInfo($"trying to connect to {options.Destination}'s obs...");
            await obsBridge.ConnectTillMadeItAsync();

            return obsBridge;
        }
        protected IntPtr WaitForDLLToBeLoaded(string dllName)
        {
            IntPtr address = _memoryInjector.GetModuleAdress(dllName);
            
            while (address == IntPtr.Zero)
            {
                Thread.Sleep(500);
                address = _memoryInjector.GetModuleAdress(dllName);
            } 

            return address;
        }

        protected void TriggerReplay()
        {
            if (_options.ReplayType == ReplayType.Local)
            {
                Task.Run(async () =>
                {
                    if (_playerOBSBridge is null)
                    {
                        ConsoleHelper.PrintError($"failed to trigger local replay, reason: player's obs bridge is null");
                        return;
                    }

                    var res = await _playerOBSBridge.SaveReplayBuffer();

                    if (!res.IsSuccessful)
                    {
                        ConsoleHelper.PrintError($"failed to trigger local replay, reason: {res.ErrorMessage}");
                        return;
                    }
                });
            }
            if (_options.ReplayType == ReplayType.Twitch)
            {
                if (_twitchChannel is null)
                {
                    ConsoleHelper.PrintError($"failed to trigger twitch replay, reason: twitch channel is null");
                    return;
                }
                if (_settings?.TwitchCredentials.UserAccessToken is null)
                {
                    ConsoleHelper.PrintError($"failed to trigger twitch replay, reason: user access token is null");
                    return;
                }
                if (_playerClient is null)
                {
                    ConsoleHelper.PrintError($"failed to trigger twitch replay, reason: player client is null");
                    return;
                }

                _playerClient.ShowTwitchReplay(_playerSlot, _twitchChannel, _settings.TwitchCredentials.UserAccessToken);
            }
        }

        protected void UpdatePlayerCaption(string caption, Color? color = null)
        {
            caption = $"{_settings?.DisplayedName} - {caption}";

            if (_options.ReplayType == ReplayType.Local)
            {
                if (_playerOBSBridge is null)
                {
                    ConsoleHelper.PrintError($"failed to update player info, reason: player obs bridge is null");
                    return;
                }

                var res = _playerOBSBridge.UpdatePlayerCaption(_playerSlot, caption, color);

                if (res is null || !res.IsSuccessful)
                {
                    ConsoleHelper.PrintError($"failed to update player info, reason: {res?.ErrorMessage}");
                }
            }
            if (_options.ReplayType == ReplayType.Twitch)
            {
                if (_playerClient is null)
                {
                    ConsoleHelper.PrintError($"failed to update player info, reason: player client is null");
                    return;
                }

                _playerClient?.UpdatePlayerCaption(_playerSlot, caption, color);
            }
        }
        private void OBSBridge_Disconnected(object? sender, OBSWebsocketDotNet.Communication.ObsDisconnectionInfo e)
        {
            if (sender is OBSBridge bridge)
            {
                ConsoleHelper.PrintError($"disconnected from {bridge.Destination}'s obs, reason: {e.DisconnectReason}");
            }
        }

        private void OBSBridge_Connected(object? sender, EventArgs e)
        {
            if(sender is OBSBridge bridge)
            {
                ConsoleHelper.PrintInfo($"connected to {bridge.Destination}'s obs");
            }
        }

        private void AttachProcessLoop()
        {
            if (_memoryInjector.Attach(_options.TargetProcess))
            {
                ConsoleHelper.PrintInfo($"process [{_options.TargetProcess}] found");
                return;
            }

            ConsoleHelper.PrintInfo("waiting for target process start...");

            while (true)
            {
                if (_memoryInjector.Attach(_options.TargetProcess))
                {
                    ConsoleHelper.PrintInfo($"process [{_options.TargetProcess}] found");
                    return;
                }

                Thread.Sleep(200);
            }
        }

        private void MainLoopAsync()
        {
            while (true)
            {
                if(_memoryInjector.ProcessTerminated)
                {
                    ConsoleHelper.PrintInfo("target process has been terminated");
                    AttachProcessLoop();

                    if (!DoCustomPostAttachProcessWork())
                    {
                        ConsoleHelper.PrintTryRestart();
                    }

                    ConsoleHelper.PrintInfo("now everything is set up!");
                }

                _obsBridgeController.DoKeepAliveWork();

                DoCustomMainLoopWork();

                Thread.Sleep(_options.UpdateInterval ?? DEFAULT_UPDATE_INTERVAL);
            }

        }

        private void ObsBridgeController_ReconnectInitiated(object? sender, ReconnectInfoArgs e)
        {
            ConsoleHelper.PrintInfo($"trying to reconnect to {e.DestinationTitle}'s obs...");
        }

        private void NotifyInfo(object? sender, NotifyArgs e)
        {
            ConsoleHelper.PrintInfo($"{e.Message}");
        }

        private void NotifyError(object? sender, NotifyArgs e)
        {
            ConsoleHelper.PrintError($"{e.Message}");
        }

        protected abstract bool DoCustomInitWork();
        protected abstract bool DoCustomPostAttachProcessWork();
        protected abstract void DoCustomMainLoopWork();
    }

    class Options
    {
        public string TargetProcess { get; init; } = string.Empty;
        public ReplayType ReplayType { get; init; }
        public int? UpdateInterval { get; init; }
        public int? InstantReplayDelay { get; init; }
        public int? InstantReplayTimeout { get; init; }
    }
}
