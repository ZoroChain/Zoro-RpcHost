using System;
using System.Reflection;
using System.Text;
using System.Net;
using System.IO;

namespace Zoro.RpcHost
{
    class Program
    {
        static RpcHost Host;

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            using (FileStream fs = new FileStream("rpchost_error.log", FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter w = new StreamWriter(fs))
                if (e.ExceptionObject is Exception ex)
                {
                    PrintErrorLogs(w, ex);
                }
                else
                {
                    w.WriteLine(e.ExceptionObject.GetType());
                    w.WriteLine(e.ExceptionObject);
                }
        }

        private static void PrintErrorLogs(StreamWriter writer, Exception ex)
        {
            writer.WriteLine(ex.GetType());
            writer.WriteLine(ex.Message);
            writer.WriteLine(ex.StackTrace);
            if (ex is AggregateException ex2)
            {
                foreach (Exception inner in ex2.InnerExceptions)
                {
                    writer.WriteLine();
                    PrintErrorLogs(writer, inner);
                }
            }
            else if (ex.InnerException != null)
            {
                writer.WriteLine();
                PrintErrorLogs(writer, ex.InnerException);
            }
        }

        static void Main(string[] args)
        {
            OnStart(args);
            RunConsole();
            OnStop();
        }

        static void OnStart(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            var bufferSize = 1024 * 67 + 128;
            Stream inputStream = Console.OpenStandardInput(bufferSize);
            Console.SetIn(new StreamReader(inputStream, Console.InputEncoding, false, bufferSize));

            int logLevel = 0;
            bool disableLog = false;
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "/disableLog":
                    case "--disableLog":
                    case "-logoff":
                        disableLog = true;
                        break;
                    case "/logdetail":
                        logLevel = 1;
                        break;
                }

            Host = new RpcHost();

            Host.EnableLog(!disableLog, logLevel);

            Host.StartWebHost(IPAddress.Any,
                Settings.Default.Port,
                sslCert: Settings.Default.SslCert,
                password: Settings.Default.SslCertPassword);

            Host.ConnectToAgent(Settings.Default.AgentAddress, Settings.Default.AgentPort);
        }

        static void OnStop()
        {
            Host.Dispose();
        }

        static void RunConsole()
        {
            bool running = true;
#if NET461
            Console.Title = ServiceName;
#endif
            Console.OutputEncoding = Encoding.Unicode;

            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Version ver = Assembly.GetEntryAssembly().GetName().Version;
            Console.WriteLine($"{Assembly.GetExecutingAssembly().GetName().Name} Version: {ver}");
            Console.WriteLine();

            while (running)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                string line = Console.ReadLine()?.Trim();
                if (line == null) break;
                Console.ForegroundColor = ConsoleColor.White;
                string[] args = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length == 0)
                    continue;
                try
                {
                    running = OnCommand(args);
                }
                catch (Exception ex)
                {
#if DEBUG
                    Console.WriteLine($"error: {ex.Message}");
#else
                    Console.WriteLine("error");
#endif
                }
            }

            Console.ResetColor();
        }

        static bool OnCommand(string[] args)
        {
            switch (args[0].ToLower())
            {
                case "clear":
                    Console.Clear();
                    return true;
                case "exit":
                    return false;
                case "version":
                    Console.WriteLine(Assembly.GetEntryAssembly().GetName().Version);
                    return true;
                case "show":
                    return OnShowCommand(args);
                default:
                    Console.WriteLine("error: command not found " + args[0]);
                    return true;
            }
        }

        static bool OnShowCommand(string[] args)
        {
            switch (args[1].ToLower())
            {
                case "state":
                    Host.ShowState();
                    return true;
                default:
                    Console.WriteLine("error: command not found " + args[0]);
                    return true;
            }
        }
    }
}
