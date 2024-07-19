using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace ExchangeRateServer;

public partial class MainWindow
{
	private void Check_Currencies_Coinbase()
	{
		_ = Task.Run(async () =>
		  {
			  var fiatCtr = 0;
			  var cryptoCtr = 0;

			  try
			  {
				  Task fiat = Task.Run(async () =>
				  {
					  using (WebClient client = new())
					  {
						  while (true)
						  {
							  _ = AwaitOnline(Services.Coinbase);

							  try
							  {
								  var json_fiat = client.DownloadString("https://api.coinbase.com/v2/currencies");

								  var currencies_fiat = JsonConvert.DeserializeObject<JSON_Coinbase_Currencies>(json_fiat);

								  foreach (var item in currencies_fiat.data)
								  {
									  fiatCtr++;
									  Currencies_Coinbase.Add(item.Id);
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
					  using (WebClient client = new())
					  {
						  while (true)
						  {
							  _ = AwaitOnline(Services.Coinbase);

							  try
							  {
								  var json_crypto = client.DownloadString("https://api.pro.coinbase.com/currencies");

								  var currencies_crypto = JsonConvert.DeserializeObject<List<JSON_Coinbase_Currencies>>(json_crypto);

								  foreach (var item in currencies_crypto)
								  {
									  if (!Currencies_Coinbase.Contains(item.id))
									  {
										  cryptoCtr++;
										  Currencies_Coinbase.Add(item.id);
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
				  log.Error($"Querying Coinbase currencies: {ex.Short()}");
			  }
		  });
	}

	private async Task Rate_Coinbase(string base_currency)
	{
		try
		{
			var maxAge = AGE_FIAT2FIAT_RATE > AGE_MIXEDRATE_RATE ? AGE_MIXEDRATE_RATE : AGE_FIAT2FIAT_RATE;
			if (!Rates.Any(x => x.Base == base_currency) || !Rates.Any(x => x.Quote == base_currency) || Rates.Any(x => (x.Base == base_currency || x.Quote == base_currency) && (DateTime.Now - x.Date > maxAge)))
			{
				try
				{
					Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Checking [{base_currency}]..."; });

					using (WebClient webClient = new())
					{
						string json = default;

						try
						{
							json = webClient.DownloadString($"https://api.coinbase.com/v2/exchange-rates?currency={base_currency}");
						}
						catch
						{
							if (Currencies_Bitfinex.Contains(base_currency))
							{
								await Rate_Bitfinex(base_currency);
								return;
							}
						}

						var deserialized = JsonConvert.DeserializeObject<JSON_Coinbase>(json);
						var cur = Currencies.ToArray();

						foreach (var quote_currency in cur)
						{
							if (quote_currency != base_currency)
							{
								if (deserialized.data.Rates.ContainsKey(quote_currency))
								{
									if (Requests.FirstOrDefault(x => x.Item1 == base_currency && x.Item2 == quote_currency && x.Item3 != Services.Coinbase) != default)
									{
										continue;
									}

									if (Rates.ToList().Exists(x => x.Base == base_currency && x.Quote == quote_currency)) // Update
									{
										var exrEntry = Rates.FirstOrDefault(x => x.Base == base_currency && x.Quote == quote_currency && x.Exchange == Services.Coinbase);

										if (exrEntry != default)
										{
											exrEntry.Date = DateTime.Now;
											exrEntry.Exchange = Services.Coinbase;

											exrEntry.Rate = Res.FIAT.Contains(quote_currency) && !Res.FIAT.Contains(base_currency)
												? Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2)
												: Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);

											Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Checking [{base_currency}]... updated."; });

											if (DateTime.Now - new TimeSpan(1, 0, 0) > History_Long[(base_currency, quote_currency)].Last().Time)
											{
												lock (lock_history)
												{
													History_Long[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)exrEntry.Rate });

													if (DateTime.Now - maxAgeLongHistory > History_Long[(base_currency, quote_currency)][0].Time)
													{
														History_Long[(base_currency, quote_currency)].RemoveAt(0);
													}
												}
											}

											if (DateTime.Now - new TimeSpan(0, 1, 0) > History_Short[(base_currency, quote_currency)].Last().Time)
											{
												lock (lock_history)
												{
													History_Short[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)exrEntry.Rate });

													if (DateTime.Now - maxAgeShortHistory > History_Short[(base_currency, quote_currency)][0].Time)
													{
														History_Short[(base_currency, quote_currency)].RemoveAt(0);
													}
												}
											}
										}
									}
									else // New
									{
										ExchangeRate exr = new()
										{
											Exchange = Services.Coinbase,
											Base = base_currency,
											Quote = quote_currency,
											Date = DateTime.Now,
										};

										exr.Rate = Res.FIAT.Contains(quote_currency) && !Res.FIAT.Contains(base_currency)
											? Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 2)
											: Ext.TruncateDecimal(decimal.Parse(deserialized.data.Rates[quote_currency], NumberStyles.Float, new NumberFormatInfo() { NumberDecimalSeparator = "." }), 8);

										Dispatcher.Invoke(() =>
										{
											Rates.Add(exr);
										});

										log.Information($"[{exr.Base}/{exr.Quote}] via Coinbase");

										if (!History_Long.ContainsKey((base_currency, quote_currency)))
										{
											lock (lock_history)
											{
												History_Long.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)exr.Rate } });
											}
										}

										if (!History_Short.ContainsKey((base_currency, quote_currency)))
										{
											lock (lock_history)
											{
												History_Short.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)exr.Rate } });
											}
										}
									}
								}
								else
								{
									await Rate_Bitfinex(base_currency, quote_currency);
								}
							}
						}
					}
				}
				catch (FormatException ex)
				{
					log.Error($"Format: {ex.Short()}");
				}
				catch (Exception ex)
				{
					log.Error($"Exchange Rate Coinbase: {ex.Short()}");

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
			log.Error($"Querying Exchange Rate (Coinbase): {ex.Short()}");
		}
	}
}