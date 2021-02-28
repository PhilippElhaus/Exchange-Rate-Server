﻿using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace ExchangeRateServer
{
    internal enum Services
    {
        Fixer = 0,
        CMC = 1,
        Bitfinex = 2,
        Coinbase = 3
    }

    public class Change
    {
        public string Reference { get; set; }
        public string Currency { get; set; }
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

    [Serializable]
    public class ExchangeRate : INotifyPropertyChanged
    {
        private string ccy1;
        private string ccy2;
        private decimal rate;
        private DateTime date;

        public string CCY1 { get { return ccy1; } set { if (value != ccy1) { ccy1 = value; NotifyPropertyChanged(); } } }
        public string CCY2 { get { return ccy2; } set { if (value != ccy2) { ccy2 = value; NotifyPropertyChanged(); } } }
        public decimal Rate { get { return rate; } set { if (value != rate) { rate = value; NotifyPropertyChanged(); } } }
        public DateTime Date { get { return date; } set { if (value != date) { date = value; NotifyPropertyChanged(); } } }

        [field: NonSerialized]
        public event PropertyChangedEventHandler PropertyChanged;

        protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class MarketInfo
    {
        private string pair;

        public string Pair
        {
            get => pair;
            set
            {
                    pair = value.ToUpper();
                    BaseCurrency = value.Substring(0, 3).ToUpper();
                    QuoteCurrency = value.Substring(3).ToUpper();
            }
        }

        public string BaseCurrency { get; set; }
        public string QuoteCurrency { get; set; }
        public decimal Minimum_order_size { get; set; }
        public DateTime Date { get; set; }
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
}