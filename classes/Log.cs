using Serilog.Core;
using Serilog.Events;
using System.Globalization;
using System.IO;

namespace ExchangeRateServer
{
    public class Sys_Log : ILogEventSink
    {
        private readonly MainWindow Main;

        private static readonly CultureInfo Culture = new("de-DE");

        internal static object fileAccessLock_syslog = new();

        public Sys_Log(MainWindow main)
        {
            Main = main;
        }

        public void Emit(LogEvent logEvent)
        {
            if (App.flag_log)
            {
                string txt = default;
                if (logEvent.Level == LogEventLevel.Warning) txt = "WARNING: ";
                if (logEvent.Level == LogEventLevel.Error) txt = "ERROR: ";

                txt += logEvent.RenderMessage();

                Main.Dispatcher.Invoke(() =>
                {
                    if (Main.SystemLog.Text.Length > 16384) Main.SystemLog.Text = string.Empty;
                    Main.SystemLog.AppendText(txt + "\n");

                    Main.LBL_SysLog.Content = $"Log ({Main.SystemLog.Text.Length})";
                });

                lock (fileAccessLock_syslog)
                {
                    try
                    {
                        Ext.FileCheck("syslog.txt", "log");

                        using (var writer = File.AppendText(@"log\syslog.txt"))
                        {
                            writer.WriteLine(logEvent.Timestamp.ToString("dd-MMM-yy HH:mm:ss", Culture) + " " + txt);
                        }
                    }
                    catch { }
                }
            }
        }
    }
}