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
        public static bool flag_log = false;
        public static bool flag_markets = false;

        private const string UniqueEventName = "{3596C906-E192-47BD-B890-B79591261EDD}";
        private EventWaitHandle eventWaitHandle;

        public App()
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SingleInstanceWatcher();
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        }

        private void SingleInstanceWatcher()
        {
            try
            {
                this.eventWaitHandle = EventWaitHandle.OpenExisting(UniqueEventName);
                this.eventWaitHandle.Set();
                this.Shutdown();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                this.eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, UniqueEventName);
            }

            new Task(() =>
            {
                while (this.eventWaitHandle.WaitOne())
                {
                    Current.Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (!Current.MainWindow.Equals(null))
                        {
                            var mw = Current.MainWindow;

                            if (mw.WindowState == WindowState.Minimized || mw.Visibility != Visibility.Visible)
                            {
                                mw.Show();
                                mw.WindowState = WindowState.Normal;
                            }

                            mw.Activate();
                            mw.Topmost = true;
                            mw.Topmost = false;
                            mw.Focus();
                        }
                    }));
                }
            })
            .Start();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            Current.DispatcherUnhandledException += (x, ex) => { File.AppendAllText("exception.txt", "Unknown Application Wide Exception: " + ex.Exception.Message + "\n" + ex.Exception.StackTrace); };

            for (int i = 0; i != e.Args.Length; ++i)
            {
                if (e.Args[i] == "/log")
                {
                    flag_log = true;
                }
               else if (e.Args[i] == "/markets")
                {
                    flag_markets = true;
                }
            }

            base.OnStartup(e);
        }
    }
}