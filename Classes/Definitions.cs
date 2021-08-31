using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace ExchangeRateServer
{
    public enum Services
    {
        None = 0,
        Fixer = 1,
        CMC = 2,
        Bitfinex = 3,
        Coinbase = 4
    }

    public class Change
    {
        public string Base { get; set; }
        public string Quote { get; set; }
        public double Change1h { get; set; }
        public double Change24h { get; set; }
        public double Change7d { get; set; }
        public double Change30d { get; set; }
        public DateTime Date { get; set; }
    }

    public struct TimeData
    {
        public DateTime Time { get; set; }
        public double Rate { get; set; }
    }

    public class Market
    {
        private string pair;

        public string Pair
        {
            get => pair;
            set
            {
                value = value.ToUpper();

                if (value.Contains(":"))
                {
                    var split = value.Split(':');
                    pair = split[0] + split[1];
                    Base = split[0];
                    Quote = split[1];
                }
                else if (value.Length == 6)
                {
                    pair = value;
                    Base = value.Substring(0, 3);
                    Quote = value.Substring(3);
                }

                Date = DateTime.Now;
            }
        }

        [JsonProperty("minimum_order_size")]
        public string MinimumOrder { get; set; }

        public string Base { get; set; }
        public string Quote { get; set; }
        public DateTime Date { get; set; }
    }

    [Serializable]
    public class ExchangeRate : INotifyPropertyChanged, IComparable
    {
        private string @base;
        private string quote;
        private decimal rate;
        private DateTime date;
        private Services exchange;

        public string Base { get { return @base; } set { if (value != @base) { @base = value; NotifyPropertyChanged(); } } }
        public string Quote { get { return quote; } set { if (value != quote) { quote = value; NotifyPropertyChanged(); } } }
        public decimal Rate { get { return rate; } set { if (value != rate) { rate = value; NotifyPropertyChanged(); } } }
        public DateTime Date { get { return date; } set { if (value != date) { date = value; NotifyPropertyChanged(); } } }
        public Services Exchange { get { return exchange; } set { if (value != exchange) { exchange = value; NotifyPropertyChanged(); } } }

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        public int CompareTo(object obj)
        {
            var exr = obj as ExchangeRate;

            return string.CompareOrdinal(Base + Quote, exr.Base + exr.Quote);
        }

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Converter

    public class TimeSinceLastUpdate : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            DateTime val = (DateTime)value;

            if (val.Year == 1900) return " ";

            var t = DateTime.Now - val;

            if (t.Hours > 0)
            {
                return string.Format("{0}h {1}m {2}s", t.Hours, t.Minutes, t.Seconds);
            }
            else if (t.Minutes > 0)
            {
                return string.Format("{0}m {1}s", t.Minutes, t.Seconds);
            }
            else
            {
                return string.Format("{1}s", t.Minutes, t.Seconds);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return default;
        }
    }

    public class ExchangeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var exchange = (Services)value;

            switch (exchange)
            {
                case Services.Fixer:
                    return "FXR";

                case Services.CMC:
                    return "CMC";

                case Services.Bitfinex:
                    return "BFX";

                case Services.Coinbase:
                    return "CB";

                default:
                    return "";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return default;
        }
    }
}