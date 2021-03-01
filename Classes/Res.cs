using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ExchangeRateServer
{
    public static class Res
    {
        public static readonly BitmapImage Red = new BitmapImage(new Uri(@"images\status_red.png", UriKind.Relative));
        public static readonly BitmapImage Yellow = new BitmapImage(new Uri(@"images\status_yellow.png", UriKind.Relative));
        public static readonly BitmapImage Green = new BitmapImage(new Uri(@"images\status_green.png", UriKind.Relative));
        public static readonly System.Drawing.Icon On = new System.Drawing.Icon(@"images\green.ico");
        public static readonly System.Drawing.Icon Off = new System.Drawing.Icon(@"images\red.ico");
        public static readonly System.Drawing.Icon Connected = new System.Drawing.Icon(@"images\yellow.ico");

        public static readonly List<string> FIAT = new List<string>() { "USD", "EUR", "JPY", "CAD", "GBP", "CNY", "NZD", "AUD", "CHF" };
    }

    public static class Ext
    {
        public static string Short(this Exception ex) => ex.Message + ex.ToString().Remove(0, ex.ToString().IndexOf(":line"));

        public static void FileCheck(string filename)
        {
            if (!File.Exists(filename)) using (File.Create(filename)) { };
            FileInfo fileInfo = new FileInfo(filename);
            if (fileInfo.Length > 262144) { fileInfo.Delete(); using (File.Create(filename)) { }; }
        }

        public static decimal TruncateDecimal(decimal value, int precision)
        {
            decimal step = (decimal)Math.Pow(10, precision);
            decimal tmp = Math.Truncate(step * value);
            return tmp / step;
        }

        [DllImport("wininet.dll")]
        internal extern static bool InternetGetConnectedState(out int Val, int ReservedValue);
    }
}