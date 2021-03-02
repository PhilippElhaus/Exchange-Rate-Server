using System;
using System.Collections.Generic;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace ExchangeRateServer
{
    // WebSocker Server

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
                        if (data.Length == 3)
                        {
                            Main.WSS_History(data[1], data[2]);

                            Main.Dispatcher.Invoke(() =>
                            {
                                Main.ExchangeRateInfo.Text = $"Request for Historic Data on [{data[1]}/{data[2]}] received.";
                            });
                        }
                        else if (data.Length == 2)
                        {
                            Main.WSS_History(data[1], "EUR");

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
                Main.ExchangeRateInfo.Text = $"Client {(e.WasClean ? "clean" : "ungraceful")} disconnected: {e.Code}";
            });
        }
    }
}