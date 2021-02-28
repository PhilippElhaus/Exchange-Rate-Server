using Newtonsoft.Json;
using Serilog;
using Serilog.Core;
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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace ExchangeRateServer
{
    internal enum Services
    {
        Fixer = 0,
        CMC = 1,
        Bitfinex = 2,
        Coinbase = 3
    }

    public partial class MainWindow : Window
    {
        internal ILogger log;

        private double currentCPUusage;
        private double currentRAMusage;
        private DateTime appStartUp;

        private bool cmcQuery;
        private bool fixerQuery;

        private bool online;
        private bool isCurrentlyUpdatingActivity;
        private object newCurrencyLock = new object();
        private object sendHistoryLock = new object();

        private readonly System.Timers.Timer autoRefresh = new System.Timers.Timer(1000) { Enabled = true, AutoReset = true };

        internal WebSocketServer wssv;
        internal ObservableCollection<ExchangeRate> Rates = new ObservableCollection<ExchangeRate>();
        internal Dictionary<(string, string), List<TimeData>> HistoricRatesLongTerm = new Dictionary<(string, string), List<TimeData>>();
        internal Dictionary<(string, string), List<TimeData>> HistoricRatesShortTerm = new Dictionary<(string, string), List<TimeData>>();

        internal ObservableCollection<MarketInfo> BitfinexCOMMarkets = new ObservableCollection<MarketInfo>();

        internal ObservableCollection<MarketInfo> BitcoinDEMarkets = new ObservableCollection<MarketInfo>()
        { new MarketInfo() { Pair = "btceur", Minimum_order_size = 60 },
            new MarketInfo() { Pair = "etheur", Minimum_order_size = 60 },
            new MarketInfo() { Pair = "btgeur", Minimum_order_size = 60 },
            new MarketInfo() { Pair = "bsveur", Minimum_order_size = 60 },
            new MarketInfo() { Pair = "ltceur", Minimum_order_size = 60 },
            new MarketInfo() { Pair = "bcheur", Minimum_order_size = 60 } };

        internal ObservableCollection<Change> CurrenciesChange = new ObservableCollection<Change>();

        private string CMCAPIKEY;
        private string FIXERAPIKEY;
        private string WSSENDPOINT = "/rates";
        private string REFERENCECURRENCY = "EUR";

        private int WSSPORT = 222;
        private TimeSpan MAXAGEFIAT2FIATRATE = new TimeSpan(0, 0, 30);
        private TimeSpan MAXAGEMIXEDRATE = new TimeSpan(0, 0, 30);

        private List<string> Currencies = new List<string>() { "USD", "EUR", "BTC", "ETH" };
        private readonly List<string> FIAT = new List<string>() { "USD", "EUR", "JPY", "CAD", "GBP", "CNY", "NZD", "AUD", "CHF" };

        private List<string> CoinbaseCurrenices = new List<string>();
        private List<string> BitfinexCurrenices = new List<string>();
        private List<string> CMCCurrenicesFIAT = new List<string>();
        private List<string> CMCCurrenicesCrypto = new List<string>();
        private List<string> FixerCurrencies = new List<string>();

        public MainWindow()
        {
            InitializeComponent();

            appStartUp = DateTime.Now;
            SysWsLog syswslog = new SysWsLog(this);
            log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(syswslog, LogEventLevel.Information).CreateLogger();
            log.Information("Starting Up...");

            DGCurrencies.ItemsSource = Currencies;

            InitFlags();
            InitConfig();

            // Events

            autoRefresh.Elapsed += (x, y) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ExchangeRatesLV.ItemsSource = null;
                    ExchangeRatesLV.ItemsSource = Rates;
                    MarketsLV.ItemsSource = null;
                    MarketsLV.ItemsSource = BitfinexCOMMarkets;
                    CurrencyLV.ItemsSource = null;
                    CurrencyLV.ItemsSource = CurrenciesChange;

                    ;

                    if (CountHistoricRate(out int count_short, out int count_long) == (0, 0))
                    {
                        lbl_exrates.Content = $"Exchange Rates ({Rates.Count})";
                    }
                    else
                    {
                        lbl_exrates.Content = $"Exchange Rates ({Rates.Count}) | Recorded: [{count_short}|{count_long}]";
                    }

                    lbl_markets_BitfinexCOM.Content = $"BitfinexCOM ({BitfinexCOMMarkets.Count})";
                    if (REFERENCECURRENCY != null) lbl_currencies_change.Content = $"Currencies ({CurrenciesChange.Count}) | Relating to ";

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

            MarketsLVBitcoinDE.ItemsSource = BitcoinDEMarkets;
            lbl_markets_BitcoinDE.Content = $"BitcoinDE ({BitcoinDEMarkets.Count})";

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

                if (App.flag_markets)
                {
                    Dispatcher.Invoke(() =>  Tab_Markets.Visibility = Visibility.Visible);
                   
                    Loop_Bitfinex_MarketInfo();
                }
            });

            Loop_ExchangeRate();

        }

        private void InitFlags()
        {
            Dispatcher.Invoke(() =>
            {
                if (!App.flag_log) Tab_Log.Visibility = Visibility.Collapsed;
            });
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
                                MAXAGEFIAT2FIATRATE = TimeSpan.FromSeconds(double.Parse(entry.Remove(0, entry.IndexOf("=") + 1)));
                            }
                            else if (entry.StartsWith("MAXAGEMIXEDRATE"))
                            {
                                MAXAGEMIXEDRATE = TimeSpan.FromSeconds(double.Parse(entry.Remove(0, entry.IndexOf("=") + 1)));
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
                        log.Error($"UpTimeAndLoad Info Loop Error: {ex.Message} {ex}");
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
            Task.Run(async () =>
            {
                while (CoinbaseCurrenices.Count == 0 || BitfinexCurrenices.Count == 0) { await Task.Delay(1000); }

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
                                else if (BitfinexCurrenices.Contains(base_currency))
                                {
                                    await ExchangeRate_Bitfinex(base_currency);
                                }

                                await Task.Delay(3000);
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
        }

        private async Task ExchangeRate_Coinbase(string base_currency)
        {
            try
            {
                TimeSpan maxAge = default;
                if (MAXAGEFIAT2FIATRATE > MAXAGEMIXEDRATE) maxAge = MAXAGEMIXEDRATE;
                else maxAge = MAXAGEFIAT2FIATRATE;

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

                            foreach (var quote_currency in Currencies.ToArray())
                            {
                                if (quote_currency != base_currency)
                                {
                                    if (deserialized.data.Rates.ContainsKey(quote_currency))
                                    {
                                        if (Rates.ToList().Exists(x => x.CCY1 == base_currency && x.CCY2 == quote_currency)) // Update
                                        {
                                            var exrEntry = Rates.Where(x => x.CCY1 == base_currency && x.CCY2 == quote_currency).Single();

                                            exrEntry.Date = DateTime.Now;

                                            if (FIAT.Contains(quote_currency) && !FIAT.Contains(base_currency))
                                            {
                                                exrEntry.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2);
                                            }
                                            else
                                            {
                                                exrEntry.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);
                                            }

                                            Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Checking [{base_currency}]... updated."; });

                                            if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)].Last().Time)
                                            {
                                                HistoricRatesLongTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)exrEntry.Rate });

                                                if ((DateTime.Now - new TimeSpan(7, 0, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)][0].Time)
                                                {
                                                    HistoricRatesLongTerm[(base_currency, quote_currency)].RemoveAt(0);
                                                }
                                            }

                                            if ((DateTime.Now - new TimeSpan(0, 1, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency)].Last().Time)
                                            {
                                                HistoricRatesShortTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)exrEntry.Rate });

                                                if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency)][0].Time)
                                                {
                                                    HistoricRatesShortTerm[(base_currency, quote_currency)].RemoveAt(0);
                                                }
                                            }
                                        }
                                        else // New
                                        {
                                            var exr = new ExchangeRate()
                                            {
                                                CCY1 = base_currency,
                                                CCY2 = quote_currency,
                                                Date = DateTime.Now,
                                            };

                                            if (FIAT.Contains(quote_currency) && !FIAT.Contains(base_currency))
                                            {
                                                exr.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2);
                                            }
                                            else
                                            {
                                                exr.Rate = Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);
                                            }

                                            Dispatcher.Invoke(() =>
                                            {
                                                Rates.Add(exr);
                                                Rates = new ObservableCollection<ExchangeRate>(Rates.OrderBy(x => x.CCY1).ThenBy(y => y.CCY2));
                                            });

                                            log.Information($"New Pair: [{exr.CCY1}/{exr.CCY2}] via Coinbase");

                                            HistoricRatesLongTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)exr.Rate } });

                                            HistoricRatesShortTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)exr.Rate } });
                                        }
                                    }
                                    else
                                    {
                                        await ExchangeRate_Bitfinex(base_currency, true, quote_currency);
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
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

        private async Task ExchangeRate_Bitfinex(string base_currency, bool singular = false, string quote_currency = default)
        {
            await Task.Run(async () =>
            {
                try
                {
                    if (singular == false)
                    {
                        foreach (var quote_currency_ in Currencies.ToArray())
                        {
                            if (base_currency != quote_currency_)
                            {
                                ExchangeRate rate = default;
                                Dispatcher.Invoke(() => { rate = Rates.Where(x => x.CCY1 == base_currency && x.CCY2 == quote_currency_).FirstOrDefault(); });

                                if (rate != default) // Update
                                {
                                    TimeSpan maxAge = default;
                                    if (FIAT.Contains(base_currency) && FIAT.Contains(quote_currency_)) maxAge = MAXAGEFIAT2FIATRATE;
                                    else maxAge = MAXAGEMIXEDRATE;

                                    if ((DateTime.Now - rate.Date) > maxAge)
                                    {
                                        var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency_);

                                        if (res != 0)
                                        {
                                            rate.Rate = res;
                                            rate.Date = DateTime.Now;

                                            Dispatcher.Invoke(() =>
                                            {
                                                ExchangeRateInfo.Text = $"Rate Updated: [{base_currency}/{quote_currency_}] ({res})";
                                            });

                                            if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency_)].Last().Time)
                                            {
                                                HistoricRatesLongTerm[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                                if ((DateTime.Now - new TimeSpan(7, 0, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency_)][0].Time)
                                                {
                                                    HistoricRatesLongTerm[(base_currency, quote_currency_)].RemoveAt(0);
                                                }
                                            }

                                            if ((DateTime.Now - new TimeSpan(0, 1, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency_)].Last().Time)
                                            {
                                                HistoricRatesShortTerm[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                                if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency_)][0].Time)
                                                {
                                                    HistoricRatesShortTerm[(base_currency, quote_currency_)].RemoveAt(0);
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
                                                CCY1 = base_currency,
                                                CCY2 = quote_currency_,
                                                Date = DateTime.Now,
                                                Rate = res
                                            });

                                            Rates = new ObservableCollection<ExchangeRate>(Rates.OrderBy(x => x.CCY1).ThenBy(y => y.CCY2));
                                        });

                                        log.Information($"New Pair: [{base_currency}/{quote_currency_}] via Bitfinex");

                                        HistoricRatesLongTerm.Add((base_currency, quote_currency_), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                        HistoricRatesShortTerm.Add((base_currency, quote_currency_), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                    }
                                }

                                await Task.Delay(3000);
                            }
                        }
                    }
                    else
                    {
                        ExchangeRate rate = default;
                        Dispatcher.Invoke(() => { rate = Rates.Where(x => x.CCY1 == base_currency && x.CCY2 == quote_currency).FirstOrDefault(); });

                        if (rate != default) // Update
                        {
                            TimeSpan maxAge = default;
                            if (FIAT.Contains(base_currency) && FIAT.Contains(quote_currency)) maxAge = MAXAGEFIAT2FIATRATE;
                            else maxAge = MAXAGEMIXEDRATE;

                            if ((DateTime.Now - rate.Date) > maxAge)
                            {
                                var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency);

                                if (res != 0)
                                {
                                    rate.Rate = res;
                                    rate.Date = DateTime.Now;

                                    Dispatcher.Invoke(() =>
                                    {
                                        ExchangeRateInfo.Text = $"Rate Updated: [{base_currency}/{quote_currency}] ({res})";
                                    });

                                    if ((DateTime.Now - new TimeSpan(1, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)].Last().Time)
                                    {
                                        HistoricRatesLongTerm[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

                                        if ((DateTime.Now - new TimeSpan(7, 0, 0, 0)) > HistoricRatesLongTerm[(base_currency, quote_currency)][0].Time)
                                        {
                                            HistoricRatesLongTerm[(base_currency, quote_currency)].RemoveAt(0);
                                        }
                                    }

                                    if ((DateTime.Now - new TimeSpan(0, 1, 0)) > HistoricRatesShortTerm[(base_currency, quote_currency)].Last().Time)
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
                        else // Add New
                        {
                            var res = Bitfinex_ExchangeRate_Query(base_currency, quote_currency);

                            if (res != 0)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    Rates.Add(new ExchangeRate()
                                    {
                                        CCY1 = base_currency,
                                        CCY2 = quote_currency,
                                        Date = DateTime.Now,
                                        Rate = res
                                    });

                                    Rates = new ObservableCollection<ExchangeRate>(Rates.OrderBy(x => x.CCY1).ThenBy(y => y.CCY2));
                                });

                                log.Information($"New Pair: [{base_currency}/{quote_currency}] via Bitfinex");

                                HistoricRatesLongTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
                                HistoricRatesShortTerm.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
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
                        if (FIAT.Contains(ccy2) && !FIAT.Contains(ccy1))
                        {
                            result = Ext.TruncateDecimal(decimal.Parse(sRead.ReadToEnd().Trim('[').Trim(']'), new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2);
                        }
                        else
                        {
                            result = Ext.TruncateDecimal(decimal.Parse(sRead.ReadToEnd().Trim('[').Trim(']'), new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);
                        }

                        sRead.Close();

                        return result;
                    };
                }
                catch (Exception ex) when (ex.Message.Contains("500"))
                {
                    log.Error($"Not available: [{ccy1}/{ccy2}]");
                    return default;
                }
                catch (Exception ex) when (ex.Message.Contains("408"))
                {
                    log.Error($"Timeout: [{ccy1}/{ccy2}]");
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
                    }
                    finally
                    {
                        await Task.Delay(300000); // 5min
                    }
                }
            });
        }

        private async void CMC_Query(bool reference_change = false)
        {
            if (cmcQuery || string.IsNullOrEmpty(CMCAPIKEY)) return;
            else cmcQuery = true;

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

                        var currencies = Currencies.Where(x => CMCCurrenicesCrypto.Contains(x));

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

                        CMC_JSON cmc = JsonConvert.DeserializeObject<CMC_JSON>(json);

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

                            CurrenciesChange = new ObservableCollection<Change>(CurrenciesChange.OrderBy(x => x.Currency));
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
                catch (Exception ex)
                {
                    log.Error($"Error in CMC Query: {ex}");
                }
            });

            cmcQuery = false;
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
                    }
                    finally
                    {
                        await Task.Delay(new TimeSpan(1, 0, 0, 0));
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

                    await Dispatcher.InvokeAsync(() => { CurrenciesChange = new ObservableCollection<Change>(CurrenciesChange.OrderBy(x => x.Currency)); });

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
                            if (entry == default)
                            {
                                CurrenciesChange.Add(temp);
                            }
                            else
                            {
                                CurrenciesChange.Remove(entry);

                                CurrenciesChange.Add(temp);
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

        // Market Info

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

                            foreach (var item in deserialized)
                            {
                                item.Date = DateTime.Now;
                            }

                            Dispatcher.Invoke(() =>
                            {
                                BitfinexCOMMarkets = new ObservableCollection<MarketInfo>(deserialized.Where(x => !x.Pair.Contains(":")));
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"Error in Bitfinex Market Info: {ex.Message}");
                    }
                    finally
                    {
                        await Task.Delay(300000);
                    }
                }
            });
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

        private Task CheckFixerCurrencies()
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

                             json = json.Remove(0, json.IndexOf(":{") + 2);
                             json = json.Remove(json.IndexOf("}}"));

                             var split = json.Split(new char[] { ',', ':' }, StringSplitOptions.RemoveEmptyEntries);

                             FixerCurrencies = split.Where(x => x.Length == 5).Select(x => x.Trim('"')).ToList();
                         }

                         log.Information($"Found {FixerCurrencies.Count} Currencies at Fixer.io");

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
                                success = true,
                                info = ExchangeRateServerInfo.ExRateInfoType.Rates,
                                currencies = Currencies,
                                currencies_change = CurrenciesChange?.ToList(),
                                rates = Rates?.ToList(),
                                markets = App.flag_markets ? new Dictionary<string, List<MarketInfo>>() { { "Bitfinex", BitfinexCOMMarkets?.ToList() }, { "BitcoinDE", BitcoinDEMarkets?.ToList() } } : null
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
                        log.Information($"Verifying addition of {candidate} ...");

                        if (Currencies.Contains(candidate))
                        {
                            log.Information($"Already supported: [{candidate}]");

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

                            CMC_Query();
                            Fixer_Query();

                            return;
                        }
                        else
                        {
                            log.Information($"Not supported: [{candidate}]");

                            Dispatcher.Invoke(() =>
                            {
                                currencyInput.Text = "";
                            });

                            if (wssv != null)
                            {
                                var cast = new ExchangeRateServerInfo()
                                {
                                    success = false,
                                    message = $"Not supported: [{candidate}]",
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
                                    if (ping.Send("data.fixer.io").Status == IPStatus.Success)
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

    // EXR Klassen

    public struct TimeData
    {
        public DateTime Time { get; set; }
        public double Rate { get; set; }
    }

    [Serializable]
    public class ExchangeRate : INotifyPropertyChanged
    {
        private string ccy1;
        private string ccy2;
        private decimal rate;
        private DateTime date;

        public string CCY1 { get { return ccy1; } set { if (value != ccy1) { ccy1 = value; NotifyPropertyChanged(); } } }
        public string CCY2 { get { return ccy2; } set { if (value != ccy2) { ccy2 = value; NotifyPropertyChanged(); } } }
        public decimal Rate { get { return rate; } set { if (value != rate) { rate = value; NotifyPropertyChanged(); } } }
        public DateTime Date { get { return date; } set { if (value != date) { date = value; NotifyPropertyChanged(); } } }

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ExchangeRateServerInfo
    {
        public enum ExRateInfoType
        {
            Rates = 1,
            History = 2,
            NewCurrency = 3,
        }

        public ExRateInfoType info;
        public List<ExchangeRate> rates;
        public Dictionary<string, List<MarketInfo>> markets;
        public List<MarketInfo> bitfinexCOMmarkets;
        public List<MarketInfo> bitcoinDEmarkets;
        public List<Change> currencies_change;
        public List<string> currencies;

        public string newCurrency;
        public History history;

        public bool success;
        public string message;

        public class History
        {
            public string ccy1;
            public string ccy2;
            public List<TimeData> historyShort;
            public List<TimeData> historyLong;
        }
    }

    public class ExchangeRateWSS : WebSocketBehavior
    {
        private object newMessageLock = new object();

        private MainWindow Main;

        public ExchangeRateWSS(MainWindow main)
        {
            Main = main;
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            try
            {
                lock (newMessageLock)
                {
                    if (e.Data.StartsWith("HISTORY")) // HISTORY.BTC.EUR
                    {
                        var data = e.Data.ToUpper().Split('.');
                        if (data.Length == 3) Main.WSS_History(data[1], data[2]);
                        else Main.WSS_History(data[1], "EUR");

                        Main.Dispatcher.Invoke(() =>
                        {
                            Main.ExchangeRateInfo.Text = $"Request for Historic Data on [{data[1]}/{data[2]}] received.";
                        });
                    }
                    else if (e.Data.StartsWith("CURRENCY")) // CURRENCY.BTC
                    {
                        var data = e.Data.ToUpper().Split('.');

                        Main.Dispatcher.Invoke(() =>
                        {
                            Main.currencyInput.Text = data[1];
                            Main.ExchangeRateInfo.Text = $"Verifying addition of {data[1]} ...";
                        });

                        Main.WSS_AddCurrency(data[1]);
                    }
                    else
                    {
                        Main.log.Information($"Unable to process '{e.Data}'");
                    }
                }
            }
            catch (Exception ex)
            {
                Main.log.Error($"Error handling OnMessage Event of WSS Server:\n{ex}");
            }
        }

        protected override void OnOpen()
        {
            Main.Dispatcher.Invoke(() =>
            {
                Main.ExchangeRateInfo.Text = "Client connected.";
            });
        }

        protected override void OnClose(CloseEventArgs e)
        {
            Main.Dispatcher.Invoke(() =>
            {
                Main.ExchangeRateInfo.Text = $"Client disconnected: {e.Reason} ({e.Code}) Clean: {e.WasClean}";
            });
        }
    }

    public class Change
    {
        public string Reference { get; set; }
        public string Currency { get; set; }
        public double Change1h { get; set; }
        public double Change24h { get; set; }
        public double Change7d { get; set; }
        public double Change30d { get; set; }
        public DateTime Date { get; set; }
    }

    public class MarketInfo
    {
        private string pair;

        public string Pair
        {
            get { return pair; }
            set
            {
                try
                {
                    pair = value.ToUpper();
                    BaseCurrency = value.Substring(0, 3).ToUpper();
                    QuoteCurrency = value.Substring(3).ToUpper();
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not parse Market Info: {value} | {ex.Message}");
                }
            }
        }

        public string BaseCurrency { get; set; }
        public string QuoteCurrency { get; set; }
        public decimal Minimum_order_size { get; set; }
        public DateTime Date { get; set; }
    }

    public class CMC_JSON
    {
        public Dictionary<string, Data> data { get; set; }

        public class Data
        {
            public string symbol { get; set; }
            public Quote quote { get; set; }
        }

        public class Quote
        {
            public Currency currency { get; set; }

            public class Currency
            {
                public double percent_change_1h { get; set; }
                public double percent_change_24h { get; set; }
                public double percent_change_7d { get; set; }
                public double percent_change_30d { get; set; }
            }
        }
    }

    public class CMC_Currencies_JSON
    {
        public class Data
        {
            public string symbol { get; set; }
        }

        public List<Data> data { get; set; }
    }

    public class Coinbase_JSON
    {
        [JsonProperty("data")]
        public Data data { get; set; }

        public class Data
        {
            [JsonProperty("currency")]
            public string Currency { get; set; }

            [JsonProperty("rates")]
            public Dictionary<string, string> Rates { get; set; }
        }
    }

    public class Coinbase_Currencies_JSON
    {
        [JsonProperty("data")]
        public Data[] data { get; set; }

        public string id { get; set; }

        public class Data
        {
            [JsonProperty("id")]
            public string Id { get; set; }
        }
    }

    public class Fixer_JSON
    {
        [JsonProperty("base")]
        public string Base { get; set; }

        [JsonProperty("date")]
        public DateTimeOffset Date { get; set; }

        [JsonProperty("rates")]
        public Dictionary<string, double> Rates { get; set; }
    }

    // Converter

    public class TimeSinceLastUpdate : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (((DateTime)value).Year == 1900) return " ";

            var t = DateTime.Now - (DateTime)value;

            string result;
            if (t.Minutes > 0)
            {
                result = string.Format("{0}m {1}s", t.Minutes, t.Seconds);
            }
            else
            {
                result = string.Format("{1}s", t.Minutes, t.Seconds);
            }

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return default;
        }
    }

    // Res + Ext

    public static class Res
    {
        public static readonly BitmapImage Red = new BitmapImage(new Uri(@"images\status_red.png", UriKind.Relative));
        public static readonly BitmapImage Yellow = new BitmapImage(new Uri(@"images\status_yellow.png", UriKind.Relative));
        public static readonly BitmapImage Green = new BitmapImage(new Uri(@"images\status_green.png", UriKind.Relative));
        public static readonly System.Drawing.Icon On = new System.Drawing.Icon(@"images\green.ico");
        public static readonly System.Drawing.Icon Off = new System.Drawing.Icon(@"images\red.ico");
        public static readonly System.Drawing.Icon Connected = new System.Drawing.Icon(@"images\yellow.ico");
    }

    public static class Ext
    {
        public static string Short(this Exception ex) => ex.Message + ex.ToString().Remove(0, ex.ToString().IndexOf(":line"));

        public static void FileCheck(string filename)
        {
            if (!File.Exists(filename)) using (File.Create(filename)) { };
            FileInfo fileInfo = new FileInfo(filename);
            if (fileInfo.Length > 262144) { fileInfo.Delete(); using (File.Create(filename)) { }; }
        }

        [DllImport("wininet.dll")]
        internal extern static bool InternetGetConnectedState(out int Val, int ReservedValue);

        public static decimal TruncateDecimal(decimal value, int precision)
        {
            decimal step = (decimal)Math.Pow(10, precision);
            decimal tmp = Math.Truncate(step * value);
            return tmp / step;
        }
    }

    // Third Party

    public class GridViewSort
    {
        #region Attached properties

        public static ICommand GetCommand(DependencyObject obj)
        {
            return (ICommand)obj.GetValue(CommandProperty);
        }

        public static void SetCommand(DependencyObject obj, ICommand value)
        {
            obj.SetValue(CommandProperty, value);
        }

        // Using a DependencyProperty as the backing store for Command.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.RegisterAttached(
                "Command",
                typeof(ICommand),
                typeof(GridViewSort),
                new UIPropertyMetadata(
                    null,
                    (o, e) =>
                    {
                        ItemsControl listView = o as ItemsControl;
                        if (listView != null)
                        {
                            if (!GetAutoSort(listView)) // Don't change click handler if AutoSort enabled
                            {
                                if (e.OldValue != null && e.NewValue == null)
                                {
                                    listView.RemoveHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
                                }
                                if (e.OldValue == null && e.NewValue != null)
                                {
                                    listView.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
                                }
                            }
                        }
                    }
                )
            );

        public static bool GetAutoSort(DependencyObject obj)
        {
            return (bool)obj.GetValue(AutoSortProperty);
        }

        public static void SetAutoSort(DependencyObject obj, bool value)
        {
            obj.SetValue(AutoSortProperty, value);
        }

        // Using a DependencyProperty as the backing store for AutoSort.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty AutoSortProperty =
            DependencyProperty.RegisterAttached(
                "AutoSort",
                typeof(bool),
                typeof(GridViewSort),
                new UIPropertyMetadata(
                    false,
                    (o, e) =>
                    {
                        ListView listView = o as ListView;
                        if (listView != null)
                        {
                            if (GetCommand(listView) == null) // Don't change click handler if a command is set
                            {
                                bool oldValue = (bool)e.OldValue;
                                bool newValue = (bool)e.NewValue;
                                if (oldValue && !newValue)
                                {
                                    listView.RemoveHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
                                }
                                if (!oldValue && newValue)
                                {
                                    listView.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
                                }
                            }
                        }
                    }
                )
            );

        public static string GetPropertyName(DependencyObject obj)
        {
            return (string)obj.GetValue(PropertyNameProperty);
        }

        public static void SetPropertyName(DependencyObject obj, string value)
        {
            obj.SetValue(PropertyNameProperty, value);
        }

        // Using a DependencyProperty as the backing store for PropertyName.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty PropertyNameProperty =
            DependencyProperty.RegisterAttached(
                "PropertyName",
                typeof(string),
                typeof(GridViewSort),
                new UIPropertyMetadata(null)
            );

        #endregion Attached properties

        #region Column header click event handler

        private static void ColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            GridViewColumnHeader headerClicked = e.OriginalSource as GridViewColumnHeader;
            if (headerClicked != null)
            {
                string propertyName = GetPropertyName(headerClicked.Column);
                if (!string.IsNullOrEmpty(propertyName))
                {
                    ListView listView = GetAncestor<ListView>(headerClicked);
                    if (listView != null)
                    {
                        ICommand command = GetCommand(listView);
                        if (command != null)
                        {
                            if (command.CanExecute(propertyName))
                            {
                                command.Execute(propertyName);
                            }
                        }
                        else if (GetAutoSort(listView))
                        {
                            ApplySort(listView.Items, propertyName);
                        }
                    }
                }
            }
        }

        #endregion Column header click event handler

        #region Helper methods

        public static T GetAncestor<T>(DependencyObject reference) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(reference);
            while (!(parent is T))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent != null)
                return (T)parent;
            else
                return null;
        }

        public static void ApplySort(ICollectionView view, string propertyName)
        {
            ListSortDirection direction = ListSortDirection.Ascending;
            if (view.SortDescriptions.Count > 0)
            {
                SortDescription currentSort = view.SortDescriptions[0];
                if (currentSort.PropertyName == propertyName)
                {
                    if (currentSort.Direction == ListSortDirection.Ascending)
                        direction = ListSortDirection.Descending;
                    else
                        direction = ListSortDirection.Ascending;
                }
                view.SortDescriptions.Clear();
            }
            if (!string.IsNullOrEmpty(propertyName))
            {
                view.SortDescriptions.Add(new SortDescription(propertyName, direction));
            }
        }

        #endregion Helper methods
    }

    public class SysWsLog : ILogEventSink
    {
        private MainWindow Main;

        private object fileAccessLock_syslog = new object();

        public SysWsLog(MainWindow main)
        {
            Main = main;
        }

        public void Emit(LogEvent logEvent)
        {
            if (App.flag_log)
            {
                Main.Dispatcher.Invoke(() =>
                {
                    if (Main.SystemLog.Text.Length > 16384) Main.SystemLog.Text = "";
                    Main.SystemLog.AppendText(logEvent.RenderMessage() + "\n");

                    Main.LBL_SysLog.Content = $"System Log ({Main.SystemLog.Text.Length})";
                });

                lock (fileAccessLock_syslog)
                {
                    try
                    {
                        Ext.FileCheck("syslog.txt");

                        using (var writer = File.AppendText("syslog.txt"))
                        {
                            writer.WriteLine(logEvent.Timestamp.ToString("dd-MMM-yy HH:mm:ss", new CultureInfo("de-DE")) + " " + logEvent.RenderMessage());
                        }
                    }
                    catch { }
                }
            }
        }
    }
}