
namespace DeathCounterNETShared
{
    internal class OBSBridgeController
    {
        List<OBSBridge> _bridgeWatchList;
        List<Task?> _taskList;

        public event EventHandler<ReconnectInfoArgs>? ReconnectInitiated;

        public OBSBridgeController()
        {
            _bridgeWatchList = new List<OBSBridge>();
            _taskList = new List<Task?>();
        }
        public void Add(OBSBridge? bridge)
        {
            if (bridge is null) { return; }

            _bridgeWatchList.Add(bridge);
            _taskList.Add(null);
        }
        public void DoKeepAliveWork()
        {
            for(int i = 0; i < _bridgeWatchList.Count; ++i)
            {
                if (_bridgeWatchList[i].IsConnected) 
                {
                    _taskList[i] = null;
                    continue; 
                }

                Task? task = _taskList[i];

                if (task is not null) { continue; }

                _taskList[i] = _bridgeWatchList[i].ConnectTillMadeItAsync();

                ReconnectInitiated?.Invoke(
                    this, 
                    new ReconnectInfoArgs 
                    { 
                        DestinationTitle = _bridgeWatchList[i].Destination 
                    });
            }
        }
    }
    internal class ReconnectInfoArgs : EventArgs
    {
        public string DestinationTitle { get; set; } = string.Empty;
    }
}
