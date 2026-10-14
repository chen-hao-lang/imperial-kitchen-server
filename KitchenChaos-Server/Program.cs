using System;
using System.Threading;

namespace KitchenChaos_Server
{
    internal class Program
    {
        static void Main(string[] args)
        {
            int port = 7777;

            if (args.Length > 0 && int.TryParse(args[0], out int customPort))
            {
                port = customPort;
            }

            Console.WriteLine($"=== KitchenChaos 服务器 ===");
            Console.WriteLine($"正在启动，监听端口: {port}");

            GameServer server = new GameServer();
            try
            {
                server.Start(port);
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine($"服务器启动失败：{ex.Message}");
                Console.WriteLine("请关闭占用该端口的进程，或使用其他端口重试。");
                return;
            }

            Console.WriteLine("服务器已启动，按任意键关闭...");

            using (var exitSignal = new ManualResetEventSlim(false))
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;          // 阻止默认的强制终止
                    exitSignal.Set();
                };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => exitSignal.Set();
                System.Runtime.Loader.AssemblyLoadContext.Default.Unloading += _ => exitSignal.Set();

                exitSignal.Wait();
            }

            server.Stop();
            Console.WriteLine("服务器已关闭。");
        }
    }
}
