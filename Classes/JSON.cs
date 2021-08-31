using System;
using System.Collections.Generic;

namespace ExchangeRateServer
{
    public class JSON_CMC_Change
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

    public class JSON_CMC_Currencies
    {
        public class Data
        {
            public string symbol { get; set; }
        }

        public List<Data> data { get; set; }
    }

    public class JSON_Coinbase
    {
        public Data data { get; set; }

        public class Data
        {
            public string Currency { get; set; }

            public Dictionary<string, string> Rates { get; set; }
        }
    }

    public class JSON_Coinbase_Currencies
    {
        public Data[] data { get; set; }

        public string id { get; set; }

        public class Data
        {
            public string Id { get; set; }
        }
    }

    public class JSON_Fixer
    {
        public bool Succeess { get; set; }

        public string Base { get; set; }

        public DateTimeOffset Date { get; set; }

        public Dictionary<string, double> Rates { get; set; }

        public Error error { get; set; }

        public class Error
        {
            public string Code { get; set; }
        }
    }
}