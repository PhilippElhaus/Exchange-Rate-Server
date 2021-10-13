using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ExchangeRateServer
{
    public partial class MainWindow
    {
        private void Loop_Query_CMC()
        {
            if (string.IsNullOrEmpty(CMCAPIKEY))
            {
                log.Warning("No CMC API Key provided: No Crypto data.");

                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);

                while (true)
                {
                    try
                    {
                        Query_CMC();

                        foreach (var request in Requests)  // Request for Change 1h/24h/7d/30d
                        {
                            string reference = default;

                            Dispatcher.Invoke(() =>
                            {
                                reference = ComboBox_ReferenceCurrency.SelectedItem as string ?? string.Empty;
                            });

                            if (!string.IsNullOrEmpty(reference))
                            {
                                if (!Currencies.Any(x => x == request.Item1 && reference == request.Item2))
                                {
                                    await Query_CMC_Specific(request.Item1, request.Item2);
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

        private async void Query_CMC(bool reference_change = false)
        {
            if (string.IsNullOrEmpty(CMCAPIKEY)) return;

            await Task.Run(() => { _ = cmcQuery.WaitOne(2500); });

            await Task.Run(async () =>
            {
                try
                {
                    _ = AwaitOnline(Services.CMC);

                    Activity();

                    var reference = await ReferenceCurrency_Get();

                    using (WebClient client = new())
                    {
                        client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                        client.Headers.Add("Accepts", "application/json");

                        var currencies = Currencies.ToArray().Where(x => Currencies_CMC_Crypto.Contains(x));

                        if (!reference_change)
                        {
                            var remove = Change.Where(x => DateTime.Now - x.Date < new TimeSpan(0, 5, 0));

                            if (remove.Any())
                            {
                                currencies = currencies.Where(x => !remove.Any(y => y.Base == x));
                            }
                        }

                        var symbols = string.Join(",", currencies);
                        if (string.IsNullOrEmpty(symbols)) return;

                        var url = $"https://pro-api.coinmarketcap.com/v1/cryptocurrency/quotes/latest?symbol={symbols}&convert={reference}";
                        var json = client.DownloadString(url);

                        json = json.Replace("\"quote\":{\"" + $"{reference}" + "\"", "\"quote\":{\"Currency\"");

                        var cmc = JsonConvert.DeserializeObject<JSON_CMC_Change>(json);

                        Dispatcher.Invoke(() =>
                        {
                            foreach (var item in cmc.data)
                            {
                                Change temp = new()
                                {
                                    Quote = reference,
                                    Base = item.Value.symbol,
                                    Change1h = Math.Round(item.Value.quote.currency.percent_change_1h, 2),
                                    Change24h = Math.Round(item.Value.quote.currency.percent_change_24h, 2),
                                    Change7d = Math.Round(item.Value.quote.currency.percent_change_7d, 2),
                                    Change30d = Math.Round(item.Value.quote.currency.percent_change_30d, 2),
                                    Date = DateTime.Now
                                };

                                var entry = Change.SingleOrDefault(x => x.Base == temp.Base);

                                lock (lock_currencyChange)
                                {
                                    if (entry == default)
                                    {
                                        Change.Add(temp);
                                    }
                                    else
                                    {
                                        _ = Change.Remove(entry);

                                        Change.Add(temp);
                                    }
                                }
                            }
                        });

                        if (reference_change) log.Information($"CMC: Added {cmc.data.Count} currencies for new reference currency [{reference}].");
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("408"))
                {
                    log.Error($"CMC Query: General Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("504"))
                {
                    log.Error($"CMC Query: Gateway Timeout");
                }
                catch (Exception ex) when (ex.Message.Contains("404"))
                {
                    log.Error("CMC Query: Not found");
                }
                catch (Exception ex) when (ex.Message.Contains("500"))
                {
                    log.Error("CMC Query: Internal Server Error");
                }
                catch (Exception ex) when (ex.Message.Contains("406"))
                {
                    log.Error($"Query unavailable at CMC.");
                }
                catch (Exception ex) when (ex.Message.Contains("403"))
                {
                    log.Error($"Query unavailable at CMC.");
                }
                catch (Exception ex)
                {
                    log.Error($"CMC Query: {ex.Short()}");
                }
            });

            _ = cmcQuery.Set();
        }

        private Task Query_CMC_Specific(string baseCurrency, string quoteCurrency, bool manual = false)
        {
            return Task.Run(async () =>
            {
                if (string.IsNullOrEmpty(CMCAPIKEY)) return;

                await Task.Run(() => { _ = cmcQuery.WaitOne(2500); });

                try
                {
                    _ = AwaitOnline(Services.CMC);

                    Activity();

                    var entry = Change_Specific.FirstOrDefault(x => x.Base == baseCurrency && x.Quote == quoteCurrency);

                    if (entry != default && (DateTime.Now - AGE_CMC_CHANGE < entry.Date) && manual == false) return;

                    using (WebClient client = new())
                    {
                        client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                        client.Headers.Add("Accepts", "application/json");

                        var url = $"https://pro-api.coinmarketcap.com/v1/cryptocurrency/quotes/latest?symbol={baseCurrency}&convert={quoteCurrency}&aux=volume_7d,volume_30d";
                        var json = client.DownloadString(url);

                        json = json.Replace("\"quote\":{\"" + $"{quoteCurrency}" + "\"", "\"quote\":{\"Currency\"");

                        var cmc = JsonConvert.DeserializeObject<JSON_CMC_Change>(json);

                        Change temp = new()
                        {
                            Quote = quoteCurrency,
                            Base = baseCurrency,
                            Change1h = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_1h, 2),
                            Change24h = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_24h, 2),
                            Change7d = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_7d, 2),
                            Change30d = Math.Round(cmc.data[baseCurrency].quote.currency.percent_change_30d, 2),
                            Date = DateTime.Now
                        };

                        lock (lock_currencyChange_Specific)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (entry == default)
                                {
                                    Change_Specific.Add(temp);
                                }
                                else
                                {
                                    _ = Change_Specific.Remove(entry);

                                    Change_Specific.Add(temp);
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
                    var entry = Change_Specific.FirstOrDefault(x => x.Base == baseCurrency && x.Quote == quoteCurrency);
                    if (entry != default)
                    {
                        if (Change_Specific.Remove(entry))
                        {
                            log.Error($"[{baseCurrency}/{quoteCurrency}] Changes unavailable at CMC: Removed.");
                        }
                        else
                        {
                            log.Error($"[{baseCurrency}/{quoteCurrency}] Changes unavailable at CMC.");
                        }
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("406"))
                {
                    log.Error($"[{baseCurrency}/{quoteCurrency}] unavailable at CMC.");
                }
                catch (Exception ex) when (ex.Message.Contains("403"))
                {
                    log.Error($"[{baseCurrency}/{quoteCurrency}] unavailable at CMC.");
                }
                catch (Exception ex)
                {
                    log.Error($"Error in Specific CMC Query: {ex.Short()}");
                }

                _ = cmcQuery.Set();
            });
        }

        private Task Check_Currencies_CMC()
        {
            return Task.Run(async () =>
            {
                if (string.IsNullOrEmpty(CMCAPIKEY)) return;

                var failCounter = 0;
                while (failCounter < 10)
                {
                    _ = AwaitOnline(Services.CMC);

                    var ctr = 0;

                    try
                    {
                        Currencies_CMC_Fiat.Clear();
                        Currencies_CMC_Crypto.Clear();

                        using (WebClient client = new())
                        {
                            client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                            client.Headers.Add("Accepts", "application/json");

                            {
                                var json = client.DownloadString("https://pro-api.coinmarketcap.com/v1/fiat/map");

                                var currencies = JsonConvert.DeserializeObject<JSON_CMC_Currencies>(json);

                                foreach (var fiat in currencies.data)
                                {
                                    if (!Currencies_CMC_Fiat.Contains(fiat.symbol))
                                    {
                                        ctr++;
                                        Currencies_CMC_Fiat.Add(fiat.symbol);
                                    }
                                }
                            }

                            Currencies_CMC_Fiat = Currencies_CMC_Fiat.OrderBy(x => x).ToList();
                        }

                        using (WebClient client = new())
                        {
                            client.Headers.Add("X-CMC_PRO_API_KEY", CMCAPIKEY);
                            client.Headers.Add("Accepts", "application/json");
                            {
                                var json = client.DownloadString("https://pro-api.coinmarketcap.com/v1/cryptocurrency/map");

                                var currencies = JsonConvert.DeserializeObject<JSON_CMC_Currencies>(json);

                                foreach (var crypto in currencies.data)
                                {
                                    if (!Currencies_CMC_Crypto.Contains(crypto.symbol))
                                    {
                                        ctr++;
                                        Currencies_CMC_Crypto.Add(crypto.symbol);
                                    }
                                }
                            }

                            Currencies_CMC_Crypto = Currencies_CMC_Crypto.OrderBy(x => x).ToList();
                        }

                        log.Information($"Found {ctr} Currencies at CMC.");

                        Dispatcher.Invoke(() =>
                        {
                            ComboBox_ReferenceCurrency.ItemsSource = Currencies_CMC_Fiat.Intersect(Currencies_Fixer);

                            if (REFERENCECURRENCY != null)
                            {
                                var idx = Currencies_CMC_Fiat.IndexOf(REFERENCECURRENCY);

                                if (idx != -1)
                                {
                                    ComboBox_ReferenceCurrency.SelectedIndex = idx;
                                }
                            }
                        });

                        break;
                    }
                    catch (Exception ex) when (ex.Message.Contains("401"))
                    {
                        log.Error("Querying CMC currencies: Unauthorized.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        failCounter++;
                        log.Error($"Querying CMC currencies: {ex.Short()}");
                        await Task.Delay(5000 * failCounter);
                    }
                }
            });
        }
    }
}