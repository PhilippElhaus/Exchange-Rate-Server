using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using WebSocketSharp.Server;

namespace ExchangeRateServer
{
    public partial class MainWindow : Window
    {
        internal ILogger log;

        private double usage_CPU;
        private double usage_RAM;
        private readonly DateTime appStartUp;

        private readonly AutoResetEvent cmcQuery = new(true);
        private bool fixerQuery;
        private bool justAdded;
        private bool AllCurrenciesIn => Currencies.Count * (Currencies.Count - 1) == Rates.Count;

        private bool online;
        private bool isCurrentlyUpdatingActivity;

        private readonly object lock_newCurrency = new();
        private readonly object lock_history = new();
        private readonly object lock_historySend = new();
        private readonly object lock_currencyChange = new();
        private readonly object lock_currencyChange_Specific = new();

        internal WebSocketServer WSSV;
        internal ObservableCollection<ExchangeRate> Rates = new();
        internal ObservableCollection<Change> Change = new();
        internal ObservableCollection<Change> Change_Specific = new();
        internal ObservableCollection<Market> Markets_Bitfinex = new();

        internal List<(string, string, Services)> Requests = new();
        internal List<Change> Requests_Selected = new(); // WorkAround

        internal Dictionary<(string, string), List<TimeData>> History_Long = new();
        internal Dictionary<(string, string), List<TimeData>> History_Short = new();

        private TimeSpan maxAgeLongHistory = new(30, 0, 0, 0);
        private TimeSpan maxAgeShortHistory = new(1, 0, 0, 0);

        private string CMCAPIKEY;
        private string FIXERAPIKEY;
        private string WSSENDPOINT = "/rates";
        private string REFERENCECURRENCY = "EUR";

        private int WSSPORT = 222;

        private TimeSpan AGE_FIAT2FIAT_RATE = new(0, 10, 0);
        private TimeSpan AGE_MIXEDRATE_RATE = new(0, 0, 30);

        private readonly TimeSpan AGE_CUTOFF_RATE = new(0, 10, 0);
        private readonly TimeSpan AGE_CMC_CHANGE = new(1, 0, 0);
        private readonly TimeSpan AGE_FIXER_CHANGE = new(1, 0, 0, 0);

        private readonly List<string> Currencies = new() { "USD", "EUR", "BTC", "ETH" };
        private readonly List<string> Currencies_Coinbase = new();
        private readonly List<string> Currencies_Bitfinex = new();
        private List<string> Currencies_CMC_Fiat = new();
        private List<string> Currencies_CMC_Crypto = new();
        private List<string> Currencies_Fixer = new();

        private readonly System.Timers.Timer autoRefresh = new(1000) { Enabled = true, AutoReset = true };

