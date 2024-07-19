using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ExchangeRateServer;

public partial class MainWindow
{
	private void Loop_Market_Bitfinex()
	{
		_ = Task.Run(async () =>
		{
			while (true)
			{
				_ = AwaitOnline(Services.Bitfinex);

				Activity();

				try
				{
					using (WebClient webClient = new())
					{
						var deserialized = JsonConvert.DeserializeObject<List<Market>>(webClient.DownloadString("https://api.bitfinex.com/v1/symbols_details"));

						Dispatcher.Invoke(() =>
						{
							Markets_Bitfinex = new ObservableCollection<Market>(deserialized.Where(x => !x.Pair.Contains(":")));
						});
					}
				}
				catch (Exception ex) when (ex.Message.Contains("Service Temporarily Unavailable") || ex.Message.Contains("503"))
				{
					Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"BFX Markets temporarily unavailable..."; });
				}
				catch (Exception ex)
				{
					log.Error($"Bitfinex Market Info: {ex.Short()}");
				}
				finally
				{
					await Task.Delay(new TimeSpan(1, 0, 0));
				}
			}
		});
	}

	private void Check_Currencies_Bitfinex()
	{
		_ = Task.Run(async () =>
		{
			var failCounter = 0;
			while (failCounter < 10)
			{
				if (!AwaitOnline(Services.Bitfinex, true))
					return;

				try
				{
					using (WebClient webclient = new())
					{
						var json = webclient.DownloadString("https://api-pub.bitfinex.com/v2/conf/pub:list:currency");

						json = json.Remove(0, 1);
						json = json.Remove(json.Length - 1);

						var currencies = JsonConvert.DeserializeObject<string[]>(json);

						foreach (var item in currencies)
						{
							Currencies_Bitfinex.Add(item);
						}

						log.Information($"Found {currencies.Length} Currencies at Bitfinex.");
						break;
					}
				}
				catch (Exception ex) when (ex.Message.Contains("Service Temporarily Unavailable") || ex.Message.Contains("503"))
				{
					Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"BFX Currencies temporarily unavailable..."; });
				}
				catch (Exception ex)
				{
					failCounter++;
					log.Error($"Querying Bitfinex currencies: {ex.Short()}");
					await Task.Delay(5000 * failCounter);
				}
			}
		});
	}

	private async Task Rate_Bitfinex(string base_currency, string quote_currency = default)
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
								rate = Rates.FirstOrDefault(x => x.Base == base_currency && x.Quote == quote_currency_ && x.Exchange == Services.Bitfinex);
							});

							if (rate != default) // Update
							{
								var maxAge = Res.FIAT.Contains(base_currency) && Res.FIAT.Contains(quote_currency_) ? AGE_FIAT2FIAT_RATE : AGE_MIXEDRATE_RATE;
								if (DateTime.Now - rate.Date > maxAge)
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

										if (DateTime.Now - new TimeSpan(1, 0, 0) > History_Long[(base_currency, quote_currency_)].Last().Time)
										{
											lock (lock_history)
											{
												History_Long[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

												if (DateTime.Now - maxAgeLongHistory > History_Long[(base_currency, quote_currency_)][0].Time)
												{
													History_Long[(base_currency, quote_currency_)].RemoveAt(0);
												}
											}
										}

										if (DateTime.Now - new TimeSpan(0, 1, 0) > History_Short[(base_currency, quote_currency_)].Last().Time)
										{
											lock (lock_history)
											{
												History_Short[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

												if (DateTime.Now - maxAgeShortHistory > History_Short[(base_currency, quote_currency_)][0].Time)
												{
													History_Short[(base_currency, quote_currency_)].RemoveAt(0);
												}
											}
										}
									}
									else
									{
										continue;
									}
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
											Base = base_currency,
											Quote = quote_currency_,
											Date = DateTime.Now,
											Rate = res
										});
									});

									log.Information($"[{base_currency}/{quote_currency_}] via Bitfinex");

									// Check for Pre-Created Historic Entries

									lock (lock_history)
									{
										if (!History_Short.Any(x => x.Key == (base_currency, quote_currency_)))
											History_Short.Add((base_currency, quote_currency_), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
										else
											History_Short[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

										if (!History_Long.Any(x => x.Key == (base_currency, quote_currency_)))
											History_Long.Add((base_currency, quote_currency_), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
										else
											History_Long[(base_currency, quote_currency_)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });
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
						rate = Rates.FirstOrDefault(x => x.Base == base_currency && x.Quote == quote_currency && x.Exchange == Services.Bitfinex);
					});

					if (rate != default) // Update
					{
						var maxAge = Res.FIAT.Contains(base_currency) && Res.FIAT.Contains(quote_currency) ? AGE_FIAT2FIAT_RATE : AGE_MIXEDRATE_RATE;
						if (DateTime.Now - rate.Date > maxAge)
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

								if (DateTime.Now - new TimeSpan(1, 0, 0) > History_Long[(base_currency, quote_currency)].Last().Time)
								{
									lock (lock_history)
									{
										History_Long[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

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
										History_Short[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

										if (DateTime.Now - maxAgeShortHistory > History_Short[(base_currency, quote_currency)][0].Time)
										{
											History_Short[(base_currency, quote_currency)].RemoveAt(0);
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
									Base = base_currency,
									Quote = quote_currency,
									Date = DateTime.Now,
									Rate = res
								});
							});

							log.Information($"[{base_currency}/{quote_currency}] via Bitfinex");

							// Check for Pre-Created Historic Entries

							lock (lock_history)
							{
								if (!History_Short.Any(x => x.Key == (base_currency, quote_currency)))
									History_Short.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
								else
									History_Short[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });

								if (!History_Long.Any(x => x.Key == (base_currency, quote_currency)))
									History_Long.Add((base_currency, quote_currency), new List<TimeData>() { new TimeData() { Time = DateTime.Now, Rate = (double)res } });
								else
									History_Long[(base_currency, quote_currency)].Add(new TimeData() { Time = DateTime.Now, Rate = (double)res });
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				log.Error($"Querying Exchange Rate (Bitfinex): {ex.Short()}");
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

				var body = "{ \"ccy1\": " + $"\"{ccy1}\", \"ccy2\":" + $" \"{ccy2}\"" + "}";
				var bodyBytes = Encoding.UTF8.GetBytes(body);

				webRequest.Method = "POST";
				webRequest.Timeout = 5000;
				webRequest.ContentLength = bodyBytes.Length;
				webRequest.ContentType = "application/json";

				webRequest.GetRequestStream().Write(bodyBytes, 0, bodyBytes.Length);

				HttpWebResponse resp = webRequest.GetResponse() as HttpWebResponse;

				using (StreamReader sRead = new(resp.GetResponseStream()))
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
			catch (Exception ex) when (ex.Message.Contains("This is usually a temporary error during hostname resolution"))
			{
				Dispatcher.Invoke(() => { ExchangeRateInfo.Text = $"Hostname Resolution Problem: [{ccy1}/{ccy2}]"; });
				return default;
			}
			catch (Exception ex)
			{
				log.Error($"Error: [{ccy1}/{ccy2}] {ex.Message}");
				return default;
			}
		}
	}
}