namespace DeathCounterNETShared
{
    public static class Logger
    {
        private static readonly object lcLogs = new Object();
        public static readonly string LOCAL_LOGS_FILENAME = "logs.txt";
        public static void AddToLogs(string location, string message)
        {
            try
            {
                lock (lcLogs)
                {
                    File.AppendAllText(LOCAL_LOGS_FILENAME,
                        DateTime.Now.ToShortDateString() + " " +
                        DateTime.Now.ToLongTimeString() + " " +
                        location + " : " + message + Environment.NewLine);
                }
            }
            catch{ }
        }
        public static void AddToLogs(string message)
        {
            AddToLogs(string.Empty, message);
        }
    }
}
