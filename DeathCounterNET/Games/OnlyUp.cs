using DeathCounterNETShared;
using System.Drawing;

namespace DeathCounterNET.Games
{
    internal class OnlyUpInjector : GameInjector
    {
        public OnlyUpInjector() : base(
            new Options
            {
                TargetProcess = "OnlyUP-Win64-Shipping",
                ReplayType = ReplayType.Twitch,
                UpdateInterval = 500,
                InstantReplayDelay = 5 * 1000,
                InstantReplayTimeout = 45 * 1000,
            })
        {

        }
        protected override bool DoCustomInitWork()
        {
            if (File.Exists(MAX_RES_PATH))
            {
                string savedCounterString = File.ReadAllText(MAX_RES_PATH).Trim();

                ConsoleHelper.PrintInfo("found saved best result, trying to read value...");

                if (!double.TryParse(savedCounterString, out double value))
                {
                    ConsoleHelper.PrintError("failed to read saved best result...");
                    return false;
                }

                ConsoleHelper.PrintInfo($"saved best result is [{value}]");

                PromptResult promptRes = ConsoleHelper.ShowModalPrompt(
                    new ModalPromptOptions()
                    {
                        Question = "choose whether you want to keep it",
                        YesOption = "continue with the saved value",
                        NoOption = "reset best result to 0",
                    });

                if(promptRes == PromptResult.Yes)
                {
                    maxPercentage = value;
                }
                else
                {
                    SaveBestResult();
                }
            }

            return true;
        }

        protected override void DoCustomMainLoopWork()
        {
            double value = _memoryInjector.ReadMemory<double>(_memoryInjector.BaseAddress, offsets) ?? 0;

            if (value == 0)
            {
                return;
            }
            
            double height = value - MIN_HEIGHT;

            if (prevHeight - height > EPSILON)
            {
                fallStreak += prevHeight - height;
            }
            else
            {
                //if (fallStreak != 0)   Console.WriteLine($"fallstreak = {fallStreak}");

                fallStreak = 0;
            }

            double currPercentage = GetCompletionRate(height);
            prevHeight = height;

            if(Math.Abs(currPercentage - prevPercentage) < EPSILON)
            {
                return;
            }

            if(currPercentage - maxPercentage > EPSILON) 
            {
                maxPercentage = currPercentage;
                SaveBestResult();
            }

            if (fallStreak > FALL_STREAK_REPLAY_THRESHOLD)
            {
                int secondsSinceLastThresholdBreak = (int)Math.Floor((DateTime.Now - lastFallStreakThresholdBreak).TotalSeconds);

                if (secondsSinceLastThresholdBreak > REPLAY_COOLDOWN_IN_SECONDS)
                {
                    TriggerReplay();
                    lastFallStreakThresholdBreak = DateTime.Now;
                }
            }

            if(fallStreak > FALL_STREAK_RED_THRESHOLD)
            {
                UpdateOBSPlayerHeightFallStreak(currPercentage);
            }
            else
            {
                UpdateOBSPlayerHeight(currPercentage);
            }
        }

        private double GetCompletionRate(double height)
        {
            double rate = Math.Max(height - MIN_HEIGHT, 0) / WORLD_HEIGHT;
            rate = Math.Round(rate * 100, 2);
            return Math.Min(rate, 100.00d);
        }

        private void UpdateOBSPlayerHeight(double value)
        {
            UpdatePlayerCaption(GetPlayerCaption(value));
        }
        private void UpdateOBSPlayerHeightFallStreak(double value)
        {
            UpdatePlayerCaption(GetPlayerCaption(value), Color.Tomato);
        }
        private string GetPlayerCaption(double value)
        {
            return $"{value}% ({maxPercentage}%)";
        }

        private void SaveBestResult()
        {
            File.WriteAllText(MAX_RES_PATH, maxPercentage.ToString());
        }
        protected override bool DoCustomPostAttachProcessWork()
        {
            UpdateOBSPlayerHeight(0);
            return true;
        }

        private const string MAX_RES_PATH = "max.ini";

        private const double EPSILON = 0.01d;

        private const double MIN_HEIGHT = -3605.98;
        private const double MAX_HEIGHT = 324135.27;
        private const double WORLD_HEIGHT = MAX_HEIGHT - MIN_HEIGHT;
        private const double FALL_STREAK_REPLAY_THRESHOLD = 4444;
        private const double FALL_STREAK_RED_THRESHOLD = 500;
        private const int REPLAY_COOLDOWN_IN_SECONDS = 40;

        private DateTime lastFallStreakThresholdBreak = DateTime.Now;

        private double prevHeight = 0;
        private double fallStreak = 0;

        private double prevPercentage = 0;
        private double maxPercentage = 0;


        private int[] offsets = new int[8]
        {
            0x073C5ED8,
            0x180,
            0xA0,
            0x98,
            0xA8,
            0x60,
            0x328,
            0x270
        };
    }
}
