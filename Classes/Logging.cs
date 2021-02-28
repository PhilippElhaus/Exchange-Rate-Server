using Serilog.Core;
using Serilog.Events;
using System.Globalization;
using System.IO;

namespace ExchangeRateServer
{
    public class SysWsLog : ILogEventSink
    {
        private MainWindow Main;

        private object fileAccessLock_syslog = new object();

        public SysWsLog(MainWindow main)
        {
            Main = main;
        }

        public void Emit(LogEvent logEvent)
        {
            if (App.flag_log)
            {
                Main.Dispatcher.Invoke(() =>
                {
                    if (Main.SystemLog.Text.Length > 16384) Main.SystemLog.Text = "";
                    Main.SystemLog.AppendText(logEvent.RenderMessage() + "\n");

                    Main.LBL_SysLog.Content = $"System Log ({Main.SystemLog.Text.Length})";
                });

                lock (fileAccessLock_syslog)
                {
                    try
                    {
                        Ext.FileCheck("syslog.txt");

                        using (var writer = File.AppendText("syslog.txt"))
                        {
                            writer.WriteLine(logEvent.Timestamp.ToString("dd-MMM-yy HH:mm:ss", new CultureInfo("de-DE")) + " " + logEvent.RenderMessage());
                        }
                    }
                    catch { }
                }
            }
        }
    }
}