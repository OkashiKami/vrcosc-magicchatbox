﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Web;
using System.Threading;
using System.Linq;
using System.ComponentModel;
using vrcosc_magicchatbox.ViewModels;
using System.Windows;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using Newtonsoft.Json.Linq;
using vrcosc_magicchatbox.DataAndSecurity;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;



namespace vrcosc_magicchatbox.Classes.Modules
{
    public partial class PulsoidModuleSettings : ObservableObject
    {
        private const string SettingsFileName = "PulsoidModuleSettings.json";

        [ObservableProperty]

        List<PulsoidTrendSymbolSet> pulsoidTrendSymbols = new();

        [ObservableProperty]
        PulsoidTrendSymbolSet selectedPulsoidTrendSymbol = new();


        public void SaveSettings()
        {
            var settingsJson = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(GetFullSettingsPath(), settingsJson);
        }

        public static string GetFullSettingsPath()
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vrcosc-MagicChatbox"), SettingsFileName);
        }

        public static PulsoidModuleSettings LoadSettings()
        {
            var settingsPath = GetFullSettingsPath();

            if (File.Exists(settingsPath))
            {
                string settingsJson = File.ReadAllText(settingsPath);

                if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.All(c => c == '\0'))
                {
                    Logging.WriteInfo("he settings JSON file is empty or corrupted.");
                    return new PulsoidModuleSettings();
                }

                try
                {
                    var settings = JsonConvert.DeserializeObject<PulsoidModuleSettings>(settingsJson);

                    if (settings != null)
                    {
                        return settings;
                    }
                    else
                    {
                        Logging.WriteInfo("Failed to deserialize the settings JSON.");
                        return new PulsoidModuleSettings();
                    }
                }
                catch (JsonException ex)
                {
                    Logging.WriteInfo($"Error parsing settings JSON: {ex.Message}");
                    return new PulsoidModuleSettings();
                }
            }
            else
            {
                Logging.WriteInfo("Settings file does not exist, returning new settings instance.");
                return new PulsoidModuleSettings();
            }
        }



    }

    public partial class PulsoidModule : ObservableObject
    {
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private readonly Queue<Tuple<DateTime, int>> _heartRates = new();
        private readonly Queue<int> _heartRateHistory = new();
        private int HeartRateFromSocket = 0;
        private System.Timers.Timer _processDataTimer;


        [ObservableProperty]
        public PulsoidModuleSettings settings;

        public PulsoidModule()
        {
            Settings = PulsoidModuleSettings.LoadSettings();
            RefreshTrendSymbols();

            _processDataTimer = new System.Timers.Timer
            {
                AutoReset = true,
                Interval = 1000
            };
            _processDataTimer.Elapsed += (sender, e) => Application.Current.Dispatcher.Invoke(ProcessData);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {

        }



        public void OnApplicationClosing()
        {
            Settings.SaveSettings();
        }

        public void RefreshTrendSymbols()
        {
            Settings.PulsoidTrendSymbols = new List<PulsoidTrendSymbolSet>
            {
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "⤴️", DownwardTrendSymbol = "⤵️" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "⬆", DownwardTrendSymbol = "⬇" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "↑", DownwardTrendSymbol = "↓" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "↗", DownwardTrendSymbol = "↘" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "🔺", DownwardTrendSymbol = "🔻" },
            };

            var symbolExists = Settings.PulsoidTrendSymbols.Any(s => s.CombinedTrendSymbol == Settings.SelectedPulsoidTrendSymbol.CombinedTrendSymbol);

            if (symbolExists)
            {
                // If the previously selected symbol exists, select it again
                Settings.SelectedPulsoidTrendSymbol = Settings.PulsoidTrendSymbols.FirstOrDefault(s => s.CombinedTrendSymbol == Settings.SelectedPulsoidTrendSymbol.CombinedTrendSymbol);
            }
            else
            {
                // If it doesn't exist or if there was no selection, default to the first symbol in the list
                Settings.SelectedPulsoidTrendSymbol = Settings.PulsoidTrendSymbols.FirstOrDefault();
            }
        }

        private static double CalculateSlope(Queue<int> values)
        {
            int count = values.Count;
            double avgX = count / 2.0;
            double avgY = values.Average();

            double sumXY = 0;
            double sumXX = 0;

            for (int i = 0; i < count; i++)
            {
                sumXY += (i - avgX) * (values.ElementAt(i) - avgY);
                sumXX += Math.Pow(i - avgX, 2);
            }

            double slope = sumXY / sumXX;
            return slope;
        }





        public static void UpdateFormattedHeartRateText()
        {
            ViewModel.Instance.FormattedLowHeartRateText = DataController.TransformToSuperscript(ViewModel.Instance.LowHeartRateText);
            ViewModel.Instance.FormattedHighHeartRateText = DataController.TransformToSuperscript(ViewModel.Instance.HighHeartRateText);
        }

        public void PropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.Instance.HeartRateScanInterval_v3))
            {
                _processDataTimer.Interval = ViewModel.Instance.HeartRateScanInterval_v3 * 1000;
                return;
            }
            if (IsRelevantPropertyChange(e.PropertyName))
            {
                if (ShouldStartMonitoring())
                {
                    StartMonitoringHeartRateAsync();
                }
                else
                {
                    StopMonitoringHeartRateAsync();
                }
            }
        }




        public bool ShouldStartMonitoring()
        {
            return ViewModel.Instance.IntgrHeartRate && ViewModel.Instance.IsVRRunning && ViewModel.Instance.IntgrHeartRate_VR ||
                   ViewModel.Instance.IntgrHeartRate && !ViewModel.Instance.IsVRRunning && ViewModel.Instance.IntgrHeartRate_DESKTOP;
        }

        public bool IsRelevantPropertyChange(string propertyName)
        {
            return propertyName == nameof(ViewModel.Instance.IntgrHeartRate) ||
                   propertyName == nameof(ViewModel.Instance.IsVRRunning) ||
                   propertyName == nameof(ViewModel.Instance.IntgrHeartRate_VR) ||
                   propertyName == nameof(ViewModel.Instance.IntgrHeartRate_DESKTOP) ||
                   propertyName == nameof(ViewModel.Instance.PulsoidAccessTokenOAuthEncrypted);
        }


        private void StopMonitoringHeartRateAsync()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait();
                _webSocket.Dispose();
                _webSocket = null;
            }

            if(_processDataTimer.Enabled)
            _processDataTimer.Stop();
        }


        private async Task ConnectToWebSocketAsync(string accessToken, CancellationToken cancellationToken)
        {
            _webSocket = new ClientWebSocket();
            _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");

            try
            {
                await _webSocket.ConnectAsync(new Uri("wss://dev.pulsoid.net/api/v1/data/real_time"), cancellationToken);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ViewModel.Instance.PulsoidAccessError = false;
                    ViewModel.Instance.PulsoidAccessErrorTxt = "";
                });

                _processDataTimer.Start();

                await HeartRateMonitoringLoopAsync(cancellationToken);
            }
            catch (WebSocketException ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ViewModel.Instance.PulsoidAccessError = true;
                    ViewModel.Instance.PulsoidAccessErrorTxt = ex.Message;
                });
                Logging.WriteException(ex, MSGBox: false);
            }
        }

        private async void StartMonitoringHeartRateAsync()
        {
            if (_cts != null) return;

            string accessToken = ViewModel.Instance.PulsoidAccessTokenOAuth;
            if (string.IsNullOrEmpty(accessToken))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ViewModel.Instance.PulsoidAccessError = true;
                    ViewModel.Instance.PulsoidAccessErrorTxt = "No Pulsoid connection found. Please connect with the Pulsoid Authentication server";
                });
                return;
            }

            bool isTokenValid = await PulsoidOAuthHandler.Instance.ValidateTokenAsync(accessToken);
            if (!isTokenValid)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ViewModel.Instance.PulsoidAccessError = true;
                    ViewModel.Instance.PulsoidAccessErrorTxt = "Invalid access token. Please reconnect with the Pulsoid Authentication server";
                });
                return;
            }

            _cts = new CancellationTokenSource();
            UpdateFormattedHeartRateText();
            await ConnectToWebSocketAsync(accessToken, _cts.Token);

        }


        private int ParseHeartRateFromMessage(string message)
        {
            try
            {
                var json = JsonConvert.DeserializeObject<dynamic>(message);
                return json.data.heart_rate;
            }
            catch (Exception ex)
            {
                Logging.WriteException(ex, MSGBox: false);
                return -1;
            }
        }

        public void ProcessData()
        {
            if(HeartRateFromSocket <= 0)
            {
                ViewModel.Instance.PulsoidDeviceOnline = false;
                return;
            }
            else
                {
                ViewModel.Instance.PulsoidDeviceOnline = true;
            }

            int heartRate = HeartRateFromSocket;


            // If SmoothHeartRate_v1 is true, calculate and use average heart rate
            if (ViewModel.Instance.SmoothHeartRate_v1)
            {
                // Record the heart rate with the current time
                _heartRates.Enqueue(new Tuple<DateTime, int>(DateTime.UtcNow, heartRate));

                // Remove old data
                while (_heartRates.Count > 0 && DateTime.UtcNow - _heartRates.Peek().Item1 > TimeSpan.FromSeconds(ViewModel.Instance.SmoothHeartRateTimeSpan))
                {
                    _heartRates.Dequeue();
                }

                // Calculate average heart rate over the defined timespan
                heartRate = (int)_heartRates.Average(t => t.Item2);
            }

            // Record the heart rate for trend analysis
            if (ViewModel.Instance.ShowHeartRateTrendIndicator)
            {
                // Only keep the last N heart rates, where N is HeartRateTrendIndicatorSampleRate
                if (_heartRateHistory.Count >= ViewModel.Instance.HeartRateTrendIndicatorSampleRate)
                {
                    _heartRateHistory.Dequeue();
                }

                _heartRateHistory.Enqueue(heartRate);

                // Update the trend indicator
                if (_heartRateHistory.Count > 1)
                {

                    double slope = CalculateSlope(_heartRateHistory);
                    if (slope > ViewModel.Instance.HeartRateTrendIndicatorSensitivity)
                    {
                        ViewModel.Instance.HeartRateTrendIndicator = Settings.SelectedPulsoidTrendSymbol.UpwardTrendSymbol;
                    }
                    else if (slope < -ViewModel.Instance.HeartRateTrendIndicatorSensitivity)
                    {
                        ViewModel.Instance.HeartRateTrendIndicator = Settings.SelectedPulsoidTrendSymbol.DownwardTrendSymbol;
                    }
                    else
                    {
                        ViewModel.Instance.HeartRateTrendIndicator = "";
                    }
                }
            }
            // Update the heart rate icon
            if (ViewModel.Instance.MagicHeartRateIcons)
            {
                // Always cycle through heart icons
                ViewModel.Instance.HeartRateIcon = ViewModel.Instance.HeartIcons[ViewModel.Instance.CurrentHeartIconIndex];
                ViewModel.Instance.CurrentHeartIconIndex = (ViewModel.Instance.CurrentHeartIconIndex + 1) % ViewModel.Instance.HeartIcons.Count;
            }
            // Append additional icons based on heart rate, if the toggle is enabled
            if (ViewModel.Instance.ShowTemperatureText)
            {
                if (heartRate < ViewModel.Instance.LowTemperatureThreshold)
                {
                    ViewModel.Instance.HeartRateIcon = ViewModel.Instance.HeartIcons[ViewModel.Instance.CurrentHeartIconIndex] + ViewModel.Instance.FormattedLowHeartRateText;
                }
                else if (heartRate >= ViewModel.Instance.HighTemperatureThreshold)
                {
                    ViewModel.Instance.HeartRateIcon = ViewModel.Instance.HeartIcons[ViewModel.Instance.CurrentHeartIconIndex] + ViewModel.Instance.FormattedHighHeartRateText;
                }
            }
            else
                ViewModel.Instance.HeartRateIcon = ViewModel.Instance.HeartIcons[ViewModel.Instance.CurrentHeartIconIndex];

            if (ViewModel.Instance.HeartRate != heartRate)
            {
                ViewModel.Instance.HeartRate = heartRate;
            }
        }

        private async Task HeartRateMonitoringLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new byte[1024];

                while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                    }
                    else
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        int heartRate = ParseHeartRateFromMessage(message);
                        Debug.WriteLine(heartRate);
                        if (heartRate != -1)
                        {
                            // Apply the adjustment if ApplyHeartRateAdjustment is true
                            if (ViewModel.Instance.ApplyHeartRateAdjustment)
                            {
                                heartRate += ViewModel.Instance.HeartRateAdjustment;
                            }

                            HeartRateFromSocket = heartRate;
                            ViewModel.Instance.HeartRateLastUpdate = DateTime.Now;
                        }
                    }
                }
            }
            catch (Exception)
            {

            }

        }


    }

    public class PulsoidTrendSymbolSet
    {
        public string UpwardTrendSymbol { get; set; } = "⤴️";
        public string DownwardTrendSymbol { get; set; } = "⤵️";

        public string CombinedTrendSymbol => $"{UpwardTrendSymbol} - {DownwardTrendSymbol}";
    }
    public class PulsoidOAuthHandler
    {
        private static readonly Lazy<PulsoidOAuthHandler> lazyInstance =
            new Lazy<PulsoidOAuthHandler>(() => new PulsoidOAuthHandler());

        public static PulsoidOAuthHandler Instance => lazyInstance.Value;

        private readonly HttpClient httpClient = new HttpClient();
        private HttpListener httpListener;
        private HttpListener secondListener;

        private PulsoidOAuthHandler() { }

        public void StartListeners()
        {
            if (httpListener == null)
            {
                httpListener = new HttpListener { Prefixes = { "http://localhost:7384/" } };
                httpListener.Start();
            }

            if (secondListener == null)
            {
                secondListener = new HttpListener { Prefixes = { "http://localhost:7385/" } };
                secondListener.Start();
            }
        }

        public void StopListeners()
        {
            httpListener?.Stop();
            httpListener = null;
            secondListener?.Stop();
            secondListener = null;
        }

        public async Task<string> AuthenticateUserAsync(string authorizationEndpoint)
        {
            string token = null;

            if (httpListener == null || secondListener == null) throw new InvalidOperationException("Listeners are not started");

            Process.Start(new ProcessStartInfo { FileName = authorizationEndpoint, UseShellExecute = true });

            // Intercept OAuth2 redirect and return HTML/JS page
            var context1 = await httpListener.GetContextAsync();
            await SendBrowserCloseResponseAsync(context1.Response);

            // Second listener to get the fragment
            var context2 = await secondListener.GetContextAsync();
            var reader = new StreamReader(context2.Request.InputStream);
            token = reader.ReadToEnd();

            return token;
        }

        public async Task<bool> ValidateTokenAsync(string accessToken)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await httpClient.GetAsync("https://dev.pulsoid.net/api/v1/token/validate");

            return response.IsSuccessStatusCode;
        }

        private async Task SendBrowserCloseResponseAsync(HttpListenerResponse response)
        {
            const string responseString = @"
    <html>
        <head>
            <script type='text/javascript'>
                var fragment = window.location.hash.substring(1);
                var xhttp = new XMLHttpRequest();
                xhttp.open('POST', 'http://localhost:7385/', true);
                xhttp.send(fragment);

                // Redirect to Pulsoid integrations page
                window.location.replace('https://pulsoid.net/ui/integrations');
            </script>
        </head>
        <body></body>
    </html>";

            var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        public static Dictionary<string, string> ParseQueryString(string queryString)
        {
            var nvc = HttpUtility.ParseQueryString(queryString);
            return nvc.AllKeys.ToDictionary(k => k, k => nvc[k]);
        }
    }


}
