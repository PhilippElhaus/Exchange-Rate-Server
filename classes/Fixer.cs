using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ExchangeRateServer
{
    public partial class MainWindow
    {
        private void Loop_Query_Fixer()
        {
            if (string.IsNullOrEmpty(FIXERAPIKEY))
            {
                log.Warning("No Fixer.io API Key provided: No Fiat data.");

                return;
            }

            _ = Task.Run(async () =>
              {
                  await Task.Delay(5000);

                  while (true)
                  {
                      try
                      {
                          Query_Fixer();

                          if (Currencies_Fixer.Count == 0) await Check_Currencies_Fixer(true);
                      }
                      finally
                      {
                          await Task.Delay(AGE_FIXER_CHANGE);
                      }
                  }
              });
        }

        private async void Query_Fixer(bool reference_change = false)
        {
            if (fixerQuery || string.IsNullOrEmpty(FIXERAPIKEY)) return;
            else fixerQuery = true;

            await Task.Run(async () =>
            {
                try
                {
                    _ = AwaitOnline(Services.Fixer);

                    Activity();

                    var reference = await ReferenceCurrency_Get();

                    var currencies = Currencies.Where(x => Currencies_Fixer.Contains(x) && !Currencies_CMC_Crypto.Contains(x) && x != reference);

                    var symbol = string.Join(",", currencies.ToArray());

                    var dates = new string[] { "latest", (DateTime.Now - new TimeSpan(1, 0, 0, 0)).ToString("yyyy-MM-dd"), (DateTime.Now - new TimeSpan(7, 0, 0, 0)).ToString("yyyy-MM-dd"), (DateTime.Now - new TimeSpan(30, 0, 0, 0)).ToString("yyyy-MM-dd") };

                    var query = new JSON_Fixer[4];

                    using (WebClient webclient = new())
                    {
                        for (var i = 0; i < 4; i++)
                        {
                            var json = webclient.DownloadString($"http://data.fixer.io/api/{dates[i]}?access_key={FIXERAPIKEY}&base=EUR&symbols={symbol + (reference == "EUR" ? null : $",{reference}")}");
                            query[i] = JsonConvert.DeserializeObject<JSON_Fixer>(json);
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
                                Quote = reference,
                                Base = currency,
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
                                Quote = reference,
                                Base = currency,
                                Change1h = 0,
                                Change24h = Math.Round(((query[1].Rates[currency] / query[0].Rates[currency] / (query[1].Rates[reference] / query[0].Rates[reference])) - 1) * 100, 2),
                                Change7d = Math.Round(((query[2].Rates[currency] / query[0].Rates[currency] / (query[2].Rates[reference] / query[0].Rates[reference])) - 1) * 100, 2),
                                Change30d = Math.Round(((query[3].Rates[currency] / query[0].Rates[currency] / (query[3].Rates[reference] / query[0].Rates[reference])) - 1) * 100, 2),
                                Date = DateTime.Now
                            };
                        }

                        UpdateEntry(temp);
                    }

                    if (reference_change) log.Information($"Fixer.io: Added {currencies.Count()} currencies for new reference currency [{reference}].");

                    async void RemovePreviousReferenceCurrency()
                    {
                        var ref_entry = Change.SingleOrDefault(x => x.Base == reference);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (ref_entry != default)
                            {
                                _ = Change.Remove(ref_entry);
                            }
                        });
                    }

                    async void UpdateEntry(Change temp)
                    {
                        var entry = Change.SingleOrDefault(x => x.Base == temp.Base);

                        await Dispatcher.InvokeAsync(() =>
                        {
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

        private Task Check_Currencies_Fixer(bool fromLoop = false)
        {
            return Task.Run(async () =>
            {
                if (string.IsNullOrEmpty(FIXERAPIKEY))
                {
                    log.Warning("Unable to request Fixer Currencies: Missing API Key.");
                    return;
                }

                while (true)
                {
                   _ = AwaitOnline(Services.Fixer);

                    try
                    {
                        using (WebClient webclient = new())
                        {
                            var json = webclient.DownloadString($"http://data.fixer.io/api/symbols?access_key={FIXERAPIKEY}");

                            var converter = JsonConvert.DeserializeObject<JSON_Fixer>(json);
                            if (converter?.Succeess == false && converter?.error?.Code == "104")
                            {
                                if (!fromLoop) log.Information($"Fixer.io requests exceeded. [Currencies]");
                                break;
                            }
                        
                            json = json.Remove(0, json.IndexOf(":{") + 2);
                            json = json.Remove(json.IndexOf("}}"));

                            var split = json.Split(new char[] { ',', ':' }, StringSplitOptions.RemoveEmptyEntries);

                            Currencies_Fixer = split.Where(x => x.Length == 5).Select(x => x.Trim('"')).ToList();
                        }
                        if (Currencies_Fixer.Count > 0) log.Information($"Found {Currencies_Fixer.Count} Currencies at Fixer.io");

                        Dispatcher.Invoke(() =>
                        {
                            ComboBox_ReferenceCurrency.ItemsSource = Currencies_Fixer.Intersect(Currencies_CMC_Fiat);

                            if (REFERENCECURRENCY != null)
                            {
                                var idx = Currencies_Fixer.IndexOf(REFERENCECURRENCY);

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
                        log.Error($"Querying Fixer.io currencies: {ex.Short()}");
                        await Task.Delay(10000);
                    }
                }
            });
        }
    }
}