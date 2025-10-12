using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LibreHardwareMonitor.Hardware;

namespace GGSystemMonitor
{
    internal class Program
    {
        // Configuration
        private const string APP_NAME = "CUSTOM_SYSTEM_MONITOR";
        private const string EVENT_NAME = "SYS_MONITOR";
        private const string COREPROPS_PATH = @"C:\ProgramData\SteelSeries\SteelSeries Engine 3\coreProps.json";
        private static HttpClient http = new HttpClient();
        private static Computer computer;
        private static IHardware cpuHardware;
        private static IHardware gpuHardware;
        private static string baseUrl = null;
        private static int updateValue;

        static void Main(string[] args)
        {
            try
            {
                // --- Read SteelSeries local API address from coreProps.json ---
                if (!File.Exists(COREPROPS_PATH))
                {
                    Console.WriteLine($"coreProps.json not found at {COREPROPS_PATH}. Please verify SteelSeries GG installation path.");
                    return;
                }

                string coreText = File.ReadAllText(COREPROPS_PATH);
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

                // --- Initialize LibreHardwareMonitor Computer ---
                computer = new Computer()
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

                //Console.WriteLine("GGSystemMonitor started. Updating every 2 seconds. Press Ctrl+C to stop.");

                // Main loop
                while (true)
                {
                    try
                    {
                        double? cpu = GetCpuTemperature();
                        double? gpu = GetGpuTemperature();
                        SendToOled(cpu, gpu);
                    }
                    catch (Exception ex)
                    {
                        // Log exceptions to file for troubleshooting
                        File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Error: " + ex + Environment.NewLine);
                    }
                    Thread.Sleep(2000);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText("GGSystemMonitor.log", DateTime.Now + " - Fatal: " + ex + Environment.NewLine);
            }
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
        private static void SendToOled(double? cpu, double? gpu)
        {
            if (updateValue++ == 2)
                updateValue = 0;

            var payload = new JObject(
                new JProperty("game", APP_NAME),
                new JProperty("event", EVENT_NAME),
                new JProperty("data", new JObject(
                    new JProperty("value", updateValue),
                    new JProperty("frame", new JObject(
                        new JProperty("text_line_1", cpu.HasValue ? $"CPU: {cpu.Value:F1}°C" : "CPU: N/A"),
                        new JProperty("text_line_2", gpu.HasValue ? $"GPU: {gpu.Value:F1}°C" : "GPU: N/A")
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

            // Try to read Package sensor first (average / package temp)
            var package = cpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Package");
            if (package != null && package.Value.HasValue)
                return (double) package.Value.Value;

            // Fallback: average all "Core" temp sensors
            var coreTemps = cpuHardware.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.StartsWith("Core"))
            .Select(s => s.Value)
            .Where(v => v.HasValue)
            .Select(v => (double)v.Value)
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

            // Look for a sensor named "GPU Core" or first temperature sensor
            var gpuCore = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name != null && s.Name.ToLower().Contains("gpu"));
            if (gpuCore != null && gpuCore.Value.HasValue)
                return (double) gpuCore.Value.Value;

            var firstTemp = gpuHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);
            if (firstTemp != null)
                return (double) firstTemp.Value.Value;

            return null;
        }
    }
}
