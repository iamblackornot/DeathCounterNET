
namespace DeathCounterNETShared
{
    internal static class ConsoleHelper
    {
        public static void PrintDebug(string message)
        {
            Console.WriteLine($"[DEBUG] {message}");
        }
        public static void PrintInfo(string message)
        {
            Console.WriteLine($"[INFO] {message}");
        }
        public static void PrintError(string message)
        {
            Console.WriteLine($"[ERR] {message}");
        }
        public static void PrintWrongInput()
        {
            Console.WriteLine();
            Console.WriteLine($"[WRONG_INPUT] try again: ");
        }
        public static void PrintOption(string option, string description)
        {
            Console.WriteLine($"[{option}] - {description}");
        }
        public static void PrintPrompt(string message)
        {
            Console.Write($"[PROMPT] {message}");
        }
        public static void PrintTryRestart()
        {
            PrintInfo("try restarting the game and the application");
            PrintPrompt("press any key to quit...");
            Console.ReadKey();
            Environment.Exit(0);
        }
        public static PromptResult ShowModalPrompt(ModalPromptOptions options)
        {
            ConsoleHelper.PrintPrompt($"{options.Question}:");
            Console.WriteLine();
            ConsoleHelper.PrintOption("y", $"{options.YesOption}");
            ConsoleHelper.PrintOption("n", $"{options.NoOption}");

            while (true)
            {
                char key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                switch (key)
                {
                    case 'y':
                        return PromptResult.Yes;
                    case 'n':
                        return PromptResult.No;
                    default:
                        PrintWrongInput();
                        break;
                }
            }
        }
        public static string PromptValue(string question)
        {
            while (true)
            {
                ConsoleHelper.PrintPrompt($"{question}: ");
                string? value = Console.ReadLine();

                if (!string.IsNullOrWhiteSpace(value)) { return value; }

                ConsoleHelper.PrintWrongInput();
            }
        }
        public static T PromptValue<T>(string question)
        {
            while(true)
            {
                string input = PromptValue(question);
                var converted = Convert.ChangeType(input, typeof(T));

                if(converted is T value) { return value; }

                ConsoleHelper.PrintWrongInput();
            }
        }
        public static T PromptValue<T>(string question, Func<string, T?> convertFunc)
        {
            while (true)
            {
                string input = PromptValue(question);
                var converted = convertFunc(input);

                if (converted is T value) { return value; }

                ConsoleHelper.PrintWrongInput();
            }
        }
    }

    internal class FixedLineConsoleOutput
    {
        private int? _line;
        public FixedLineConsoleOutput()
        {
            _line = Console.GetCursorPosition().Top;
        }
        public void Write(string text)
        {
            (int left, int top) = Console.GetCursorPosition();

            if(_line is null)
            {
                _line = top;
            }

            Console.SetCursorPosition(0, _line.Value);
            ConsoleHelper.PrintInfo(text);
            Console.SetCursorPosition(left, top);
        }
    }

    internal class ModalPromptOptions
    {
        public string? Question { get; set; }
        public string? YesOption { get; set; }
        public string? NoOption { get; set; }
    }

    internal enum PromptResult
    {
        Yes = 0,
        No = 1,
    }
}
