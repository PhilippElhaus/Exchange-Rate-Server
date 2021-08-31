using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace ExchangeRateServer
{
    public partial class MainWindow
    {
        private void Loop_WSS()
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

                WSSV = new WebSocketServer(WSSPORT);
                WSSV.AddWebSocketService(WSSENDPOINT, () => new WSS_Behavior(this));
                WSSV.Start();

                while (!WSSV.IsListening) { await Task.Delay(5000); Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"WSSV awaits startup ..."; }); }
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
                                WebSocketServerStatus.Inlines.Add(new Run("ws://" + Dns.GetHostEntry(Dns.GetHostName()).AddressList.First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString() + ":" + WSSV.Port.ToString() + WSSENDPOINT + "\n") { FontSize = 9 });
                                WebSocketServerStatus.Inlines.Add(new Run(WSSV.WebSocketServices.SessionCount.ToString()) { FontSize = 26 });
                            });

                            WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new WSS_Communication()
                            {
                                info = WSS_Communication.ExRateInfoType.Rates,
                                success = true,
                                currencies = Currencies,
                                currencies_change = Change?.Union(Change_Specific).ToList(),
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
                    lock (lock_historySend)
                    {
                        if (WSSV != null)
                        {
                            if (History_Long.ContainsKey((CCY1, CCY2)) && History_Long.ContainsKey((CCY1, CCY2)))
                            {
                                WSS_Communication cast = new WSS_Communication()
                                {
                                    success = true,
                                    info = WSS_Communication.ExRateInfoType.History,
                                    history = new WSS_Communication.History() { @base = CCY1, quote = CCY2, historyLong = new List<TimeData>(History_Long[(CCY1, CCY2)]), historyShort = new List<TimeData>(History_Short[(CCY1, CCY2)]) }
                                };

                                WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));

                                Dispatcher.Invoke(() =>
                                {
                                    ExchangeRateInfo.Text = $"Sent history for [{CCY1}/{CCY2}].";
                                });
                            }
                            else
                            {
                                WSS_Communication cast = new WSS_Communication()
                                {
                                    success = false,
                                    message = $"[{CCY1}/{CCY2}] history not available.",
                                    info = WSS_Communication.ExRateInfoType.History,
                                    history = new WSS_Communication.History() { @base = CCY1, quote = CCY2 }
                                };

                                WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));

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
                    lock (lock_newCurrency)
                    {
                        Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Verifying {candidate}..."; });

                        if (Currencies.Contains(candidate))
                        {
                            Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"{candidate} already supported."; });

                            if (WSSV != null)
                            {
                                WSS_Communication cast = new WSS_Communication()
                                {
                                    success = false,
                                    message = $"Already supported: [{candidate}]",

                                    info = WSS_Communication.ExRateInfoType.NewCurrency,
                                    newCurrency = candidate
                                };

                                WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
                            }

                            return;
                        }
                        else if ((Currencies_Bitfinex.Contains(candidate) || Currencies_Coinbase.Contains(candidate)) && candidate != "BCH")
                        {
                            Currencies.Add(candidate);

                            Dispatcher.Invoke(() =>
                            {
                                DG_Currencies.ItemsSource = null;
                                DG_Currencies.ItemsSource = Currencies;
                                TB_CurrencyInput.Text = "";
                            });

                            log.Information($"Added new Currency: {candidate}");

                            if (WSSV != null)
                            {
                                WSS_Communication cast = new WSS_Communication()
                                {
                                    success = true,
                                    info = WSS_Communication.ExRateInfoType.NewCurrency,
                                    newCurrency = candidate
                                };
                                WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
                            }

                            Task.Run(async () =>
                            {
                                if (justAdded) return;
                                else justAdded = true;

                                await Task.Delay(5000);

                                Query_CMC();
                                Query_Fixer();

                                justAdded = false;
                            });

                            return;
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                TB_CurrencyInput.Text = "";
                                ExchangeRateInfo.Text = $"{candidate} not supported.";
                            });

                            if (WSSV != null)
                            {
                                WSS_Communication cast = new WSS_Communication()
                                {
                                    success = false,
                                    message = $"{candidate} not supported.",
                                    info = WSS_Communication.ExRateInfoType.NewCurrency,
                                    newCurrency = candidate
                                };

                                WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
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

                if (Requests.Where(x => x.Item1 == CCY1 && x.Item2 == CCY2 && x.Item3 == Exchange).Any())
                {
                    ResultNotification($"Already supported: [{CCY1}/{CCY2}] @ {exchange}", false);
                }
                else
                {
                    if (Exchange == Services.Bitfinex)
                    {
                        if (!Currencies_Bitfinex.Contains(CCY1) || !Currencies_Bitfinex.Contains(CCY2))
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
                        if (!Currencies_Coinbase.Contains(CCY1) || !Currencies_Coinbase.Contains(CCY2))
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
                    Requests.Add((CCY1, CCY2, Exchange));

                    Query_CMC_Specific(CCY1, CCY2);

                    log.Information($"Added new Pair: [{CCY1}/{CCY2}] @ {exchange}");
                }

                void ResultNotification(string message, bool success)
                {
                    if (WSSV != null)
                    {
                        WSS_Communication cast = new WSS_Communication()
                        {
                            success = success,
                            message = message,
                            newPair = new WSS_RequestedPair() { Base = CCY1, Quote = CCY2 },
                            info = WSS_Communication.ExRateInfoType.SpecificPair
                        };

                        WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(cast));
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
                    if (WSSV != null)
                    {
                        if (Markets_Bitfinex?.Count > 0)
                        {
                            WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new WSS_Communication()
                            {
                                success = true,
                                message = $"{Markets_Bitfinex.Count} Bitfinex Markets available.",
                                markets = Markets_Bitfinex.ToList(),
                                info = WSS_Communication.ExRateInfoType.Markets
                            }));
                        }
                        else
                        {
                            WSSV.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new WSS_Communication()
                            {
                                success = false,
                                message = "No Bitfinex Markets available.",
                                info = WSS_Communication.ExRateInfoType.Markets
                            }));
                        }
                    }
                }
            });
        }
    }

    public class WSS_RequestedPair
    {
        public string Base;
        public string Quote;
    }

    public class WSS_Communication
    {
        public enum ExRateInfoType
        {
            Rates = 1,
            History = 2,
            NewCurrency = 3,
            SpecificPair = 4,
            Markets = 5
        }

        public ExRateInfoType info;
        public List<ExchangeRate> rates;
        public List<Change> currencies_change;
        public List<string> currencies;

        public string newCurrency;
        public WSS_RequestedPair newPair;
        public Services exchange;
        public History history;
        public List<Market> markets;

        public bool success;
        public string message;

        public class History
        {
            public string @base;
            public string quote;
            public List<TimeData> historyShort;
            public List<TimeData> historyLong;
        }
    }

    public class WSS_Behavior : WebSocketBehavior
    {
        private readonly System.Threading.SemaphoreSlim newMessageQueue = new(1, 1);

        private readonly MainWindow Main;

        public WSS_Behavior(MainWindow main)
        {
            Main = main;
        }

        protected async override void OnMessage(MessageEventArgs e)
        {
            try
            {
                await newMessageQueue.WaitAsync(new TimeSpan(0, 0, 10));

                if (e.Data.StartsWith("HISTORY")) // HISTORY.BTC.EUR
                {
                    var data = e.Data.ToUpper().Split('.');
                    if (data.Length == 3)
                    {
                        await Main.WSS_History(data[1], data[2]);

                        Main.Dispatcher.Invoke(() =>
                        {
                            Main.ExchangeRateInfo.Text = $"Request for Historic Data on [{data[1]}/{data[2]}] received.";
                        });
                    }
                    else if (data.Length == 2)
                    {
                        await Main.WSS_History(data[1], "EUR");

                        Main.Dispatcher.Invoke(() =>
                        {
                            Main.ExchangeRateInfo.Text = $"Request for Historic Data on [{data[1]}/EUR] received.";
                        });
                    }
                }
                else if (e.Data.StartsWith("CURRENCY")) // CURRENCY.BTC
                {
                    var data = e.Data.ToUpper().Split('.');

                    Main.Dispatcher.Invoke(() =>
                    {
                        Main.TB_CurrencyInput.Text = data[1];
                        Main.ExchangeRateInfo.Text = $"Verifying addition of {data[1]} ...";
                    });

                    await Main.WSS_AddCurrency(data[1]);
                }
                else if (e.Data.StartsWith("PAIR")) // PAIR.BTC.EUR.EXCHANGE
                {
                    var data = e.Data.ToUpper().Split('.');

                    if (data.Length < 4)
                    {
                        Main.Dispatcher.Invoke(() =>
                        {
                            Main.ExchangeRateInfo.Text = $"Incomplete Request Data. Aborted.";
                        });
                    }
                    else
                    {
                        if (data[1] != data[2])
                        {
                            Main.Dispatcher.Invoke(() =>
                            {
                                Main.TB_CurrencyInput.Text = data[1];
                                Main.ExchangeRateInfo.Text = $"Verifying Pair [{data[1]}/{data[2]}] @ {data[3]}...";
                            });

                            await Main.WSS_AddTradingPair(data[1], data[2], data[3]);
                        }
                        else
                        {
                            Main.Dispatcher.Invoke(() =>
                            {
                                Main.ExchangeRateInfo.Text = $"Currencies are identical. Aborted.";
                            });
                        }
                    }
                }
                else if (e.Data.StartsWith("MARKETS")) // MARKETS.MARKET
                {
                    Main.Dispatcher.Invoke(() =>
                    {
                        Main.ExchangeRateInfo.Text = $"Casting Market Info ...";
                    });

                    var split = e.Data.Split('.');

                    await Main.WSS_SendMarketInfo(split[1]);
                }
                else
                {
                    Main.log.Information($"Unable to process '{e.Data}'");
                }

                _ = newMessageQueue.Release();
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
                Main.ExchangeRateInfo.Text = $"Client {(e.WasClean ? "clean" : "ungraceful")} disconnected: {e.Code}";
            });
        }
    }
}