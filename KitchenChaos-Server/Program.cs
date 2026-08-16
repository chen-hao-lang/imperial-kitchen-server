using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            server.Start(port);

            Console.WriteLine("服务器已启动，按任意键关闭...");
            Console.ReadKey();

            server.Stop();
            Console.WriteLine("服务器已关闭。");
        }
    }
}
