﻿using Newtonsoft.Json;
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
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
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

        private double currentCPUusage;
        private double currentRAMusage;
        private DateTime appStartUp;

        private AutoResetEvent cmcQuery = new AutoResetEvent(true);
        private bool fixerQuery;
        private bool justAdded;
        private bool allCurrenciesIn => Currencies.Count * (Currencies.Count - 1) == Rates.Count ? true : false;

        private bool online;
        private bool isCurrentlyUpdatingActivity;

        private object newCurrencyLock = new object();
        private object historyLock = new object();
        private object sendHistoryLock = new object();
        private object currencyChangeLock = new object();
        private object currencyChange_SpecificLock = new object();
        
        internal WebSocketServer wssv;
        internal ObservableCollection<ExchangeRate> Rates = new ObservableCollection<ExchangeRate>();
        internal ObservableCollection<Change> CurrenciesChange = new ObservableCollection<Change>();
        internal ObservableCollection<Change> CurrenciesChange_Specific = new ObservableCollection<Change>();
        internal ObservableCollection<MarketInfo> BitfinexMarkets = new ObservableCollection<MarketInfo>();

        internal List<(string, string, Services)> SpecificRequests = new List<(string, string, Services)>();
        internal List<Change> SpecificRequests_Selected = new List<Change>(); // WorkAround
        internal Dictionary<(string, string), List<TimeData>> HistoricRatesLongTerm = new Dictionary<(string, string), List<TimeData>>();
        internal Dictionary<(string, string), List<TimeData>> HistoricRatesShortTerm = new Dictionary<(string, string), List<TimeData>>();

        private string CMCAPIKEY;
        private string FIXERAPIKEY;
        private string WSSENDPOINT = "/rates";
        private string REFERENCECURRENCY = "EUR";

        private int WSSPORT = 222;
        private TimeSpan AGE_FIAT2FIAT_RATE = new TimeSpan(0, 10, 0);
        private TimeSpan AGE_MIXEDRATE_RATE = new TimeSpan(0, 0, 30);
        private TimeSpan AGE_CUTOFF_RATE = new TimeSpan(0, 10, 0);

        private TimeSpan AGE_CMC_CHANGE = new TimeSpan(1, 0, 0);
        private TimeSpan AGE_FIXER_CHANGE = new TimeSpan(1, 0, 0, 0);

        private List<string> Currencies = new List<string>() { "USD", "EUR", "BTC", "ETH" };

        private List<string> CoinbaseCurrenices = new List<string>();
        private List<string> BitfinexCurrenices = new List<string>();
        private List<string> CMCCurrenicesFIAT = new List<string>();
        private List<string> CMCCurrenicesCrypto = new List<string>();
        private List<string> FixerCurrencies = new List<string>();

        private readonly System.Timers.Timer autoRefresh = new System.Timers.Timer(1000) { Enabled = true, AutoReset = true };

        public MainWindow()
        {
            InitializeComponent();
            
            appStartUp = DateTime.Now;
            SysWsLog syswslog = new SysWsLog(this);
            log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(syswslog, LogEventLevel.Information).CreateLogger();
            log.Information("Starting Up...");

            DGCurrencies.ItemsSource = Currencies;
            MarketsLV.ItemsSource = BitfinexMarkets;

            InitFlags();
            InitConfig();

            // Events

            autoRefresh.Elapsed += (x, y) =>
            {
                Rates = new ObservableCollection<ExchangeRate>(Rates.OrderBy(z => z.CCY1).ThenBy(z => z.CCY2)); ;
                CurrenciesChange = new ObservableCollection<Change>(CurrenciesChange.OrderByDescending(z => Res.FIAT.Contains(z.Currency)).ThenBy(z => z.Currency));

                Dispatcher.Invoke(() =>
                {
                    ExchangeRatesLV.ItemsSource = null;
                    ExchangeRatesLV.ItemsSource = Rates;

                    CurrencyLV.ItemsSource = null;
                    CurrencyLV.ItemsSource = CurrenciesChange;

                    LV_SpecificRequest.ItemsSource = null;
                    LV_SpecificRequest.ItemsSource = CurrenciesChange_Specific;

                    MarketsLV.ItemsSource = null;
                    MarketsLV.ItemsSource = BitfinexMarkets;

                    if (CountHistoricRate(out int count_short, out int count_long) == (0, 0))
                    {
                        lbl_exrates.Content = $"Exchange Rates ({Rates.Count})";
                    }
                    else
                    {
                        lbl_exrates.Content = $"Exchange Rates ({Rates.Count}) | Recorded: [{count_short}|{count_long}]";
                    }

                    if (REFERENCECURRENCY != null) lbl_currencies_change.Content = $"Currencies ({CurrenciesChange.Count}) | Relating to ";

                    if (CurrenciesChange_Specific.Count > 0)
                    {
                        LBL_LV_Change_SpecificRequests.Content = $"Specific Requests ({CurrenciesChange_Specific.Count})";
                        LV_SpecificRequest.IsEnabled = true;
                    }
                    else
                    {
                        LBL_LV_Change_SpecificRequests.Content = $"Specific Requests";
                        LV_SpecificRequest.IsEnabled = false;
                    }

                    lbl_markets_BitfinexCOM.Content = $"Bitfinex ({BitfinexMarkets.Count})";

                    lbl_currencies.Content = $"Currencies ({Currencies.Count})";
                });

                (int, int) CountHistoricRate(out int count_short, out int count_long)
                {
                    count_short = default;

                    foreach (var pair in HistoricRatesShortTerm.ToArray())
                    {
                        foreach (var item in pair.Value.ToArray())
                        {
                            count_short++;
                        }
                    }

                    count_long = default;

                    foreach (var pair in HistoricRatesLongTerm.ToArray())
                    {
                        foreach (var item in pair.Value.ToArray())
                        {
                            count_long++;
                        }
                    }

                    return (count_short, count_long);
                }
            };

            // Loops

            Loop_Internet();
            Loop_UptimeAndLoad();
            Loop_WebSocketServer();

            var t1 = CheckCMCCurrencies();
            var t2 = CheckFixerCurrencies();

            CheckCoinbaseCurrencies();
            CheckBitfinexCurrencies();

            Task.WhenAll(new Task[] { t1, t2 }).ContinueWith((_) =>
            {
                Loop_FixerQuery();
                Loop_CMCQuery();

                ComboBox_ReferenceCurrency.SelectionChanged += (x, y) => ChangeInReferenceCurrency(x, y);
            });

            Loop_ExchangeRate();
            Loop_Bitfinex_MarketInfo();
        }

        private void InitFlags()
        {
            if (!App.flag_log)
            {
                Dispatcher.Invoke(() =>
                {
                    Tab_Log.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void InitConfig()
        {
            try
            {
                if (File.Exists("config.txt"))
                {
                    var config = File.ReadAllLines("config.txt");

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
                            log.Information($"Unable to Parse Config entry '{entry}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warning($"Could not read config file: {ex.Message} {ex}.");
            }
        }

        private void Loop_Internet()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (Ext.InternetGetConnectedState(out _, 0))
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (wssv != null && wssv.IsListening)
                                {
                                    TaskBarIcon.Icon = Res.On;
                                }
                                else
                                {
                                    TaskBarIcon.Icon = Res.Off;
                                }

                                onlineIndicator.Source = Res.Green;
                                online = true;
                            });
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (wssv != null && wssv.IsListening)
                                {
                                    TaskBarIcon.Icon = Res.Connected;
                                }
                                else
                                {
                                    TaskBarIcon.Icon = Res.Off;
                                }

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
                        log.Error($"Internet Verification Loop Crashed: {ex.Message} {ex}");
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
            Task.Run(async () =>
            {
                PerformanceCounter cpuCounter;
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");

                while (true)
                {
                    try
                    {
                        using (Process proc = Process.GetCurrentProcess())
                        {
                            currentCPUusage = Math.Round(cpuCounter.NextValue(), 2);
                            currentRAMusage = Math.Round(proc.PrivateMemorySize64 / 1048576d, 2);

                            string txt = "App: " + (DateTime.Now - appStartUp).ToString(@"dd\ hh\:mm\:ss", new CultureInfo("de-DE")) + "\n";
                            txt += "CPU: " + currentCPUusage + "%" + "\nRAM: " + currentRAMusage + "MB";

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
                        log.Error($"UpTimeAndLoad Info Loop Error: {ex.Short()}");
                    }
                    finally
                    {
                        await Task.Delay(1000);
                    }
                }
            });
        }

        // Exchange Rate

        private void Loop_ExchangeRate()
        {
            Init();

            Task.Run(async () =>
            {
                Thread.Sleep(5000);

                while (true)
                {
                    try
                    {
                        lock (historyLock)
                        {
                            File.WriteAllText("historic_data", JsonConvert.SerializeObject(new object[2] { HistoricRatesShortTerm, HistoricRatesLongTerm }));
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Information($"Error writing historic data to file: {ex.Short()}");
                    }
                    finally
                    {
                        await Task.Delay(new TimeSpan(0, 1, 0));
                    }
                }
            });

            Task.Run(async () =>
            {
                Thread.Sleep(5000);

                while (CoinbaseCurrenices.Count == 0 && BitfinexCurrenices.Count == 0) { await Task.Delay(1000); }

                while (true)
                {
                    if (AwaitOnline(Services.Coinbase) && AwaitOnline(Services.Bitfinex))
                    {
                        try
                        {
                            foreach (var base_currency in Currencies.ToArray())
                            {
                                if (CoinbaseCurrenices.Contains(base_currency))
                                {
                                    await ExchangeRate_Coinbase(base_currency);
                                }
                                else

                                if (BitfinexCurrenices.Contains(base_currency))
                                {
                                    await ExchangeRate_Bitfinex(base_currency);
                                }
                                else Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Not found: {base_currency}"; });

                                await Task.Delay(allCurrenciesIn ? 5000 : 3000);
                            }

                            foreach (var request in SpecificRequests)
                            {
                                if (request.Item3 == Services.Bitfinex)
                                {
                                    await ExchangeRate_Bitfinex(request.Item1, request.Item2);
                                }
                                else if (request.Item3 == Services.Coinbase)
                                {
                                    await ExchangeRate_Coinbase(request.Item1);
                                }
                            }

                            // Check for neccessary Cut Off's of outdated Rates from previous Exchanges

                            var temp = Rates.Where(x => x.Date > DateTime.Now - AGE_CUTOFF_RATE);

                            if (!temp.OrderBy(i => i).SequenceEqual(Rates.OrderBy(i => i)))
                            {
                                int count = Rates.Count() - temp.Count();

                                Rates = new ObservableCollection<ExchangeRate>(temp);

                                Dispatcher.Invoke(() =>
                                {
                                    ExchangeRatesLV.ItemsSource = null;
                                    ExchangeRatesLV.ItemsSource = Rates;
                                });

                                log.Information($"Cut off {count} outdated rates.");
                            }

                            Dispatcher.Invoke(() => { ExchangeRateInfo.Text = "Visited all pairs."; });
                        }
                        catch (Exception ex)
                        {
                            log.Error($"Error in Exchange Rate Loop: {ex.Short()}");
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
                    lock (historyLock)
                    {
                        if (File.Exists("historic_data"))
                        {
                            var content = File.ReadAllText("historic_data");

                            var deser = JsonConvert.DeserializeObject<Dictionary<string, List<TimeData>>[]>(content);

                            foreach (var item in deser[0])
                            {
                                var raw = item.Key.Trim(new char[] { '(', ')' });
                                var currencies = raw.Split(',');

                                currencies[1] = currencies[1].Trim();

                                if (DateTime.Now - new TimeSpan(1, 0, 0) < item.Value[item.Value.Count - 1].Time)
                                {
                                    HistoricRatesShortTerm.Add((currencies[0], currencies[1]), item.Value.Skip(Math.Max(0, item.Value.Count() - 60)).ToList());
                                }
                            }

                            foreach (var item in deser[1])
                            {
                                var raw = item.Key.Trim(new char[] { '(', ')' });
                                var currencies = raw.Split(',');

                                currencies[1] = currencies[1].Trim();

                                if (DateTime.Now - new TimeSpan(7, 0, 0, 0) < item.Value[item.Value.Count - 1].Time)
                                {
                                    HistoricRatesLongTerm.Add((currencies[0], currencies[1]), item.Value.Skip(Math.Max(0, item.Value.Count() - 180)).ToList());
                                }
                            }

                            log.Information(HistoricRatesShortTerm.Count + HistoricRatesLongTerm.Count > 0 ? $"Loaded {HistoricRatesShortTerm.Count + HistoricRatesLongTerm.Count} sets of historic rates." : $"No historic rates found.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Information($"Error loading historic data: {ex.Short()}");
                }
            }
        }

        private async Task ExchangeRate_Coinbase(string base_currency)
        {
            try
            {
                TimeSpan maxAge = default;
                if (AGE_FIAT2FIAT_RATE > AGE_MIXEDRATE_RATE) maxAge = AGE_MIXEDRATE_RATE;
                else maxAge = AGE_FIAT2FIAT_RATE;

                if (!Rates.Where(x => x.CCY1 == base_currency).Any() || !Rates.Where(x => x.CCY2 == base_currency).Any() || Rates.Where(x => (x.CCY1 == base_currency || x.CCY2 == base_currency) && ((DateTime.Now - x.Date) > maxAge)).Any())
                {
                    try
                    {
                        Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Checking [{base_currency}]..."; });

                        using (WebClient webClient = new WebClient())
                        {
                            string json = default;

                            try
                            {
                                json = webClient.DownloadString($"https://api.coinbase.com/v2/exchange-rates?currency={base_currency}");
                            }
                            catch
                            {
                                if (BitfinexCurrenices.Contains(base_currency))
                                {
                                    await ExchangeRate_Bitfinex(base_currency);
                                    return;
                                }
                            }

                            var deserialized = JsonConvert.DeserializeObject<Coinbase_JSON>(json);
                            var cur = Currencies.ToArray();

                            foreach (var quote_currency in cur)
                            {
                                if (quote_currency != base_currency)
                                {
                                    if (deserialized.data.Rates.ContainsKey(quote_currency))
                                    {
                                        if (SpecificRequests.FirstOrDefault(x => x.Item1 == base_currency && x.Item2 == quote_currency && x.Item3 != Services.Coinbase) != default)
                                        {
                                            continue;
                                        }

                                        if (Rates.ToList().Exists(x => x.CCY1 == base_currency && x.CCY2 == quote_currency)) // Update
                                        {
                                            var exrEntry = Rates.FirstOrDefault(x => x.CCY1 == base_currency && x.CCY2 == quote_currency && x.Exchange == Services.Coinbase);

                                            if (exrEntry != default)
                                            {
                                                exrEntry.Date = DateTime.Now;
                                                exrEntry.Exchange = Services.Coinbase;

                                                if (Res.FIAT.Contains(quote_currency) && !Res.FIAT.Contains(base_currency))
                                                {
                                                    exrEntry.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2);
                                                }
                                                else
                                                {
                                                    exrEntry.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);
                                                }

                                                Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Checking [{base_currency}]... updated."; });

                                                if (DateTime.Now - new TimeSpan(1, 0, 0) > HistoricRatesLongTerm[(base_currency, quote_currency)].Last().Time)
                                                {
                                                    lock (historyLock)
                                                    {
                                                        HistoricRatesLongTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)exrEntry.Rate });

                                                        if ((DateTime.Now - new TimeSpan(7, 0, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)][0].Time)
                                                        {
                                                            HistoricRatesLongTerm[(base_currency, quote_currency)].RemoveAt(0);
                                                        }
                                                    }
                                                }

                                                if (DateTime.Now - new TimeSpan(0, 1, 0) > HistoricRatesShortTerm[(base_currency, quote_currency)].Last().Time)
                                                {
                                                    lock (historyLock)
                                                    {
                                                        HistoricRatesShortTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)exrEntry.Rate });

                                                        if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency)][0].Time)
                                                        {
                                                            HistoricRatesShortTerm[(base_currency, quote_currency)].RemoveAt(0);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else // New
                                        {
                                            var exr = new ExchangeRate()
                                            {
                                                Exchange = Services.Coinbase,
                                                CCY1 = base_currency,
                                                CCY2 = quote_currency,
                                                Date = DateTime.Now,
                                            };

                                            if (Res.FIAT.Contains(quote_currency) && !Res.FIAT.Contains(base_currency))
                                            {
                                                exr.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2);
                                            }
                                            else
                                            {
                                                exr.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);
                                            }

                                            Dispatcher.Invoke(() =>
                                            {
                                                Rates.Add(exr);
                                            });

                                            log.Information($"New Pair: [{exr.CCY1}/{exr.CCY2}] via Coinbase");

                                            if (!HistoricRatesLongTerm.ContainsKey((base_currency, quote_currency)))
                                            {
                                                lock (historyLock)
                                                {
                                                    HistoricRatesLongTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)exr.Rate } });
                                                }
                                            }

                                            if (!HistoricRatesShortTerm.ContainsKey((base_currency, quote_currency)))
                                            {
                                                lock (historyLock)
                                                {
                                                    HistoricRatesShortTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)exr.Rate } });
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        await ExchangeRate_Bitfinex(base_currency, quote_currency);
                                    }
                                }
                            }
                        }
                    }
                    catch (FormatException ex)
                    {
                        log.Information($"Format Exception: {Ext.Short(ex)}");
                    }
                    catch (Exception ex)
                    {
                        log.Information($"Error: {ex.Short()}");

                        return;
                    }
                }
            }
            catch (Exception ex) when (ex.Message.Contains("429"))
            {
                log.Error("Too many requests (Coinbase). Throttling...");
                await Task.Delay(60000);
            }
            catch (Exception ex)
            {
                log.Error($"Error querying Exchange Rate (Coinbase): {ex.Short()}");
            }
        }

        private async Task ExchangeRate_Bitfinex(string base_currency, string quote_currency = default)
        {
            await Task.Run(async () =>
            {
                try
                {
                    if (quote_currency == default)
                    {
                        foreach (var quote_currency_ in Currencies.ToArray())
                        {
                            if (base_currency != quote_currency_)
                            {
                                ExchangeRate rate = default;
                                Dispatcher.Invoke(() =>
                                {
                                    rate = Rates.Where(x => x.CCY1 == base_currency && x.CCY2 == quote_currency_ && x.Exchange == Services.Bitfinex).FirstOrDefault();
                                });

                                if (rate != default) // Update
                                {
                                    TimeSpan maxAge = default;
                                    if (Res.FIAT.Contains(base_currency) && Res.FIAT.Contains(quote_currency_)) maxAge = AGE_FIAT2FIAT_RATE;
                                    else maxAge = AGE_MIXEDRATE_RATE;

                                    if ((DateTime.Now - rate.Date) > maxAge)
                                    {
                                        var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency_);

                                        if (res != 0)
                                        {
                                            rate.Rate = res;
                                            rate.Date = DateTime.Now;
                                            rate.Exchange = Services.Bitfinex;

                                            Dispatcher.Invoke(() =>
                                            {
                                                ExchangeRateInfo.Text = $"Rate Updated: [{base_currency}/{quote_currency_}] ({res})";
                                            });

                                            if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency_)].Last().Time)
                                            {
                                                lock (historyLock)
                                                {
                                                    HistoricRatesLongTerm[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                                    if ((DateTime.Now - new TimeSpan(7, 0, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency_)][0].Time)
                                                    {
                                                        HistoricRatesLongTerm[(base_currency, quote_currency_)].RemoveAt(0);
                                                    }
                                                }
                                            }

                                            if ((DateTime.Now - new TimeSpan(0, 1, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency_)].Last().Time)
                                            {
                                                lock (historyLock)
                                                {
                                                    HistoricRatesShortTerm[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                                    if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency_)][0].Time)
                                                    {
                                                        HistoricRatesShortTerm[(base_currency, quote_currency_)].RemoveAt(0);
                                                    }
                                                }
                                            }
                                        }
                                        else continue;
                                    }
                                }
                                else // Add New
                                {
                                    var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency_);

                                    if (res != 0)
                                    {
                                        Dispatcher.Invoke(() =>
                                        {
                                            Rates.Add(new ExchangeRate()
                                            {
                                                Exchange = Services.Bitfinex,
                                                CCY1 = base_currency,
                                                CCY2 = quote_currency_,
                                                Date = DateTime.Now,
                                                Rate = res
                                            });
                                        });

                                        log.Information($"New Pair: [{base_currency}/{quote_currency_}] via Bitfinex");

                                        // Check for Pre-Created Historic Entries

                                        lock (historyLock)
                                        {
                                            if (HistoricRatesShortTerm.Where(x => x.Key == (base_currency, quote_currency_)).Count() == 0) HistoricRatesShortTerm.Add((base_currency, quote_currency_), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                            else HistoricRatesShortTerm[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                            if (HistoricRatesLongTerm.Where(x => x.Key == (base_currency, quote_currency_)).Count() == 0) HistoricRatesLongTerm.Add((base_currency, quote_currency_), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                            else HistoricRatesLongTerm[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });
                                        }
                                    }
                                }

                                await Task.Delay(3000);
                            }
                        }
                    }
                    else
                    {
                        ExchangeRate rate = default;
                        Dispatcher.Invoke(() =>
                        {
                            rate = Rates.FirstOrDefault(x => x.CCY1 == base_currency && x.CCY2 == quote_currency && x.Exchange == Services.Bitfinex);
                        });

                        if (rate != default) // Update
                        {
                            TimeSpan maxAge = default;
                            if (Res.FIAT.Contains(base_currency) && Res.FIAT.Contains(quote_currency)) maxAge = AGE_FIAT2FIAT_RATE;
                            else maxAge = AGE_MIXEDRATE_RATE;

                            if ((DateTime.Now - rate.Date) > maxAge)
                            {
                                var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency);

                                if (res != 0)
                                {
                                    rate.Exchange = Services.Bitfinex;
                                    rate.Rate = res;
                                    rate.Date = DateTime.Now;

                                    Dispatcher.Invoke(() =>
                                    {
                                        ExchangeRateInfo.Text = $"Rate Updated: [{base_currency}/{quote_currency}] ({res})";
                                    });

                                    if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)].Last().Time)
                                    {
                                        lock (historyLock)
                                        {
                                            HistoricRatesLongTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                            if ((DateTime.Now - new TimeSpan(7, 0, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)][0].Time)
                                            {
                                                HistoricRatesLongTerm[(base_currency, quote_currency)].RemoveAt(0);
                                            }
                                        }
                                    }

                                    if ((DateTime.Now - new TimeSpan(0, 1, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency)].Last().Time)
                                    {
                                        lock (historyLock)
                                        {
                                            HistoricRatesShortTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                            if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency)][0].Time)
                                            {
                                                HistoricRatesShortTerm[(base_currency, quote_currency)].RemoveAt(0);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else // Add New
                        {
                            var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency);

                            if (res != 0)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    Rates.Add(new ExchangeRate()
                                    {
                                        Exchange = Services.Bitfinex,
                                        CCY1 = base_currency,
                                        CCY2 = quote_currency,
                                        Date = DateTime.Now,
                                        Rate = res
                                    });
                                });

                                log.Information($"New Pair: [{base_currency}/{quote_currency}] via Bitfinex");

                                // Check for Pre-Created Historic Entries

                                lock (historyLock)
                                {
                                    if (HistoricRatesShortTerm.Where(x => x.Key == (base_currency, quote_currency)).Count() == 0) HistoricRatesShortTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                    else HistoricRatesShortTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                    if (HistoricRatesLongTerm.Where(x => x.Key == (base_currency, quote_currency)).Count() == 0) HistoricRatesLongTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                    else HistoricRatesLongTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Error querying Exchange Rate (Bitfinex): {ex.Message}");
                }
            });

            decimal Bitfinex_ExchangeRate_Query(string ccy1, string ccy2)
            {
                Activity();

                try
                {
                    ccy1 = ccy1.ToUpper();
                    ccy2 = ccy2.ToUpper();

                    decimal result = default;

                    HttpWebRequest webRequest = WebRequest.Create("https://api.bitfinex.com/v2/calc/fx") as HttpWebRequest;

                    string body = "{ \"ccy1\": " + $"\"{ccy1}\", \"ccy2\":" + $" \"{ccy2}\"" + "}";
                    var bodyBytes = Encoding.UTF8.GetBytes(body);

                    webRequest.Method = "POST";
                    webRequest.Timeout = 5000;
                    webRequest.ContentLength = bodyBytes.Length;
                    webRequest.ContentType = "application/json";

                    webRequest.GetRequestStream().Write(bodyBytes, 0, bodyBytes.Length);

                    HttpWebResponse resp = webRequest.GetResponse() as HttpWebResponse;

                    using (StreamReader sRead = new StreamReader(resp.GetResponseStream()))
                    {
                        if (Res.FIAT.Contains(ccy2) && !Res.FIAT.Contains(ccy1))
                        {
                            result = Ext.TruncateDecimal(decimal.Parse(sRead.ReadToEnd().Trim('[').Trim(']'), NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2);
                        }
                        else
                        {
                            result = Ext.TruncateDecimal(decimal.Parse(sRead.ReadToEnd().Trim('[').Trim(']'), NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);
                        }

                        sRead.Close();

                        return result;
                    };
                }
                catch (Exception ex) when (ex.Message.Contains("500") || ex.Message.Contains("The underlying connection was closed"))
                {
                    Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Not available: [{ccy1}/{ccy2}]"; });

                    return default;
                }
                catch (Exception ex) when (ex.Message.Contains("408") || ex.Message.Contains("The operation has timed out"))
                {
                    Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Timeout: [{ccy1}/{ccy2}]"; });
                    return default;
                }
                catch (Exception ex) when (ex.Message.Contains("502") || ex.Message.Contains("Bad Gateway"))
                {
                    Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Bad Gateway: [{ccy1}/{ccy2}]"; });
                    return default;
                }
                catch (Exception ex) when (ex.Message.Contains("The remote name could not be resolved"))
                {
                    Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"DNS resolving DNS: [{ccy1}/{ccy2}]"; });
                    return default;
                }
                catch (Exception ex)
                {
                    log.Error($"Error: [{ccy1}/{ccy2}] {ex.Message}");
                    return default;
                }
            }
        }

        // CMC API

        private void Loop_CMCQuery()
        {
            if (string.IsNullOrEmpty(CMCAPIKEY))
            {
                log.Information("No CMC API Key provided. Crypto currency change data will not be displayed.");

                return;
            }

            Task.Run(async () =>
            {
                await Task.Delay(5000);

                while (true)
                {
                    try
                    {
                        CMC_Query();

                        foreach (var request in SpecificRequests)  // Request for Change 1h/24h/7d/30d
                        {
                            string reference = default;

                            Dispatcher.Invoke(() =>
                            {
                                reference = ComboBox_ReferenceCurrency.SelectedItem as string ?? "";
                            });

                            if (!string.IsNullOrEmpty(reference))
                            {
                                if (!Currencies.Any(x => x == request.Item1 && reference == request.Item2))
                                {
                                    await CMC_Query_SpecificPair(request.Item1, request.Item2);
                                }
                            }
                        }
                    }
                    finally
                    {
                        await Task.Delay(AGE_CMC_CHANGE);
                    }
                }
            });
        }

        private async void CMC_Query(bool reference_change = false)
        {
            if (string.IsNullOrEmpty(CMCAPIKEY)) return;

            await Task.Run(() => { cmcQuery.WaitOne(2500); });

            await Task.Run(async () =>
            {
                try
                {
                    AwaitOnline(Services.CMC);

                    Activity();

                    string reference = await GetReferenceCurrency();

                    using (var client = new WebClient())
                    {
                        client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                        client.Headers.Add("Accepts", "application/json");

                        var currencies = Currencies.ToArray().Where(x => CMCCurrenicesCrypto.Contains(x));

                        if (!reference_change)
                        {
                            var remove = CurrenciesChange.Where(x => DateTime.Now - x.Date < new TimeSpan(0, 5, 0));

                            if (remove.Count() > 0)
                            {
                                currencies = currencies.Where(x => !remove.Any(y => y.Currency == x));
                            }
                        }

                        var symbols = string.Join(",", currencies);
                        if (string.IsNullOrEmpty(symbols)) return;

                        string url = $"https://pro-api.coinmarketcap.com/v1/cryptocurrency/quotes/latest?symbol={symbols}&convert={reference}";
                        var json = client.DownloadString(url);

                        json = json.Replace("\"quote\":{\"" + $"{reference}" + "\"", "\"quote\":{\"Currency\"");

                        CMC_Change_JSON cmc = JsonConvert.DeserializeObject<CMC_Change_JSON>(json);

                        Dispatcher.Invoke(() =>
                        {
                            foreach (var item in cmc.data)
                            {
                                var temp = new Change()
                                {
                                    Reference = reference,
                                    Currency = item.Value.symbol,
                                    Change1h = Math.Round(item.Value.quote.currency.percent_change_1h, 2),
                                    Change24h = Math.Round(item.Value.quote.currency.percent_change_24h, 2),
                                    Change7d = Math.Round(item.Value.quote.currency.percent_change_7d, 2),
                                    Change30d = Math.Round(item.Value.quote.currency.percent_change_30d, 2),
                                    Date = DateTime.Now
                                };

                                var entry = CurrenciesChange.Where(x => x.Currency == temp.Currency).SingleOrDefault();

                                lock (currencyChangeLock)
                                {
                                    if (entry == default)
                                    {
                                        CurrenciesChange.Add(temp);
                                    }
                                    else
                                    {
                                        CurrenciesChange.Remove(entry);

                                        CurrenciesChange.Add(temp);
                                    }
                                }
                            }
                        });

                        if (reference_change) log.Information($"CMC: Added {cmc.data.Count()} currencies for new reference currency [{reference}].");
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("408"))
                {
                    log.Error($"Error in CMC Query: General Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("504"))
                {
                    log.Error($"Error in CMC Query: Gateway Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("404"))
                {
                    log.Error("Error in CMC Query: Not found");
                }
                catch (Exception ex) when (ex.Message.Contains("500"))
                {
                    log.Error("Error in CMC Query: Internal Server Error");
                }
                catch (Exception ex)
                {
                    log.Error($"Error in CMC Query: {ex}");
                }
            });

            cmcQuery.Set();
        }

        private Task CMC_Query_SpecificPair(string baseCurrency, string quoteCurrency, bool manual = false)
        {
            return Task.Run(async () =>
            {
                await Task.Run(() => { cmcQuery.WaitOne(2500); });

                try
                {
                    AwaitOnline(Services.CMC);

                    Activity();

                    var entry = CurrenciesChange_Specific.FirstOrDefault(x => x.Currency == baseCurrency && x.Reference == quoteCurrency);

                    if (entry != default && (DateTime.Now - AGE_CMC_CHANGE < entry.Date) && manual == false) return;

                    using (var client = new WebClient())
                    {
                        client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                        client.Headers.Add("Accepts", "application/json");

                        var url = $"https://pro-api.coinmarketcap.com/v1/cryptocurrency/quotes/latest?symbol={baseCurrency}&convert={quoteCurrency}&aux=volume_7d,volume_30d";
                        var json = client.DownloadString(url);

                        json = json.Replace("\"quote\":{\"" + $"{quoteCurrency}" + "\"", "\"quote\":{\"Currency\"");

                        CMC_Change_JSON cmc = JsonConvert.DeserializeObject<CMC_Change_JSON>(json);

                        var temp = new Change()
                        {
                            Reference = quoteCurrency,
                            Currency = baseCurrency,
                            Change1h = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_1h, 2),
                            Change24h = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_24h, 2),
                            Change7d = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_7d, 2),
                            Change30d = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_30d, 2),
                            Date = DateTime.Now
                        };

                        lock (currencyChange_SpecificLock)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (entry == default)
                                {
                                    CurrenciesChange_Specific.Add(temp);
                                }
                                else
                                {
                                    CurrenciesChange_Specific.Remove(entry);

                                    CurrenciesChange_Specific.Add(temp);
                                }
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("408"))
                {
                    log.Error($"Error in Specific CMC Query: General Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("504"))
                {
                    log.Error($"Error in Specific CMC Query: Gateway Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("404"))
                {
                    log.Error("Error in Specific CMC Query: Not found");
                }
                catch (Exception ex) when (ex.Message.Contains("500"))
                {
                    log.Error("Error in Specific CMC Query: Internal Server Error");
                }
                catch (Exception ex) when (ex.Message.Contains("400"))
                {
                    log.Error($"[{baseCurrency}/{quoteCurrency}] Changes unavailable at CMC.");
                }
                catch (Exception ex)
                {
                    log.Error($"Error in Specific CMC Query: {ex.Short()}");
                }

                cmcQuery.Set();
            });
        }

        // Fixer API

        private void Loop_FixerQuery()
        {
            if (string.IsNullOrEmpty(FIXERAPIKEY))
            {
                log.Information("No Fixer.io API Key provided. Fiat currency change data will not be displayed.");

                return;
            }

            Task.Run(async () =>
            {
                await Task.Delay(5000);

                while (true)
                {
                    try
                    {
                        Fixer_Query();

                        if (FixerCurrencies.Count == 0) await CheckFixerCurrencies(true);
                    }
                    finally
                    {
                        await Task.Delay(AGE_FIXER_CHANGE);
                    }
                }
            });
        }

        private async void Fixer_Query(bool reference_change = false)
        {
            if (fixerQuery || string.IsNullOrEmpty(FIXERAPIKEY)) return;
            else fixerQuery = true;

            await Task.Run(async () =>
            {
                try
                {
                    AwaitOnline(Services.Fixer);

                    Activity();

                    string reference = await GetReferenceCurrency();

                    var currencies = Currencies.Where(x => FixerCurrencies.Contains(x) && !CMCCurrenicesCrypto.Contains(x) && x != reference);

                    string symbol = string.Join(",", currencies.ToArray());

                    string[] dates = new string[] { "latest", (DateTime.Now - new TimeSpan(1, 0, 0, 0)).ToString("yyyy-MM-dd"), (DateTime.Now - new TimeSpan(7, 0, 0, 0)).ToString("yyyy-MM-dd"), (DateTime.Now - new TimeSpan(30, 0, 0, 0)).ToString("yyyy-MM-dd") };

                    Fixer_JSON[] query = new Fixer_JSON[4];

                    using (WebClient webclient = new WebClient())
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            var json = webclient.DownloadString($"http://data.fixer.io/api/{dates[i]}?access_key={FIXERAPIKEY}&base=EUR&symbols={symbol + (reference == "EUR" ? null : $",{reference}")}");
                            query[i] = JsonConvert.DeserializeObject<Fixer_JSON>(json);
                        }
                    }

                    if (query.Any(x => x.Succeess == false && x?.error?.Code == "104"))
                    {
                        log.Information("Fixer.io requests exceeded. [Pair]");
                        return;
                    }

                    RemovePreviousReferenceCurrency();

                    foreach (var currency in currencies)
                    {
                        Change temp;

                        if (reference == "EUR")
                        {
                            temp = new Change()
                            {
                                Reference = reference,
                                Currency = currency,
                                Change1h = 0,
                                Change24h = Math.Round(((query[1].Rates[currency] / query[0].Rates[currency]) - 1) * 100, 2),
                                Change7d = Math.Round(((query[2].Rates[currency] / query[0].Rates[currency]) - 1) * 100, 2),
                                Change30d = Math.Round(((query[3].Rates[currency] / query[0].Rates[currency]) - 1) * 100, 2),
                                Date = DateTime.Now
                            };
                        }
                        else
                        {
                            temp = new Change()
                            {
                                Reference = reference,
                                Currency = currency,
                                Change1h = 0,
                                Change24h = Math.Round((((query[1].Rates[currency] / query[0].Rates[currency]) / (query[1].Rates[reference] / query[0].Rates[reference])) - 1) * 100, 2),
                                Change7d = Math.Round((((query[2].Rates[currency] / query[0].Rates[currency]) / (query[2].Rates[reference] / query[0].Rates[reference])) - 1) * 100, 2),
                                Change30d = Math.Round((((query[3].Rates[currency] / query[0].Rates[currency]) / (query[3].Rates[reference] / query[0].Rates[reference])) - 1) * 100, 2),
                                Date = DateTime.Now
                            };
                        }

                        UpdateEntry(temp);
                    }

                    if (reference_change) log.Information($"Fixer.io: Added {currencies.Count()} currencies for new reference currency [{reference}].");

                    async void RemovePreviousReferenceCurrency()
                    {
                        var ref_entry = CurrenciesChange.Where(x => x.Currency == reference).SingleOrDefault();

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (ref_entry != default)
                            {
                                CurrenciesChange.Remove(ref_entry);
                            }
                        });
                    }

                    async void UpdateEntry(Change temp)
                    {
                        var entry = CurrenciesChange.Where(x => x.Currency == temp.Currency).SingleOrDefault();

                        await Dispatcher.InvokeAsync(() =>
                        {
                            lock (currencyChangeLock)
                            {
                                if (entry == default)
                                {
                                    CurrenciesChange.Add(temp);
                                }
                                else
                                {
                                    CurrenciesChange.Remove(entry);

                                    CurrenciesChange.Add(temp);
                                }
                            }
                        });
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("408"))
                {
                    log.Error($"Error in Fixer.io Query: General Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("504"))
                {
                    log.Error($"Error in Fixer.io Query: Gateway Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("404"))
                {
                    log.Error("Error in Fixer.io Query: Not found");
                }
                catch (Exception ex)
                {
                    log.Error($"Error in Fixer.io Query: {ex.Short()}");
                }
            });

            fixerQuery = false;
        }

        // API Supported Currencies

        private void CheckCoinbaseCurrencies()
        {
            Task.Run(async () =>
            {
                int fiatCtr = 0;
                int cryptoCtr = 0;

                try
                {
                    Task fiat = Task.Run(async () =>
                    {
                        using (WebClient client = new WebClient())
                        {
                            while (true)
                            {
                                AwaitOnline(Services.Coinbase);

                                try
                                {
                                    var json_fiat = client.DownloadString("https://api.coinbase.com/v2/currencies");

                                    var currencies_fiat = JsonConvert.DeserializeObject<Coinbase_Currencies_JSON>(json_fiat);

                                    foreach (var item in currencies_fiat.data)
                                    {
                                        fiatCtr++;
                                        CoinbaseCurrenices.Add(item.Id);
                                    }

                                    break;
                                }
                                catch (Exception ex) when (ex.Message.Contains("400"))
                                {
                                    await Task.Delay(10000);
                                }
                                catch (Exception ex)
                                {
                                    log.Information($"Error querying Coinbase Fiat currencies: {ex.Short()}");

                                    await Task.Delay(10000);
                                }
                            }
                        }
                    });

                    Task crypto = Task.Run(async () =>
                    {
                        using (WebClient client = new WebClient())
                        {
                            while (true)
                            {
                                AwaitOnline(Services.Coinbase);

                                try
                                {
                                    var json_crypto = client.DownloadString("https://api.pro.coinbase.com/currencies");

                                    var currencies_crypto = JsonConvert.DeserializeObject<List<Coinbase_Currencies_JSON>>(json_crypto);

                                    foreach (var item in currencies_crypto)
                                    {
                                        if (!CoinbaseCurrenices.Contains(item.id))
                                        {
                                            cryptoCtr++;
                                            CoinbaseCurrenices.Add(item.id);
                                        }
                                    }

                                    break;
                                }
                                catch (Exception ex) when (ex.Message.Contains("400"))
                                {
                                    await Task.Delay(10000);
                                }
                                catch (Exception ex)
                                {
                                    log.Information($"Error querying Coinbase Crypto currencies: {ex.Short()}");

                                    await Task.Delay(10000);
                                }
                            }
                        }
                    });

                    await Task.WhenAll(new Task[] { fiat, crypto });

                    log.Information($"Found {fiatCtr} Fiat & {cryptoCtr} Crypto Currencies at Coinbase.");
                }
                catch (Exception ex)
                {
                    log.Information($"Error querying Coinbase currencies: {ex.Short()}");
                }
            });
        }

        private void CheckBitfinexCurrencies()
        {
            Task.Run(async () =>
            {
                int failCounter = 0;
                while (failCounter < 10)
                {
                    if (!AwaitOnline(Services.Bitfinex, true)) return;

                    try
                    {
                        using (WebClient webclient = new WebClient())
                        {
                            var json = webclient.DownloadString("https://api-pub.bitfinex.com/v2/conf/pub:list:currency");

                            json = json.Remove(0, 1);
                            json = json.Remove(json.Length - 1);

                            var currencies = JsonConvert.DeserializeObject<string[]>(json);

                            foreach (var item in currencies)
                            {
                                BitfinexCurrenices.Add(item);
                            }

                            log.Information($"Found {currencies.Count()} Currencies at Bitfinex.");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        failCounter++;
                        log.Information($"Error querying Bitfinex currencies: {ex.Message}");
                        await Task.Delay(5000 * failCounter);
                    }
                }
            });
        }

        private Task CheckCMCCurrencies()
        {
            return Task.Run(async () =>
                 {
                     int failCounter = 0;
                     while (failCounter < 10)
                     {
                         AwaitOnline(Services.CMC);

                         int ctr = 0;

                         try
                         {
                             CMCCurrenicesFIAT.Clear();
                             CMCCurrenicesCrypto.Clear();

                             using (var client = new WebClient())
                             {
                                 client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                                 client.Headers.Add("Accepts", "application/json");

                                 {
                                     var json = client.DownloadString("https://pro-api.coinmarketcap.com/v1/fiat/map");

                                     var currencies = JsonConvert.DeserializeObject<CMC_Currencies_JSON>(json);

                                     foreach (var fiat in currencies.data)
                                     {
                                         if (!CMCCurrenicesFIAT.Contains(fiat.symbol))
                                         {
                                             ctr++;
                                             CMCCurrenicesFIAT.Add(fiat.symbol);
                                         }
                                     }
                                 }

                                 CMCCurrenicesFIAT.OrderBy(x => x);
                             }

                             using (var client = new WebClient())
                             {
                                 client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                                 client.Headers.Add("Accepts", "application/json");
                                 {
                                     var json = client.DownloadString("https://pro-api.coinmarketcap.com/v1/cryptocurrency/map");

                                     var currencies = JsonConvert.DeserializeObject<CMC_Currencies_JSON>(json);

                                     foreach (var crypto in currencies.data)
                                     {
                                         if (!CMCCurrenicesCrypto.Contains(crypto.symbol))
                                         {
                                             ctr++;
                                             CMCCurrenicesCrypto.Add(crypto.symbol);
                                         }
                                     }
                                 }

                                 CMCCurrenicesCrypto.OrderBy(x => x);
                             }

                             log.Information($"Found {ctr} Currencies at CMC.");

                             Dispatcher.Invoke(() =>
                             {
                                 ComboBox_ReferenceCurrency.ItemsSource = CMCCurrenicesFIAT.Intersect(FixerCurrencies);

                                 if (REFERENCECURRENCY != null)
                                 {
                                     var idx = CMCCurrenicesFIAT.IndexOf(REFERENCECURRENCY);

                                     if (idx != -1)
                                     {
                                         ComboBox_ReferenceCurrency.SelectedIndex = idx;
                                     }
                                 }
                             });

                             break;
                         }
                         catch (Exception ex)
                         {
                             failCounter++;
                             log.Information($"Error querying CMC currencies: {ex.Message}");
                             await Task.Delay(5000 * failCounter);
                         }
                     }
                 });
        }

        private Task CheckFixerCurrencies(bool fromLoop = false)
        {
            return Task.Run(async () =>
             {
                 while (true)
                 {
                     AwaitOnline(Services.Fixer);

                     try
                     {
                         using (WebClient webclient = new WebClient())
                         {
                             var json = webclient.DownloadString($"http://data.fixer.io/api/symbols?access_key={FIXERAPIKEY}");

                             var converter = JsonConvert.DeserializeObject<Fixer_JSON>(json);
                             if (converter?.Succeess == false && converter?.error?.Code == "104")
                             {
                                 if (!fromLoop) log.Information($"Fixer.io requests exceeded. [Currencies]");
                                 break;
                             }

                             json = json.Remove(0, json.IndexOf(":{") + 2);
                             json = json.Remove(json.IndexOf("}}"));

                             var split = json.Split(new char[] { ',', ':' }, StringSplitOptions.RemoveEmptyEntries);

                             FixerCurrencies = split.Where(x => x.Length == 5).Select(x => x.Trim('"')).ToList();
                         }

                         if (FixerCurrencies.Count() > 0) log.Information($"Found {FixerCurrencies.Count} Currencies at Fixer.io");

                         Dispatcher.Invoke(() =>
                         {
                             ComboBox_ReferenceCurrency.ItemsSource = FixerCurrencies.Intersect(CMCCurrenicesFIAT);

                             if (REFERENCECURRENCY != null)
                             {
                                 var idx = FixerCurrencies.IndexOf(REFERENCECURRENCY);

                                 if (idx != -1)
                                 {
                                     ComboBox_ReferenceCurrency.SelectedIndex = idx;
                                 }
                             }
                         });

                         break;
                     }
                     catch (Exception ex)
                     {
                         log.Information($"Error querying Fixer.io currencies: {ex.Short()}");
                         await Task.Delay(10000);
                     }
                 }
             });
        }

        // Bitfinex Markets

        private void Loop_Bitfinex_MarketInfo()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    AwaitOnline(Services.Bitfinex);

                    Activity();

                    try
                    {
                        using (WebClient webClient = new WebClient())
                        {
                            var deserialized = JsonConvert.DeserializeObject<List<MarketInfo>>(webClient.DownloadString("https://api.bitfinex.com/v1/symbols_details"));

                            Dispatcher.Invoke(() =>
                            {
                                BitfinexMarkets = new ObservableCollection<MarketInfo>(deserialized.Where(x => !x.Pair.Contains(":")));
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"Error in Bitfinex Market Info: {ex.Message}");
                    }
                    finally
                    {
                        await Task.Delay(new TimeSpan(1, 0, 0));
                    }
                }
            });
        }

        // WSS Server

        private void Loop_WebSocketServer()
        {
            Task.Run(async () =>
            {
                Dispatcher.Invoke(() =>
                {
                    WebSocketServerStatus.Inlines.Clear();
                    WebSocketServerStatus.Inlines.Add(new Run("WebSocket Server\n") { FontWeight = FontWeights.Bold });
                    WebSocketServerStatus.Inlines.Add(new Run("Starting up...\n"));
                    WebSocketServerStatus.Inlines.Add(new Run("...") { FontSize = 26 });
                });

                await Task.Delay(3000);
                while (!online) { await Task.Delay(5000); Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"WSSV awaits Internet ..."; }); }

                wssv = new WebSocketServer(WSSPORT);
                wssv.AddWebSocketService(WSSENDPOINT, () => new ExchangeRateWSS(this));
                wssv.Start();

                while (!wssv.IsListening) { await Task.Delay(5000); Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"WSSV awaits startup ..."; }); }
                log.Information("Websocket Server running.");

                while (true)
                {
                    Activity();

                    try
                    {
                        await Task.Run(() =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                WebSocketServerStatus.Inlines.Clear();
                                WebSocketServerStatus.Inlines.Add(new Run("WebSocket Server\n") { FontWeight = FontWeights.Bold });
                                WebSocketServerStatus.Inlines.Add(new Run("ws://" + Dns.GetHostEntry(Dns.GetHostName()).AddressList.First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString() + ":" + wssv.Port.ToString() + WSSENDPOINT + "\n") { FontSize = 9 });
                                WebSocketServerStatus.Inlines.Add(new Run(wssv.WebSocketServices.SessionCount.ToString()) { FontSize = 26 });
                            });

                            wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new ExchangeRateServerInfo()
                            {
                                info = ExchangeRateServerInfo.ExRateInfoType.Rates,
                                success = true,
                                currencies = Currencies,
                                currencies_change = CurrenciesChange?.Union(CurrenciesChange_Specific).ToList(),
                                rates = Rates?.ToList(),
                            }));
                        });
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Error in Websocket Server Loop: {ex.Message} {ex}");
                    }
                    finally
                    {
                        await Task.Delay(3000);
                    }
                }
            });
        }

        internal Task WSS_History(string CCY1, string CCY2)
        {
            return Task.Run(async () =>
            {
                while (!online) { Dispatcher.Invoke(() => { ExchangeRateInfo.Text = "Awaiting Internet..."; }); await Task.Delay(5000); }

                try
                {
                    lock (sendHistoryLock)
                    {
                        if (wssv != null)
                        {
                            if (HistoricRatesLongTerm.ContainsKey((CCY1, CCY2)) && HistoricRatesLongTerm.ContainsKey((CCY1, CCY2)))
                            {
                                var cast = new ExchangeRateServerInfo()
                                {
                                    success = true,
                                    info = ExchangeRateServerInfo.ExRateInfoType.History,
                                    history = new ExchangeRateServerInfo.History() { ccy1 = CCY1, ccy2 = CCY2, historyLong = new List<TimeData>(HistoricRatesLongTerm[(CCY1, CCY2)]), historyShort = new List<TimeData>(HistoricRatesShortTerm[(CCY1, CCY2)]) }
                                };

                                wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));

                                Dispatcher.Invoke(() =>
                                {
                                    ExchangeRateInfo.Text = $"Sent history for [{CCY1}/{CCY2}].";
                                });
                            }
                            else
                            {
                                var cast = new ExchangeRateServerInfo()
                                {
                                    success = false,
                                    message = $"[{CCY1}/{CCY2}] history not available.",
                                    info = ExchangeRateServerInfo.ExRateInfoType.History,
                                    history = new ExchangeRateServerInfo.History() { ccy1 = CCY1, ccy2 = CCY2 }
                                };

                                wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));

                                log.Information($"Unable to send history for [{CCY1}/{CCY2}].");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Error processing history cast of [{CCY1}/{CCY2}]:\n{ex}");
                }
            });
        }

        internal Task WSS_AddCurrency(string candidate)
        {
            return Task.Run(async () =>
            {
                while (!online) { Dispatcher.Invoke(() => { ExchangeRateInfo.Text = "Awaiting Internet..."; }); await Task.Delay(5000); }

                try
                {
                    lock (newCurrencyLock)
                    {
                        Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Verifying {candidate}..."; });

                        if (Currencies.Contains(candidate))
                        {
                            Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"{candidate} already supported."; });

                            if (wssv != null)
                            {
                                var cast = new ExchangeRateServerInfo()
                                {
                                    success = false,
                                    message = $"Already supported: [{candidate}]",

                                    info = ExchangeRateServerInfo.ExRateInfoType.NewCurrency,
                                    newCurrency = candidate
                                };

                                wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
                            }

                            return;
                        }
                        else if ((BitfinexCurrenices.Contains(candidate) || CoinbaseCurrenices.Contains(candidate)) && candidate != "BCH")
                        {
                            Currencies.Add(candidate);

                            Dispatcher.Invoke(() =>
                            {
                                DGCurrencies.ItemsSource = null;
                                DGCurrencies.ItemsSource = Currencies;
                                currencyInput.Text = "";
                            });

                            log.Information($"Added new Currency: {candidate}");

                            if (wssv != null)
                            {
                                var cast = new ExchangeRateServerInfo()
                                {
                                    success = true,
                                    info = ExchangeRateServerInfo.ExRateInfoType.NewCurrency,
                                    newCurrency = candidate
                                };
                                wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
                            }

                            Task.Run(async () =>
                            {
                                if (justAdded) return;
                                else justAdded = true;

                                await Task.Delay(5000);

                                CMC_Query();
                                Fixer_Query();

                                justAdded = false;
                            });

                            return;
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                currencyInput.Text = "";
                                ExchangeRateInfo.Text = $"{candidate} not supported.";
                            });

                            if (wssv != null)
                            {
                                var cast = new ExchangeRateServerInfo()
                                {
                                    success = false,
                                    message = $"{candidate} not supported.",
                                    info = ExchangeRateServerInfo.ExRateInfoType.NewCurrency,
                                    newCurrency = candidate
                                };

                                wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
                            }

                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Error adding Currency: {ex}");
                }
            });
        }

        internal Task WSS_AddTradingPair(string CCY1, string CCY2, string exchange)
        {
            return Task.Run(() =>
            {
                Services Exchange = default;
                try
                {
                    Exchange = (Services)Enum.Parse(typeof(Services), exchange, true);
                }
                catch
                {
                    ResultNotification($"Unable to process Exchange '{exchange}'.", false);

                    return;
                }

                if (SpecificRequests.Where(x => x.Item1 == CCY1 && x.Item2 == CCY2 && x.Item3 == Exchange).Any())
                {
                    ResultNotification($"Already supported: [{CCY1}/{CCY2}] @ {exchange}", false);
                }
                else
                {
                    if (Exchange == Services.Bitfinex)
                    {
                        if (!BitfinexCurrenices.Contains(CCY1) || !BitfinexCurrenices.Contains(CCY2))
                        {
                            ResultNotification($"Not supported: [{CCY1}/{CCY2}] @ {exchange}", false);
                        }
                        else
                        {
                            Success();
                            ResultNotification($"Added: [{CCY1}/{CCY2}] @ {exchange}", true);
                        }
                    }
                    else if (Exchange == Services.Coinbase)
                    {
                        if (!CoinbaseCurrenices.Contains(CCY1) || !CoinbaseCurrenices.Contains(CCY2))
                        {
                            ResultNotification($"Not supported: [{CCY1}/{CCY2}] @ {exchange}", false);
                        }
                        else
                        {
                            Success();
                            ResultNotification($"Added: [{CCY1}/{CCY2}] @ {exchange}", true);
                        }
                    }
                }

                void Success()
                {
                    SpecificRequests.Add((CCY1, CCY2, Exchange));

                    CMC_Query_SpecificPair(CCY1, CCY2);

                    log.Information($"Added new Pair: [{CCY1}/{CCY2}] @ {exchange}");
                }

                void ResultNotification(string message, bool success)
                {
                    if (wssv != null)
                    {
                        var cast = new ExchangeRateServerInfo()
                        {
                            success = success,
                            message = message,
                            newPair = new RequestedPair() { CCY1 = CCY1, CCY2 = CCY2 },
                            info = ExchangeRateServerInfo.ExRateInfoType.SpecificPair
                        };

                        wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
                    }

                    Dispatcher.Invoke(() => { ExchangeRateInfo.Text = message; });
                }
            });
        }

        internal Task WSS_SendMarketInfo(string exchange)
        {
            return Task.Run(() =>
            {
                if (exchange.Equals("bitfinex", StringComparison.OrdinalIgnoreCase))
                {
                    if (wssv != null)
                    {
                        if (BitfinexMarkets?.Count() > 0)
                        {
                            wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new ExchangeRateServerInfo()
                            {
                                success = true,
                                message = $"{BitfinexMarkets.Count()} Bitfinex Markets available.",
                                markets = BitfinexMarkets.ToList(),
                                info = ExchangeRateServerInfo.ExRateInfoType.Markets
                            }));
                        }
                        else
                        {
                            wssv.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new ExchangeRateServerInfo()
                            {
                                success = false,
                                message = "No Bitfinex Markets available.",
                                info = ExchangeRateServerInfo.ExRateInfoType.Markets
                            }));
                        }
                    }
                }
            });
        }

        // Utility

        private void Activity()
        {
            Task.Run(async () =>
            {
                try
                {
                    if (isCurrentlyUpdatingActivity == true) return;
                    isCurrentlyUpdatingActivity = true;

                    Dispatcher.Invoke(() => { activityIndicator.Source = Res.Yellow; });
                    await Task.Delay(15);
                    Dispatcher.Invoke(() => { activityIndicator.Source = Res.Red; });
                }
                catch (Exception ex)
                {
                    log.Error("Error updating Activity: " + ex.Message);
                }
                finally
                {
                    isCurrentlyUpdatingActivity = false;
                }
            });
        }

        private bool AwaitOnline(Services service, bool timeout = false)
        {
            int timeOutCounter = 0;

            while (true)
            {
                if (timeout && timeOutCounter > 30)
                {
                    return false;
                }

                if (online)
                {
                    using (Ping ping = new Ping())
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
                            switch (service)
                            {
                                case Services.CMC:
                                    Dispatcher.Invoke(() => { onlineIndicator_CMC.Source = Res.Red; });
                                    break;

                                case Services.Bitfinex:
                                    Dispatcher.Invoke(() => { onlineIndicator_Bitfinex.Source = Res.Red; });
                                    break;

                                case Services.Coinbase:
                                    Dispatcher.Invoke(() => { onlineIndicator_Coinbase.Source = Res.Red; });
                                    break;

                                case Services.Fixer:
                                    Dispatcher.Invoke(() => { onlineIndicator_Fixer.Source = Res.Red; });
                                    break;
                            }

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

        private async Task<string> GetReferenceCurrency()
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

        // Interface

        private async void BTN_Click_ChangePullNow(object sender, RoutedEventArgs e)
        {
            try
            {
                foreach (var item in SpecificRequests)
                {
                    await CMC_Query_SpecificPair(item.Item1, item.Item2, true);
                }
            }
            catch (Exception ex)
            {
                log.Information($"Error in Manual Change Pull: {ex.Short()}");
            }
        }

        internal void AddCurrency_Button(object sender, RoutedEventArgs e)
        {
            try
            {
                string ccy = default;

                Dispatcher.Invoke(() =>
                {
                    ccy = currencyInput.Text.ToUpper();
                    ExchangeRateInfo.Text = $"Verifying addition of {ccy} ...";
                });

                WSS_AddCurrency(ccy);
            }
            catch (Exception ex)
            {
                log.Error($"Error adding new Currency manually: {ex.Message} {ex}");
            }
        }

        private void AddCurrency_EnterKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddCurrency_Button(default, default);
        }

        private void BTN_Click_DeleteSpecificRequest(object sender, RoutedEventArgs e)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (LV_SpecificRequest.SelectedItems != null)
                    {
                        foreach (var item in SpecificRequests_Selected)
                        {
                            CurrenciesChange_Specific.Remove(item);

                            var req = SpecificRequests.FirstOrDefault(x => x.Item1 == item.Currency && x.Item2 == item.Reference);

                            if (req != default)
                            {
                                SpecificRequests.Remove(req);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                log.Information($"Error in Deleting Specific Request: {ex.Short()}");
            }
        }

        private void LV_SpecificRequests_CM_Opening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                if (LV_SpecificRequest.SelectedItem == null)
                {
                    e.Handled = true;
                }
                else
                {
                    SpecificRequests_Selected = LV_SpecificRequest.SelectedItems.Cast<Change>().ToList();
                }
            }
            catch (Exception ex)
            {
                log.Information($"Error opening Executed Trade LV CM: {ex.Short()}");
            }
        }

        private void ChangeInReferenceCurrency(object sender, SelectionChangedEventArgs e)
        {
            CMC_Query(true);
            Fixer_Query(true);

            log.Information($"Changed Reference Currency to [{ComboBox_ReferenceCurrency.SelectedItem as string}].");
        }

        private void VPNTaskBarIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (Visibility == Visibility.Hidden)
                {
                    Visibility = Visibility.Visible;
                    Activate();
                }
                else if (Visibility == Visibility.Visible)
                {
                    Visibility = Visibility.Hidden;
                }
            });
        }

        private void ClearLog(object sender, RoutedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                SystemLog.Text = "";
                LBL_SysLog.Content = $"System Log";
            });
        }

        // Exit

        private void ClosingMainWindow(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Visibility = Visibility.Hidden;
        }

        private void QuitApplication(object sender, RoutedEventArgs e)
        {
            App.Current.Shutdown();
        }
    }
}