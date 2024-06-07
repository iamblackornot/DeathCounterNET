using DeathCounterNETShared;

namespace DeathCounterNET.Games
{
    internal class ConditionZeroInjector : GameInjector
    {
        FixedLineConsoleOutput _failOutput = new FixedLineConsoleOutput();

        public ConditionZeroInjector() : base(
            new Options()
            {
                TargetProcess = "czero",
                ReplayType = ReplayType.Twitch
            })
        { }

        protected override bool DoCustomInitWork()
        {
            if (File.Exists(COUNTER_PATH))
            {
                string savedCounterString = File.ReadAllText(COUNTER_PATH).Trim();

                ConsoleHelper.PrintInfo("found counter save, trying to read value...");

                if (!int.TryParse(savedCounterString, out int value))
                {
                    ConsoleHelper.PrintError("failed to read saved counter value...");
                    return false;
                }

                ConsoleHelper.PrintInfo($"saved counter value is [{value}]");

                PromptResult promptRes = ConsoleHelper.ShowModalPrompt(
                    new ModalPromptOptions()
                    {
                        Question = "choose whether you want to keep it",
                        YesOption = "continue with the saved value",
                        NoOption = "reset counter to 0",
                    });

                if (promptRes == PromptResult.Yes)
                {
                    failCount = value;
                }
                else
                {
                    SaveCounter();
                }
            }

            return true;
        }

        protected override void DoCustomMainLoopWork()
        {
            int? currentHp = _memoryInjector.ReadMemory<int>(healthAddress);

            if (currentHp.HasValue && currentHp != prevHp)
            {
                if (currentHp < prevHp && currentHp == 0 && !_memoryInjector.ProcessTerminated)
                {
                    Thread.Sleep(1000);

                    if (!_memoryInjector.ProcessTerminated)
                    {
                        ++failCount;

                        TriggerReplay();
                        UpdateFailCount();

                        _failOutput.Write($"you failed {failCount} times");

                        SaveCounter();
                    }
                }

                prevHp = currentHp.Value;
            }
        }
        private void UpdateFailCount()
        {
            string caption = $"{failCount} fails";
            UpdatePlayerCaption(caption);
        }
        protected override bool DoCustomPostAttachProcessWork()
        {
            ConsoleHelper.PrintInfo("waiting for game to start...");

            IntPtr clientDllAddress = WaitForDLLToBeLoaded("client.dll");
            IntPtr czDllAddress = WaitForDLLToBeLoaded("cz.dll");
            IntPtr exeAddress = _memoryInjector.BaseAddress;

            ConsoleHelper.PrintInfo("game started, trying to inject to [czero.exe] and [cz.dll]");

            healthAddress = clientDllAddress + clientDllHealthOffset;
            prevHp = _memoryInjector.ReadMemory<int>(healthAddress) ?? 100;

            bool exeWasInjected = _memoryInjector.WriteMemory(exeAddress + exeInjectOffset, exeInjectBuffer);

            if (!exeWasInjected)
            {
                ConsoleHelper.PrintError("failed to inject to [czero.exe]");
                return false;
            }

            int diff = 0x12300000 - czDllAddress.ToInt32();
            long jmpAddress = 0xEF0EFE09 + diff;

            byte[] buffer = new byte[9];
            buffer[0] = 0xE9;
            BitConverter.GetBytes(jmpAddress).CopyTo(buffer, 1);

            bool dllWasInjected = _memoryInjector.WriteMemory(czDllAddress + czDllInjectOffset, buffer);

            if (!dllWasInjected)
            {
                ConsoleHelper.PrintError("failed to inject to [cz.dll]");
                return false;
            }

            UpdateFailCount();

            return true;
        }
        private void SaveCounter()
        {
            File.WriteAllText(COUNTER_PATH, failCount.ToString());
        }

        private void _hostOBSBridge_Connected(object? sender, EventArgs e)
        {
            UpdateFailCount();
        }

        int failCount = 0;
        int prevHp = 0;
        IntPtr healthAddress = IntPtr.Zero;

        readonly int exeInjectOffset = 0x13400;
        readonly byte[] exeInjectBuffer =
        {
            0xC7, 0x05, 0x88, 0xB8, 0x11, 0x27, 0x00, 0x00, 0x00, 0x00, 0xC2, 0x10, 0x00, 0x90
        };

        readonly int clientDllHealthOffset = 0x11B888;
        readonly int czDllInjectOffset = 0x235F2;

        const string COUNTER_PATH = "counter.ini";
    }
}