        public MainWindow()
        {
            InitializeComponent();

            appStartUp = DateTime.Now;
            log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(new Sys_Log(this), App.flag_debug ? LogEventLevel.Debug : LogEventLevel.Information).CreateLogger();

            log.Information("Starting Up...");

            DG_Currencies.ItemsSource = Currencies;
            LV_Markets.ItemsSource = Markets_Bitfinex;

            Init_Flags();
            Init_Config();

            // Loops

            Loop_Internet();
            Loop_UptimeAndLoad();
            Loop_WSS();

            var t1 = Check_Currencies_CMC();
            var t2 = Check_Currencies_Fixer();

            Check_Currencies_Coinbase();
            Check_Currencies_Bitfinex();

            _ = Task.WhenAll(new Task[] { t1, t2 }).ContinueWith((_) =>
            {
                Loop_Query_Fixer();
                Loop_Query_CMC();

                ComboBox_ReferenceCurrency.SelectionChanged += (x, y) => ReferenceCurrency_Change(x, y);
            });

            Loop_ExchangeRate();
            Loop_Market_Bitfinex();

            // Events

            autoRefresh.Elapsed += (x, y) =>
            {
                Rates = new ObservableCollection<ExchangeRate>(Rates.OrderBy(z => z.Base).ThenBy(z => z.Quote)); ;
                Change = new ObservableCollection<Change>(Change.OrderByDescending(z => Res.FIAT.Contains(z.Base)).ThenBy(z => z.Base));

                Dispatcher.Invoke(() =>
                {
                    LV_Rates.ItemsSource = null;
                    LV_Rates.ItemsSource = Rates;

                    LV_Currencies.ItemsSource = null;
                    LV_Currencies.ItemsSource = Change;

                    LV_Requests.ItemsSource = null;
                    LV_Requests.ItemsSource = Change_Specific;

                    LV_Markets.ItemsSource = null;
                    LV_Markets.ItemsSource = Markets_Bitfinex;

                    LBL_Rates.Content = CountHistoricRate(out var count_short, out var count_long) == (0, 0)
                        ? $"Exchange Rates ({Rates.Count})"
                        : $"Exchange Rates ({Rates.Count}) | Recorded: [{count_short}|{count_long}]";

                    if (REFERENCECURRENCY != null) lbl_currencies_change.Content = $"Currencies ({Change.Count}) | Relating to ";

                    if (Change_Specific.Count > 0)
                    {
                        LBL_LV_Change_Requests.Content = $"Specific Requests ({Change_Specific.Count})";
                        LV_Requests.IsEnabled = true;
                        BTN_Specific_Requests_Pull.IsEnabled = true;
                    }
                    else
                    {
                        LBL_LV_Change_Requests.Content = $"Specific Requests";
                        LV_Requests.IsEnabled = false;
                        BTN_Specific_Requests_Pull.IsEnabled = false;
                    }

                    LBL_Markets_Bitfinex.Content = $"Bitfinex ({Markets_Bitfinex.Count})";
                    LBL_Currencies.Content = $"Currencies ({Currencies.Count})";
                });

                (int, int) CountHistoricRate(out int count_short, out int count_long)
                {
                    count_short = default;

                    foreach (var pair in History_Short.ToArray())
                    {
                        foreach (var item in pair.Value.ToArray())
                        {
                            count_short++;
                        }
                    }

                    count_long = default;

                    foreach (var pair in History_Long.ToArray())
                    {
                        foreach (var item in pair.Value.ToArray())
                        {
                            count_long++;
                        }
                    }

                    return (count_short, count_long);
                }
            };
        }

        private void Init_Flags()
        {
            if (!App.flag_log)
            {
                Dispatcher.Invoke(() =>
                {
                    Tab_Log.Visibility = Visibility.Collapsed;
                });
            }

            Dispatcher.Invoke(() =>
            {
                Title += $"ExchangeRate Server{(App.flag_debug ? " [DEBUG]" : string.Empty)}";
            });
        }

        private void Init_Config()
        {
            try
            {
                if (File.Exists(@"data\config.txt"))
                {
                    var config = File.ReadAllLines(@"data\config.txt");

                    foreach (var entry in config)
                    {
                        try
                        {
                            if (entry.StartsWith("CMCAPIKEY"))
                            {
                                CMCAPIKEY = entry.Remove(0, entry.IndexOf("=") + 1);
                            }
                            else if (entry.StartsWith("FIXERAPIKEY"))
                            {
                                FIXERAPIKEY = entry.Remove(0, entry.IndexOf("=") + 1);
                            }
                            else if (entry.StartsWith("WSSENDPOINT"))
                            {
                                WSSENDPOINT = entry.Remove(0, entry.IndexOf("=") + 1);
                            }
                            else if (entry.StartsWith("REFERENCECURRENCY"))
                            {
                                REFERENCECURRENCY = entry.Remove(0, entry.IndexOf("=") + 1);
                            }
                            else if (entry.StartsWith("WSSPORT"))
                            {
                                WSSPORT = int.Parse(entry.Remove(0, entry.IndexOf("=") + 1));
                            }
                            else if (entry.StartsWith("MAXAGEFIAT2FIATRATE"))
                            {
                                AGE_FIAT2FIAT_RATE = TimeSpan.FromSeconds(double.Parse(entry.Remove(0, entry.IndexOf("=") + 1)));
                            }
                            else if (entry.StartsWith("MAXAGEMIXEDRATE"))
                            {
                                AGE_MIXEDRATE_RATE = TimeSpan.FromSeconds(double.Parse(entry.Remove(0, entry.IndexOf("=") + 1)));
                            }
                        }
                        catch
                        {
                            log.Warning($"Unable to Parse Config entry '{entry}'");
                        }
                    }
                }
                else
                {
                    log.Warning(@"Did not find 'data\config.txt'.");
                }
            }
            catch (Exception ex)
            {
                log.Warning($"Could not read config file: {ex.Short()}.");
            }
        }

