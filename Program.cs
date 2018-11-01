using System;
using System.Reflection;
using System.Text;

namespace Zoro.RpcHost
{
    class Program
    {
        static RpcHost Host;
        static void Main(string[] args)
        {
            OnStart();
            RunConsole();
            OnStop();
        }

        static void OnStart()
        {
            Host = new RpcHost();

            Host.StartWebHost(Settings.Default.BindAddress,
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
                default:
                    Console.WriteLine("error: command not found " + args[0]);
                    return true;
            }
        }
    }
}
