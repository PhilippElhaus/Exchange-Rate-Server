using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ExchangeRateServer
{
    public partial class App : Application
    {
        internal static bool flag_log;
        internal static bool flag_debug;

        private readonly static CultureInfo Culture = new("en-US");

        private const string UniqueEventName = "{3596C906-E192-47BD-B890-B79591261EDD}";
        private EventWaitHandle eventWaitHandle;

        public App()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SingleInstanceWatcher();
            CultureInfo.DefaultThreadCurrentCulture = Culture;
        }

        private void SingleInstanceWatcher()
        {
            try
            {
                eventWaitHandle = EventWaitHandle.OpenExisting(UniqueEventName);
                _ = eventWaitHandle.Set();
                Shutdown();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, UniqueEventName);
            }

            new Task(() =>
            {
                while (eventWaitHandle.WaitOne())
                {
                    _ = Current.Dispatcher.BeginInvoke((Action)(() =>
                      {
                          if (!Current.MainWindow.Equals(null))
                          {
                              var mw = Current.MainWindow;

                              if (mw.WindowState == WindowState.Minimized || mw.Visibility != Visibility.Visible)
                              {
                                  mw.Show();
                                  mw.WindowState = WindowState.Normal;
                              }

                              _ = mw.Activate();
                              mw.Topmost = true;
                              mw.Topmost = false;
                              _ = mw.Focus();
                          }
                      }));
                }
            })
            .Start();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            Current.DispatcherUnhandledException += (x, ex) =>
            {
                if (!Directory.Exists("log"))
                {
                    _ = Directory.CreateDirectory("log");
                };

                File.AppendAllText(@"log\crash.txt", "Unknown Application Wide Exception: " + ex.Exception.Message + "\n" + ex.Exception.StackTrace);
            };

            for (var i = 0; i != e.Args.Length; ++i)
            {
                if (e.Args[i] == "/log")
                {
                    flag_log = true;
                }

                if (e.Args[i] == "/debug")
                {
                    flag_debug = true;
                }
            }

            base.OnStartup(e);
        }
    }
}