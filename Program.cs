using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using static GGSystemMonitor.Program;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace GGSystemMonitor
{
    internal class Program
    {
        // Configuration
        private const string APP_NAME = "CUSTOM_SYSTEM_MONITOR";
        private const string EVENT_NAME = "SYS_MONITOR";
        private static HttpClient http = new HttpClient();
        private static IHardware cpuHardware;
        private static IHardware gpuHardware;
        private static string baseUrl = null;
        private static int updateValue;
        private static int screenUpdate;
        private static bool gpuPasteWarning;
        private static int gpuPasteFPCounter;
        private static int textScrollPos;

        private static AppSettings settings;
        private static float cpuWarningTemperature;
        private static float cpuCriticalTemperature;
        private static float gpuWarningTemperature;
        private static float gpuCriticalTemperature;

        static void Main(string[] args)
        {
            try
            {
                // --- Initialize LibreHardwareMonitor Computer ---
                Computer computer = new Computer()
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true
                };
                computer.Open();

                // Give LHM a short moment to initialize internal polling
                Thread.Sleep(500);

                // Init the hardware for the cpu and gpu
                foreach (var hw in computer.Hardware)
                {
                    // HardwareType.ToString() contains "Cpu" for CPU entries
                    if (hw.HardwareType.ToString().ToLower().Contains("cpu"))
                    {
                        cpuHardware = hw;
                        break;
                    }
                }
                foreach (var hw in computer.Hardware)
                {
                    if (hw.HardwareType.ToString().ToLower().Contains("gpu"))
                    {
                        gpuHardware = hw;
                        break;
                    }
                }

                // --- Initialize Settings.json file ---
                settings = SettingsManager.LoadSettings();

                FileSystemWatcher watcher = new FileSystemWatcher(AppContext.BaseDirectory, "settings.json");
                watcher.Changed += (s, e) =>
                {
                    try
                    {
                        Thread.Sleep(100); // brief delay for file lock
                        settings = SettingsManager.LoadSettings();
                    }
                    catch { }
                };
                watcher.EnableRaisingEvents = true;

                // --- Read SteelSeries local API address from coreProps.json ---
                if (!File.Exists(settings.GGEngineCorePropsPath))
                {
                    Console.WriteLine($"coreProps.json not found at {settings.GGEngineCorePropsPath}. Please verify SteelSeries GG installation path.");
                    return;
                }

                string coreText = File.ReadAllText(settings.GGEngineCorePropsPath);
                try
                {
                    var j = JObject.Parse(coreText);
                    var addr = j["address"]?.ToString();
                    if (string.IsNullOrEmpty(addr))
                    {
                        Console.WriteLine("Could not find 'address' field in coreProps.json");
                        return;
                    }
                    baseUrl = "http://" + addr;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to parse coreProps.json: " + ex.Message);
                    return;
                }

                // --- Register app/events with SteelSeries GameSense ---
                RegisterApp();
                RegisterEvent();
                BindEvent();

                //Console.WriteLine("GGSystemMonitor started. Updating every 2 seconds. Press Ctrl+C to stop.");

                double? cpuTemp = 0;
                double? gpuTemp = 0;

                int temperatureUpdate = 0;

                // Main loop
                while (true)
                {
                    try
                    {
                        if (temperatureUpdate <= 0)
                        {
                            temperatureUpdate = settings.TemperatureUpdateIntervalMs;
                            cpuTemp = GetCpuTemperature();
                            gpuTemp = GetGpuTemperature();
                        }
                        temperatureUpdate -= 250;

                        if (settings.EnableGPUThermalPasteMonitoring)
                        {
                            double? hotspot = GetGpuHotSpot();
                            if (hotspot.HasValue && hotspot - gpuTemp > 15)
                            {
                                gpuPasteFPCounter++;
                            }
                            else if (gpuPasteFPCounter > 0)
                            {
                                gpuPasteFPCounter--;
                            }
                            gpuPasteWarning = gpuPasteFPCounter >= 12;
                        }

                        if (screenUpdate == 0)
                        {
                            screenUpdate = 2000;
                        }
                        SendToOled(BuildFirstLine(cpuTemp), BuildSecondLine(gpuTemp));
                        screenUpdate -= 250;
                    }
                    catch (Exception ex)
                    {
                        // Log exceptions to file for troubleshooting
                        File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Error: " + ex + Environment.NewLine);
                    }
                    Thread.Sleep(250);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Fatal: " + ex + Environment.NewLine);
            }
        }
        public class AppSettings
        {
            public string GGEngineCorePropsPath { get; set; } = @"C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json";
            public int TemperatureUpdateIntervalMs { get; set; } = 2000;
            public bool EnableCPUTemperatureWarningIndicators { get; set; } = true;
            public bool EnableGPUTemperatureWarningIndicators { get; set; } = true;
            public object WarningCPUTemperature { get; set; } = "AUTO";
            public object CriticalCPUTemperature { get; set; } = "AUTO";
            public object WarningGPUTemperature { get; set; } = "AUTO";
            public object CriticalGPUTemperature { get; set; } = "AUTO";
            public bool EnableGPUThermalPasteMonitoring { get; set; } = true;
            public bool ShowCapsLockIndicator { get; set; } = true;
        }
        public static class SettingsManager
        {
            private static readonly string SettingsFilePath =
                Path.Combine(AppContext.BaseDirectory, "settings.json");

            public static AppSettings LoadSettings()
            {
                if (!File.Exists(SettingsFilePath))
                {
                    var defaultSettings = new AppSettings();
                    string json = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(SettingsFilePath, json);
                    return defaultSettings;
                }
                string fileJson = File.ReadAllText(SettingsFilePath);
                AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(fileJson) ?? new AppSettings();
                cpuCriticalTemperature = GetSettingParse(appSettings.CriticalCPUTemperature, GetCpuMaximum(cpuHardware.Name));
                cpuWarningTemperature = GetSettingParse(appSettings.WarningCPUTemperature, cpuCriticalTemperature - 15);
                if (cpuWarningTemperature >= cpuCriticalTemperature)
                {
                    cpuWarningTemperature = cpuCriticalTemperature - 1;
                }
                gpuCriticalTemperature = GetSettingParse(appSettings.CriticalGPUTemperature, GetGpuMaximum(gpuHardware.Name));
                gpuWarningTemperature = GetSettingParse(appSettings.WarningGPUTemperature, gpuCriticalTemperature - 15);
                if (gpuWarningTemperature >= gpuCriticalTemperature)
                {
                    gpuWarningTemperature = gpuCriticalTemperature - 1;
                }
                return appSettings;
            }
        }
        private static float GetSettingParse(object setting, float defaultValue)
        {
            Console.WriteLine("setting is "+setting);
            if (setting is string s && s.ToLower().Equals("auto"))
            {
                return defaultValue;
            }
            if (setting is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number)
                    return element.GetSingle();
                if (element.ValueKind == JsonValueKind.String &&
                    float.TryParse(element.GetString(), out float val))
                    return val;
            }
            if (setting is IConvertible convertible)
            {
                try
                {
                    return Convert.ToSingle(convertible);
                }
                catch { }
            }
            return defaultValue;
        }
        private static void RegisterApp()
        {
            var meta = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("game_display_name", "Custom System Monitor"),
                new JProperty("developer", "WindowsTopGuy")
            );
            PostJson("/game_metadata", meta);
        }
        private static void RegisterEvent()
        {
            var evt = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("min_value", 0),
                new JProperty("max_value", 100),
                new JProperty("icon_id", 0),
                new JProperty("value_optional", true)
            );
            PostJson("/register_game_event", evt);
        }
        private static void BindEvent()
        {
            // Build the bind payload using JObject so we can use hyphenated property names (device-type, has-text)
            var bind = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("handlers", new JArray(
                    new JObject(
                        new JProperty("device-type", "keyboard"),
                        new JProperty("zone", "one"),
                        new JProperty("mode", "screen"),
                        new JProperty("datas", new JArray(
                            new JObject(
                                new JProperty("lines", new JArray(
                                    new JObject(new JProperty("has-text", true), new JProperty("context-frame-key", "text_line_1")),
                                    new JObject(new JProperty("has-text", true), new JProperty("context-frame-key", "text_line_2"))
                                ))
                            ))
                        )
                    ))
                )
            );
            PostJson("/bind_game_event", bind);
        }
        private static string BuildFirstLine(double? cpuTemp)
        {
            string line;
            bool capsLock = settings.ShowCapsLockIndicator && Control.IsKeyLocked(Keys.CapsLock);
            if (cpuTemp.HasValue)
            {
                line = $"CPU: {cpuTemp.Value:F1}°C";
                if (settings.EnableCPUTemperatureWarningIndicators && cpuTemp.Value >= cpuWarningTemperature)
                {
                    if (cpuTemp.Value >= cpuCriticalTemperature - ((cpuCriticalTemperature - cpuWarningTemperature) / 2.0))
                    {
                        if (screenUpdate % 500 == 0)
                        {
                            if (capsLock)
                            {
                                line = line.PadRight(12) + "🡅" + "🔥";
                            }
                            else
                            {
                                line = line.PadRight(14) + "🔥";
                            }
                        }
                        else if (capsLock)
                        {
                            line = line.PadRight(12) + "🡅";
                        }
                    }
                    else
                    {
                        switch (screenUpdate)
                        {
                            case 2000:
                            case 1750:
                            case 1000:
                            case 750:
                            case 0:
                                if (capsLock)
                                {
                                    line = line.PadRight(12) + "🡅" + "⚠";
                                }
                                else
                                {
                                    line = line.PadRight(14) + "⚠";
                                }
                                break;
                            default:
                                if (capsLock)
                                {
                                    line = line.PadRight(12) + "🡅";
                                }
                                break;
                        }
                    }
                }
                else if (capsLock)
                {
                    line = line.PadRight(14) + "🡅";
                }
            }
            else
            {
                line = "CPU: N/A";
                if (capsLock)
                {
                    line = line.PadRight(14) + "🡅";
                }
            }
            return line;
        }
        private static string BuildSecondLine(double? gpuTemp)
        {
            if (gpuPasteWarning)
            {
                string warning = "                 Check GPU Thermal Paste!";
                textScrollPos++;
                if (textScrollPos > warning.Length)
                {
                    if (textScrollPos < warning.Length + 8)
                    {
                        return GetGpuTempLine(gpuTemp);
                    }
                    else if (textScrollPos < warning.Length + 16)
                    {
                        double? hotspot = GetGpuHotSpot();
                        if (hotspot.HasValue)
                        {
                            return $"HOTSPOT: {hotspot.Value:F1}°C";
                        }
                        else
                        {
                            return $"HOT SPOT: N/A";
                        }
                    }
                    textScrollPos = 0;
                }
                return warning.Substring(textScrollPos) + warning.Substring(0, textScrollPos);
            }

            return GetGpuTempLine(gpuTemp);
        }
        private static string GetGpuTempLine(double? gpuTemp)
        {
            string line;
            if (gpuTemp.HasValue)
            {
                line = $"GPU: {gpuTemp.Value:F1}°C";
                if (settings.EnableGPUTemperatureWarningIndicators && gpuTemp.Value >= gpuWarningTemperature)
                {
                    if (gpuTemp.Value >= gpuCriticalTemperature - ((gpuCriticalTemperature - gpuWarningTemperature) / 2.0))
                    {
                        if (screenUpdate % 500 == 0)
                        {
                            line = line.PadRight(14) + "🔥";
                        }
                    }
                    else
                    {
                        switch (screenUpdate)
                        {
                            case 2000:
                            case 1750:
                            case 1000:
                            case 750:
                            case 0:
                                line = line.PadRight(14) + "⚠";
                                break;
                        }
                    }
                }
            }
            else
            {
                line = "GPU: N/A";
            }
            return line;
        }
        private static void SendToOled(string line1, string line2)
        {
            if (updateValue++ == 2)
                updateValue = 0;

            var payload = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("data", new JObject(
                    new JProperty("value", updateValue),
                    new JProperty("frame", new JObject(
                        new JProperty("text_line_1", line1),
                        new JProperty("text_line_2", line2)
                    ))
                ))
            );
            PostJson("/game_event", payload);
        }
        private static void PostJson(string path, JObject obj)
        {
            try
            {
                var url = baseUrl + path;
                var content = new StringContent(obj.ToString(Formatting.None), Encoding.UTF8, "application/json");
                var resp = http.PostAsync(url, content).Result;
                // optionally inspect resp.StatusCode or resp.Content
            }
            catch (Exception ex)
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - PostJson error: " + ex + Environment.NewLine);
            }
        }
        private static double? GetCpuTemperature()
        {
            if (cpuHardware == null)
                return null;

            cpuHardware.Update();

            // Try to read Average sensor first
            var average = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("average"));
            if (average != null && average.Value.HasValue)
                return (double) average.Value.Value;

            // Try to read Package sensor next
            var package = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("package"));
            if (package != null && package.Value.HasValue)
                return (double) package.Value.Value;

            // Fallback: average all "Core" temp sensors
            Regex coreSensorRegex = new Regex(@"core #\d+$");
            var coreTemps = cpuHardware.Sensors
                .Where(s => s.SensorType == SensorType.Temperature 
                    && s.Name != null 
                    && coreSensorRegex.IsMatch(s.Name.ToLower()))
                .Select(s => s.Value)
                .Where(v => v.HasValue)
                .Select(v => (double) v.Value)
                .ToList();

            if (coreTemps.Count > 0)
                return coreTemps.Average();

            // Last resort: first Temperature sensor with a value
            var anyTemp = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (anyTemp != null)
                return (double) anyTemp.Value.Value;

            return null;
        }
        private static double? GetGpuTemperature()
        {
            if (gpuHardware == null)
                return null;

            gpuHardware.Update();

            // Look for core sensor
            var gpuCore = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("core"));
            if (gpuCore != null && gpuCore.Value.HasValue)
                return (double) gpuCore.Value.Value;

            var firstTemp = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (firstTemp != null)
                return (double) firstTemp.Value.Value;

            return null;
        }
        private static double? GetGpuHotSpot()
        {
            if (gpuHardware == null)
                return null;

            var gpuCore = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature 
                && s.Name != null 
                && (s.Name.ToLower().Contains("hot spot") || s.Name.ToLower().Contains("hotspot")));
            if (gpuCore != null && gpuCore.Value.HasValue)
                return (double)gpuCore.Value.Value;

            return null;
        }
        private static float GetCpuMaximum(string CpuName)
        {
            if (!string.IsNullOrEmpty(CpuName))
                return 100.0f;
            Dictionary<string, float> CpuMaxTemps = new Dictionary<string, float>
            {
                // Intel CPUs
                { "Intel Core i9-13900K", 100.0f },
                { "Intel Core i7-13700K", 100.0f },
                { "Intel Core i5-13600K", 100.0f },
                { "Intel Core i9-12900K", 100.0f },
                { "Intel Core i7-12700K", 100.0f },
                { "Intel Core i5-12600K", 100.0f },
                { "Intel Core i9-11900K", 100.0f },
                { "Intel Core i7-11700K", 100.0f },
                { "Intel Core i5-11600K", 100.0f },
                { "Intel Core i9-10900K", 100.0f },
                { "Intel Core i7-10700K", 100.0f },
                { "Intel Core i5-10600K", 100.0f },
                { "Intel Core i9-9900K", 100.0f },
                { "Intel Core i7-9700K", 100.0f },
                { "Intel Core i5-9600K", 100.0f },
                { "Intel Core i7-8700K", 100.0f },
                { "Intel Core i5-8600K", 100.0f },
                { "Intel Core i7-7700K", 100.0f },
                { "Intel Core i5-7600K", 100.0f },
                { "Intel Core i7-6700K", 100.0f },
                { "Intel Core i5-6600K", 100.0f },

                // AMD CPUs
                { "AMD Ryzen 9 7950X", 95.0f },
                { "AMD Ryzen 9 7900X", 95.0f },
                { "AMD Ryzen 7 7700X", 95.0f },
                { "AMD Ryzen 7 7700", 95.0f },
                { "AMD Ryzen 5 7600X", 95.0f },
                { "AMD Ryzen 5 7600", 95.0f },
                { "AMD Ryzen 9 5950X", 90.0f },
                { "AMD Ryzen 9 5900X", 90.0f },
                { "AMD Ryzen 7 5800X", 90.0f },
                { "AMD Ryzen 7 5800X3D", 90.0f },
                { "AMD Ryzen 5 5600X", 95.0f },
                { "AMD Ryzen 5 5600", 95.0f },
                { "AMD Ryzen 7 5700X", 95.0f },
                { "AMD Ryzen 5 5500", 95.0f },
                { "AMD Ryzen 5 3600", 95.0f },
                { "AMD Ryzen 5 3600X", 95.0f },
                { "AMD Ryzen 5 3400G", 95.0f },
                { "AMD Ryzen 5 3400GE", 95.0f },
                { "AMD Ryzen 3 3300X", 95.0f },
                { "AMD Ryzen 3 3200G", 95.0f },
                { "AMD Ryzen 3 3200GE", 95.0f },
                { "AMD Ryzen 7 2700X", 85.0f },
                { "AMD Ryzen 7 2700", 85.0f },
                { "AMD Ryzen 5 2600X", 95.0f },
                { "AMD Ryzen 5 2600", 95.0f },
                { "AMD Ryzen 3 2300X", 95.0f },
                { "AMD Ryzen 3 2200G", 95.0f },
                { "AMD Ryzen 3 2200GE", 95.0f },
                { "AMD Ryzen 7 1700X", 95.0f },
                { "AMD Ryzen 7 1700", 95.0f },
                { "AMD Ryzen 5 1600X", 95.0f },
                { "AMD Ryzen 5 1600", 95.0f },
                { "AMD Ryzen 3 1300X", 95.0f },
                { "AMD Ryzen 3 1300", 95.0f },
            };

            if (CpuMaxTemps.ContainsKey(CpuName))
                return CpuMaxTemps[CpuName];

            if (CpuName.Contains("Intel"))
                return 100.0f;

            return 95.0f;
        }
        private static float GetGpuMaximum(string GpuName)
        {
            if (!string.IsNullOrEmpty(GpuName))
                return 92.0f;
            Dictionary<string, float> GpuMaxTemps = new Dictionary<string, float>
            {
                // NVIDIA GPUs
                { "NVIDIA GeForce RTX 5090", 90.0f },
                { "NVIDIA GeForce RTX 5080", 88.0f },
                { "NVIDIA GeForce RTX 5070 Ti", 88.0f },
                { "NVIDIA GeForce RTX 5070", 85.0f },

                { "NVIDIA GeForce RTX 4090", 90.0f },
                { "NVIDIA GeForce RTX 4080", 90.0f },
                { "NVIDIA GeForce RTX 4070 Ti", 90.0f },
                { "NVIDIA GeForce RTX 4070", 90.0f },
                { "NVIDIA GeForce RTX 4060 Ti", 90.0f },
                { "NVIDIA GeForce RTX 4060", 90.0f },

                { "NVIDIA GeForce RTX 3090 Ti", 92.0f },
                { "NVIDIA GeForce RTX 3090", 92.0f },
                { "NVIDIA GeForce RTX 3080 Ti", 93.0f },
                { "NVIDIA GeForce RTX 3080", 93.0f },
                { "NVIDIA GeForce RTX 3070 Ti", 93.0f },
                { "NVIDIA GeForce RTX 3070", 93.0f },
                { "NVIDIA GeForce RTX 3060 Ti", 93.0f },
                { "NVIDIA GeForce RTX 3060", 93.0f },

                { "NVIDIA GeForce GTX 1080 Ti", 94.0f },
                { "NVIDIA GeForce GTX 1080", 94.0f },
                { "NVIDIA GeForce GTX 1070 Ti", 94.0f },
                { "NVIDIA GeForce GTX 1070", 94.0f },
                { "NVIDIA GeForce GTX 1060", 94.0f },
                { "NVIDIA GeForce GTX 1050 Ti", 94.0f },
                { "NVIDIA GeForce GTX 1050", 94.0f },

                // AMD Radeon GPUs
                { "AMD Radeon RX 7900 XTX", 110.0f },
                { "AMD Radeon RX 7900 XT", 110.0f },
                { "AMD Radeon RX 7800 XT", 110.0f },
                { "AMD Radeon RX 7800", 110.0f },
                { "AMD Radeon RX 7700 XT", 110.0f },
                { "AMD Radeon RX 7700", 110.0f },
                { "AMD Radeon RX 7600 XT", 110.0f },
                { "AMD Radeon RX 7600", 110.0f },

                { "AMD Radeon RX 6950 XT", 110.0f },
                { "AMD Radeon RX 6900 XT", 110.0f },
                { "AMD Radeon RX 6800 XT", 110.0f },
                { "AMD Radeon RX 6800", 110.0f },
                { "AMD Radeon RX 6700 XT", 110.0f },
                { "AMD Radeon RX 6700", 110.0f },
                { "AMD Radeon RX 6600 XT", 110.0f },
                { "AMD Radeon RX 6600", 110.0f },
                { "AMD Radeon RX 6500 XT", 110.0f },
                { "AMD Radeon RX 6500", 110.0f },
                { "AMD Radeon RX 6400", 110.0f },

                { "AMD Radeon RX 5700 XT", 110.0f },
                { "AMD Radeon RX 5700", 110.0f },
                { "AMD Radeon RX 5600 XT", 110.0f },
                { "AMD Radeon RX 5600", 110.0f },
                { "AMD Radeon RX 5500 XT", 110.0f },
                { "AMD Radeon RX 5500", 110.0f },
                { "AMD Radeon RX 5300 XT", 110.0f },
                { "AMD Radeon RX 5300", 110.0f },
                { "AMD Radeon RX 5200", 110.0f },
            };

            if (GpuMaxTemps.ContainsKey(GpuName))
                return GpuMaxTemps[GpuName];

            if (GpuName.Contains("AMD Radeon"))
                return 110.0f;

            return 92.0f;
        }
    }
}
