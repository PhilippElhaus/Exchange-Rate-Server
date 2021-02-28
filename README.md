# CryptoExchangeRateServer

Exchange Rate Server for Cryptocurrencies (Using the FREE plans of Fixer.IO API, Bitfinex API, Coinmarketcap API and Coinbase API)

<img src="flow.png">

## Built with .NET 4.7.2 on WPF

The *Exchange Rate Server* casts latest **Rate & Changes (1h,24h,7d,30d)** as well as available Markets (BitcoinDE & BitfinexCOM) and on request historic rates via a WebSocketServer on the Network.
Auto-generates pairs based on the addition of new Currencies by manual input or WebSocket request. Casts updates every 3secs.

* Ideal for Windows Server
* Access via Tray Icon
* Low Resource Usage

To add a new Currency via a Websocket Client *CURRENCY.CURRENCY* (e.g. CURRENCY.CHF) to the Websocket Server.

To request recorded rates of up to three hours, send *HISTORY.CURRENCY* (e.g. HISTORY.CHF) to the WebSocket Server.

<img src="1.png" width="350">
<img src="2.png" width="350">



## Start-Up Flags

Usage: **ExchangeRateServer.exe /flag**

Flag _/log_: Log Tab & Log File.

Flag _/markets_: Casts additional info on available markets.

## Modify Settings by editing the *config.txt* with the following entries

      FIXERAPIKEY=yourapikey
      CMCAPIKEY=yourapikey
      WSSENDPOINT=/yourendpoint
      REFERENCECURRENCY=EUR
      PORT=222

## Sample JSON Websocket Server Cast

<img src="json.png" width="350">
