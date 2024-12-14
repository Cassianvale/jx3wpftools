using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace WpfApp.Services
{
    public class ConfigService
    {
        private const string CONFIG_FILE = "config.ini";
        private const string SECTION_HOTKEYS = "Hotkeys";
        private const string SECTION_KEYLIST = "KeyList";
        private const string SECTION_SETTINGS = "Settings";

        public ConfigService()
        {
            InitializeConfigFile();
        }

        private void InitializeConfigFile()
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                // 确保目录存在
                string? directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 写入默认配置
                WritePrivateProfileString(SECTION_HOTKEYS, "StartKey", "", configPath);
                WritePrivateProfileString(SECTION_HOTKEYS, "StartModifiers", "None", configPath);
                WritePrivateProfileString(SECTION_HOTKEYS, "StopKey", "", configPath);
                WritePrivateProfileString(SECTION_HOTKEYS, "StopModifiers", "None", configPath);

                WritePrivateProfileString(SECTION_KEYLIST, "Count", "0", configPath);

                WritePrivateProfileString(SECTION_SETTINGS, "KeyMode", "0", configPath);
                WritePrivateProfileString(SECTION_SETTINGS, "Interval", "50", configPath);
                WritePrivateProfileString(SECTION_SETTINGS, "SoundEnabled", "True", configPath);

                System.Diagnostics.Debug.WriteLine($"已创建默认配置文件: {configPath}");
            }
        }

        [DllImport("kernel32")]
        private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);

        [DllImport("kernel32")]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);

        public string GetConfigFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(baseDir, CONFIG_FILE);
            return configPath;
        }

        public void SaveConfig(
            DDKeyCode? startHotkey, ModifierKeys startModifiers,
            DDKeyCode? stopHotkey, ModifierKeys stopModifiers,
            List<DDKeyCode> keyList,
            int keyMode,
            int interval,
            bool soundEnabled)
        {
            string configPath = GetConfigFilePath();
            System.Diagnostics.Debug.WriteLine($"正在保存配置到: {configPath}");
            
            // 确保目录存在
            string? directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                System.Diagnostics.Debug.WriteLine($"创建配置目录: {directory}");
            }

            // 打印保存的配置内容
            System.Diagnostics.Debug.WriteLine($"保存配置内容:");
            System.Diagnostics.Debug.WriteLine($"- 启动热键: {startHotkey}, 修饰键: {startModifiers}");
            System.Diagnostics.Debug.WriteLine($"- 停止热键: {stopHotkey}, 修饰键: {stopModifiers}");
            System.Diagnostics.Debug.WriteLine($"- 按键列表数量: {keyList.Count}");
            System.Diagnostics.Debug.WriteLine($"- 按键模式: {keyMode}");
            System.Diagnostics.Debug.WriteLine($"- 按键间隔: {interval}");
            System.Diagnostics.Debug.WriteLine($"- 声音提示: {soundEnabled}");

            // 保存热键设置
            WritePrivateProfileString(SECTION_HOTKEYS, "StartKey", startHotkey?.ToString() ?? "", configPath);
            WritePrivateProfileString(SECTION_HOTKEYS, "StartModifiers", startModifiers.ToString(), configPath);
            WritePrivateProfileString(SECTION_HOTKEYS, "StopKey", stopHotkey?.ToString() ?? "", configPath);
            WritePrivateProfileString(SECTION_HOTKEYS, "StopModifiers", stopModifiers.ToString(), configPath);

            // 保存按键列表
            WritePrivateProfileString(SECTION_KEYLIST, "Count", keyList.Count.ToString(), configPath);
            for (int i = 0; i < keyList.Count; i++)
            {
                WritePrivateProfileString(SECTION_KEYLIST, $"Key{i}", keyList[i].ToString(), configPath);
            }

            // 保存其他设置
            WritePrivateProfileString(SECTION_SETTINGS, "KeyMode", keyMode.ToString(), configPath);
            WritePrivateProfileString(SECTION_SETTINGS, "Interval", interval.ToString(), configPath);
            WritePrivateProfileString(SECTION_SETTINGS, "SoundEnabled", soundEnabled.ToString(), configPath);
        }

        public (DDKeyCode? startKey, ModifierKeys startMods, 
                DDKeyCode? stopKey, ModifierKeys stopMods,
                List<DDKeyCode> keyList,
                int keyMode, int interval, bool soundEnabled) LoadConfig()
        {
            string configPath = GetConfigFilePath();
            
            if (!File.Exists(configPath))
            {
                // 返回默认配置
                return (null, ModifierKeys.None, null, ModifierKeys.None, 
                        new List<DDKeyCode>(), 0, 50, true);
            }

            StringBuilder retVal = new StringBuilder(255);

            // 读取热键设置
            GetPrivateProfileString(SECTION_HOTKEYS, "StartKey", "", retVal, 255, configPath);
            DDKeyCode? startKey = string.IsNullOrEmpty(retVal.ToString()) ? null : 
                                 Enum.TryParse<DDKeyCode>(retVal.ToString(), out var sk) ? sk : null;

            GetPrivateProfileString(SECTION_HOTKEYS, "StartModifiers", "None", retVal, 255, configPath);
            ModifierKeys startMods = Enum.TryParse<ModifierKeys>(retVal.ToString(), out var sm) ? sm : ModifierKeys.None;

            GetPrivateProfileString(SECTION_HOTKEYS, "StopKey", "", retVal, 255, configPath);
            DDKeyCode? stopKey = string.IsNullOrEmpty(retVal.ToString()) ? null :
                                Enum.TryParse<DDKeyCode>(retVal.ToString(), out var stk) ? stk : null;

            GetPrivateProfileString(SECTION_HOTKEYS, "StopModifiers", "None", retVal, 255, configPath);
            ModifierKeys stopMods = Enum.TryParse<ModifierKeys>(retVal.ToString(), out var stm) ? stm : ModifierKeys.None;

            // 读取按键列表
            GetPrivateProfileString(SECTION_KEYLIST, "Count", "0", retVal, 255, configPath);
            int count = int.TryParse(retVal.ToString(), out var c) ? c : 0;

            List<DDKeyCode> keyList = new List<DDKeyCode>();
            for (int i = 0; i < count; i++)
            {
                GetPrivateProfileString(SECTION_KEYLIST, $"Key{i}", "", retVal, 255, configPath);
                if (Enum.TryParse<DDKeyCode>(retVal.ToString(), out var key))
                {
                    keyList.Add(key);
                }
            }

            // 读取其他设置
            GetPrivateProfileString(SECTION_SETTINGS, "KeyMode", "0", retVal, 255, configPath);
            int keyMode = int.TryParse(retVal.ToString(), out var km) ? km : 0;

            GetPrivateProfileString(SECTION_SETTINGS, "Interval", "50", retVal, 255, configPath);
            int interval = int.TryParse(retVal.ToString(), out var iv) ? iv : 50;

            GetPrivateProfileString(SECTION_SETTINGS, "SoundEnabled", "True", retVal, 255, configPath);
            bool soundEnabled = bool.TryParse(retVal.ToString(), out var se) ? se : true;

            return (startKey, startMods, stopKey, stopMods, keyList, keyMode, interval, soundEnabled);
        }
    }
} 