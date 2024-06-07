using Newtonsoft.Json;

namespace DeathCounterNETShared
{
    internal abstract class AppBase<TSettings> where TSettings : new()
    {
        private string _settingsFilename;         
        protected AppBase(string settingsFilePath) 
        { 
            _settingsFilename = settingsFilePath;
        }
        protected Result<TSettings> LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilename))
                {
                    return new GoodResult<TSettings>(new TSettings());
                }

                TSettings? settings = JsonConvert.DeserializeObject<TSettings>(File.ReadAllText(_settingsFilename));

                if (settings == null)
                {
                    return new BadResult<TSettings>("couldn't parse settings file");
                }

                return new GoodResult<TSettings>(settings);
            }
            catch(Exception ex)
            {
                Logger.AddToLogs(ex.ToString());    
                return new BadResult<TSettings>("failed to load settings file, look for more information in logs");
            }
        }

        protected Result SaveSettings(TSettings settings)
        {
            try
            {
                File.WriteAllText(_settingsFilename, JsonConvert.SerializeObject(settings, Formatting.Indented));
                return new Result(true);
            }
            catch (Exception ex)
            {
                Logger.AddToLogs(ex.ToString());
                return new Result(false, "failed to save settings file, look for more information in logs");
            }
        }
    }
}