        private void Loop_Internet()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (Ext.InternetGetConnectedState(out _, 0))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                onlineIndicator.Source = Res.Green;
                                online = true;
                            });
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                onlineIndicator.Source = Res.Red;
                                online = false;

                                onlineIndicator_CMC.Source = Res.Red;
                                onlineIndicator_Bitfinex.Source = Res.Red;
                                onlineIndicator_Coinbase.Source = Res.Red;
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Internet Verification Loop Crashed: {ex.Short()}");
                    }
                    finally
                    {
                        await Task.Delay(1000);
                    }
                }
            });
        }

        private void Loop_UptimeAndLoad()
        {
            _ = Task.Run(async () =>
              {
                  PerformanceCounter cpuCounter = new("Processor", "% Processor Time", "_Total");

                  while (true)
                  {
                      try
                      {
                          using (Process proc = Process.GetCurrentProcess())
                          {
                              usage_CPU = Math.Round(cpuCounter.NextValue(), 2);
                              usage_RAM = Math.Round(proc.PrivateMemorySize64 / 1048576d, 2);

                              var txt = "App: " + (DateTime.Now - appStartUp).ToString(@"dd\ hh\:mm\:ss", new CultureInfo("de-DE")) + "\n";
                              txt += "CPU: " + usage_CPU + "%" + "\nRAM: " + usage_RAM + "MB";

                              Dispatcher.Invoke(() =>
                              {
                                  SystemInfo.Inlines.Clear();
                                  SystemInfo.Inlines.Add(new Run("System\n") { FontWeight = FontWeights.Bold });
                                  SystemInfo.Inlines.Add(new Run(txt));
                              });
                          };
                      }
                      catch (Exception ex)
                      {
                          log.Error($"UpTimeAndLoad Info Loop: {ex.Short()}");
                      }
                      finally
                      {
                          await Task.Delay(1000);
                      }
                  }
              });
        }

        private void Loop_ExchangeRate()
        {
            Init();

            _ = Task.Run(async () =>
            {
                Thread.Sleep(5000);

                while (true)
                {
                    try
                    {
                        lock (lock_history)
                        {
                            if (Directory.Exists("data") && File.Exists(@"data\historic_data"))
                            {
                                File.WriteAllText(@"data\historic_data", JsonConvert.SerializeObject(new object[2] { History_Short, History_Long }));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Writing historic data to file: {ex.Short()}");
                    }
                    finally
                    {
                        await Task.Delay(new TimeSpan(0, 1, 0));
                    }
                }
            });

            _ = Task.Run(async () =>
             {
                 await Task.Delay(5000);

                 while (Currencies_Coinbase.Count == 0 && Currencies_Bitfinex.Count == 0) { await Task.Delay(1000); }

                 while (true)
                 {
                     if (AwaitOnline(Services.Coinbase) && AwaitOnline(Services.Bitfinex))
                     {
                         try
                         {
                             foreach (var base_currency in Currencies.ToArray())
                             {
                                 if (Currencies_Coinbase.Contains(base_currency))
                                 {
                                     await Rate_Coinbase(base_currency);
                                 }
                                 else

                                 if (Currencies_Bitfinex.Contains(base_currency))
                                 {
                                     await Rate_Bitfinex(base_currency);
                                 }
                                 else
                                 {
                                     Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Not found: {base_currency}"; });
                                 }

                                 await Task.Delay(AllCurrenciesIn ? 5000 : 3000);
                             }

                             foreach (var request in Requests)
                             {
                                 if (request.Item3 == Services.Bitfinex)
                                 {
                                     await Rate_Bitfinex(request.Item1, request.Item2);
                                 }
                                 else if (request.Item3 == Services.Coinbase)
                                 {
                                     await Rate_Coinbase(request.Item1);
                                 }
                             }

                             // Check for neccessary Cut Off's of outdated Rates from previous Exchanges

                             var temp = Rates.Where(x => x.Date > DateTime.Now - AGE_CUTOFF_RATE);

                             if (!temp.OrderBy(i => i).SequenceEqual(Rates.OrderBy(i => i)))
                             {
                                 var count = Rates.Count - temp.Count();

                                 Rates = new(temp);

                                 Dispatcher.Invoke(() =>
                                 {
                                     LV_Rates.ItemsSource = null;
                                     LV_Rates.ItemsSource = Rates;
                                 });

                                 log.Information($"Cut off {count} outdated rates.");
                             }

                             Dispatcher.Invoke(() => { ExchangeRateInfo.Text = "Visited all pairs."; });
                         }
                         catch (Exception ex)
                         {
                             log.Error($"Exchange Rate Loop: {ex.Short()}");
                         }
                     }
                     else
                     {
                         await Task.Delay(5000);
                     }
                 }
             });

            void Init()
            {
                try
                {
                    lock (lock_history)
                    {
                        if (!Directory.Exists("data")) _ = Directory.CreateDirectory("data");
                        if (!File.Exists(@"data\historic_data")) using (File.Create(@"data\historic_data")) { };

                        var content = File.ReadAllText(@"data\historic_data");

                        var deser = JsonConvert.DeserializeObject<Dictionary<string, List<TimeData>>[]>(content);

                        foreach (var item in deser[0])
                        {
                            var raw = item.Key.Trim(new char[] { '(', ')' });
                            var currencies = raw.Split(',');

                            currencies[1] = currencies[1].Trim();

                            if (DateTime.Now - new TimeSpan(1, 0, 0) < item.Value[^1].Time)
                            {
                                History_Short.Add((currencies[0], currencies[1]), item.Value.Skip(Math.Max(0, item.Value.Count - 60)).ToList());
                            }
                        }

                        foreach (var item in deser[1])
                        {
                            var raw = item.Key.Trim(new char[] { '(', ')' });
                            var currencies = raw.Split(',');

                            currencies[1] = currencies[1].Trim();

                            if (DateTime.Now - new TimeSpan(7, 0, 0, 0) < item.Value[^1].Time)
                            {
                                History_Long.Add((currencies[0], currencies[1]), item.Value.Skip(Math.Max(0, item.Value.Count - 180)).ToList());
                            }
                        }

                        log.Information(History_Short.Count + History_Long.Count > 0 ? $"Loaded {History_Short.Count + History_Long.Count} sets of historic rates." : $"No historic rates found.");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Loading historic data: {ex.Short()}");
                }
            }
        }

        // Utility

        private void Activity()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (isCurrentlyUpdatingActivity) return;
                    isCurrentlyUpdatingActivity = true;

                    Dispatcher.Invoke(() => { activityIndicator.Source = Res.Yellow; });
                    await Task.Delay(15);
                    Dispatcher.Invoke(() => { activityIndicator.Source = Res.Red; });
                }
                catch (Exception ex)
                {
                    log.Error($"Error updating Activity: {ex.Short()}");
                }
                finally
                {
                    isCurrentlyUpdatingActivity = false;
                }
            });
        }

        private bool AwaitOnline(Services service, bool timeout = false)
        {
            var timeOutCounter = 0;

            while (true)
            {
                if (timeout && timeOutCounter > 30)
                {
                    return false;
                }

                if (online)
                {
                    using (Ping ping = new())
                    {
                        try
                        {
                            switch (service)
                            {
                                case Services.CMC:
                                    if (ping.Send("pro-api.coinmarketcap.com").Status == IPStatus.Success)
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_CMC.Source = Res.Green; });
                                        return true;
                                    }
                                    else
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_CMC.Source = Res.Yellow; });
                                    }
                                    break;

                                case Services.Bitfinex:
                                    if (ping.Send("api-pub.bitfinex.com").Status == IPStatus.Success)
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_Bitfinex.Source = Res.Green; });
                                        return true;
                                    }
                                    else
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_Bitfinex.Source = Res.Yellow; });
                                    }
                                    break;

                                case Services.Coinbase:
                                    if (ping.Send("api.coinbase.com").Status == IPStatus.Success)
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_Coinbase.Source = Res.Green; });
                                        return true;
                                    }
                                    else
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_Coinbase.Source = Res.Yellow; });
                                    }
                                    break;

                                case Services.Fixer:
                                    if (ping.Send("fixer.io").Status == IPStatus.Success)
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_Fixer.Source = Res.Green; });
                                        return true;
                                    }
                                    else
                                    {
                                        Dispatcher.Invoke(() => { onlineIndicator_Fixer.Source = Res.Yellow; });
                                    }
                                    break;
                            }

                            Thread.Sleep(1000);
                            timeOutCounter++;
                            continue;
                        }
                        catch
                        {
                            Dispatcher.Invoke(() =>
                            {
                                switch (service)
                                {
                                    case Services.CMC:
                                        onlineIndicator_CMC.Source = Res.Red;
                                        break;

                                    case Services.Bitfinex:
                                        onlineIndicator_Bitfinex.Source = Res.Red;
                                        break;

                                    case Services.Coinbase:
                                        onlineIndicator_Coinbase.Source = Res.Red;
                                        break;

                                    case Services.Fixer:
                                        onlineIndicator_Fixer.Source = Res.Red;
                                        break;
                                }
                            });

                            Thread.Sleep(1000);
                            timeOutCounter++;
                            continue;
                        }
                    }
                }
                else
                {
                    Thread.Sleep(1000);
                    continue;
                }
            }
        }

        private async Task<string> ReferenceCurrency_Get()
        {
            string reference = default;

            await Dispatcher.InvokeAsync(() =>
            {
                if (ComboBox_ReferenceCurrency.SelectedItem == null)
                {
                    if (string.IsNullOrEmpty(REFERENCECURRENCY)) { REFERENCECURRENCY = "EUR"; }

                    reference = REFERENCECURRENCY;
                }
                else
                {
                    reference = (string)ComboBox_ReferenceCurrency.SelectedItem;
                }
            });

            return reference;
        }

        private void ReferenceCurrency_Change(object sender, SelectionChangedEventArgs e)
        {
            Query_CMC(true);
            Query_Fixer(true);

            log.Information($"Changed Reference Currency to [{(string)ComboBox_ReferenceCurrency.SelectedItem}].");
        }

        // Interface

        private async void BTN_Click_ChangePullNow(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var item in Requests)
                {
                    await Query_CMC_Specific(item.Item1, item.Item2, true);
                }
            }
            catch (Exception ex)
            {
                log.Error($"Manual Change Pull: {ex.Short()}");
            }
        }

        internal void BTN_Click_AddCurrency(object sender, RoutedEventArgs e)
        {
            try
            {
                string ccy = default;

                Dispatcher.Invoke(() =>
                {
                    ccy = TB_CurrencyInput.Text.ToUpper();
                    ExchangeRateInfo.Text = $"Verifying addition of {ccy} ...";
                });

                _ = WSS_AddCurrency(ccy);
            }
            catch (Exception ex)
            {
                log.Error($"Error adding new Currency manually: {ex.Short()}");
            }
        }

        private void BTN_Click_DeleteSpecificRequest(object sender, RoutedEventArgs e)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (LV_Requests.SelectedItems != null)
                    {
                        foreach (var item in Requests_Selected)
                        {
                            _ = Change_Specific.Remove(item);

                            var req = Requests.FirstOrDefault(x => x.Item1 == item.Base && x.Item2 == item.Quote);

                            if (req != default)
                            {
                                _ = Requests.Remove(req);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                log.Error($"Deleting Specific Request: {ex.Short()}");
            }
        }

        private void BTN_Click_ShowLogFile(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(@"log\syslog.txt"))
                {
                    _ = new Process
                    {
                        StartInfo = new ProcessStartInfo(@"log\syslog.txt")
                        {
                            UseShellExecute = true
                        }
                    }.Start();
                }
            }
            catch (Exception ex)
            {
                log.Error($"Showing Log File: {ex.Short()}");
            }
        }

        private void AddCurrency_EnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            BTN_Click_AddCurrency(default, default);
        }

        private void LV_SpecificRequests_CM_Opening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                if (LV_Requests.SelectedItem == null)
                {
                    e.Handled = true;
                }
                else
                {
                    Requests_Selected = LV_Requests.SelectedItems.Cast<Change>().ToList();
                }
            }
            catch (Exception ex)
            {
                log.Error($"Opening Executed Trade LV CM: {ex.Short()}");
            }
        }

        private void TrayIcon_MouseDoubleClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (Visibility == Visibility.Hidden)
                {
                    Visibility = Visibility.Visible;
                    _ = Activate();
                }
                else if (Visibility == Visibility.Visible)
                {
                    Visibility = Visibility.Hidden;
                }
            });
        }

        private void BTN_RightClick_ClearLogFile(object sender, MouseButtonEventArgs e)
        {
            try
            {
                lock (Sys_Log.fileAccessLock_syslog)
                {
                    File.WriteAllText(@"log\syslog.txt", string.Empty);
                }

                SysLog_Clear(default, default);
            }
            catch (Exception ex)
            {
                log.Error($"Deleting Log File: {ex.Short()}");
            }
        }

        private void SysLog_Clear(object sender, RoutedEventArgs e) => Dispatcher.Invoke(() => { SystemLog.Text = ""; LBL_SysLog.Content = "Log"; });

        // Exit

        private void Application_Exiting(object sender, CancelEventArgs e)
        {
            if (MessageBox.Show("Terminate Application?", "Exit", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation) == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
          
        }
    }
}