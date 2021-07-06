using System;
using System.Collections.Generic;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace ExchangeRateServer
{
    // WebSocker Server

    public class RequestedPair
    {
        public string CCY1;
        public string CCY2;
    }

    public class ExchangeRateServerInfo
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
        public RequestedPair newPair;
        public Services exchange;
        public History history;
        public List<MarketInfo> markets;

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
        private readonly System.Threading.SemaphoreSlim newMessageQueue = new System.Threading.SemaphoreSlim(1,1);

        private MainWindow Main;

        public ExchangeRateWSS(MainWindow main)
        {
            Main = main;
        }

        protected async override void OnMessage(MessageEventArgs e)
        {
            try
            {
                await newMessageQueue.WaitAsync(new TimeSpan(0,0,10));

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
                        Main.currencyInput.Text = data[1];
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
                                Main.currencyInput.Text = data[1];
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