using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KitchenChaos_Server.Message;

namespace KitchenChaos_Server
{
    public class GameServer
    {
        /// <summary>
        /// 最大同时在线人数
        /// </summary>
        public const int MAX_PLAYERS = 4;

        private TcpListener listener;
        private bool isRunning;
        private int nextPlayerId = 1;

        /// <summary>
        /// 当前已连接的所有客户端
        /// </summary>
        private readonly List<ServerClient> clients = new List<ServerClient>();
        private readonly object clientsLock = new object();

        private ServerGameManager gameManager;

        /// <summary>
        /// 服务器权威的订单管理器（生成订单、校验交付、维护分数）
        /// </summary>
        private ServerDeliveryManager deliveryManager;

        /// <summary>
        /// 操作台更新用的 CancellationTokenSource
        /// </summary>
        private CancellationTokenSource counterCts;

        /// <summary>
        /// 操作台更新间隔（秒），约 20 次/秒
        /// </summary>
        private const float COUNTER_TICK_INTERVAL = 0.05f;

        /// <summary>
        /// 启动服务器，开始监听
        /// </summary>
        /// <param name="port"></param>
        public void Start(int port)
        {
            gameManager = new ServerGameManager(this);
            deliveryManager = new ServerDeliveryManager(this);

            // 操作台类型映射由客户端连接后通过 C2S_CounterRegister 上报

            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            isRunning = true;

            // 启动操作台更新循环（盘子生成、进度计时等）
            counterCts = new CancellationTokenSource();
            _ = CounterUpdateLoopAsync(counterCts.Token);

            _ = AcceptLoopAsync();
        }

        /// <summary>
        /// 停止服务器，断开所有客户端连接
        /// </summary>
        public void Stop()
        {
            isRunning = false;
            counterCts?.Cancel();
            listener?.Stop();

            lock (clientsLock)
            {
                foreach (var client in clients)
                {
                    client.Disconnect();
                }
                clients.Clear();
            }
        }

        /// <summary>
        /// 持续等待新客户端连接的循环
        /// </summary>
        /// <returns></returns>
        private async Task AcceptLoopAsync()
        {
            while (isRunning)
            {
                try
                {
                    // 等待一个客户端连接
                    TcpClient tcpClient = await listener.AcceptTcpClientAsync();

                    lock (clientsLock)
                    {
                        if (clients.Count >= MAX_PLAYERS)
                        {
                            Console.WriteLine($"[GameServer] 房间已满，拒绝连接：{tcpClient.Client.RemoteEndPoint}");
                            tcpClient.Dispose();
                            continue;
                        }

                        // 为这个连接 创建ServerClient
                        int playerId = nextPlayerId;
                        ServerClient client = new ServerClient(playerId, tcpClient, this);
                        clients.Add(client);

                        Console.WriteLine($"[GameServer] 玩家 {playerId} 已连接{tcpClient.Client.RemoteEndPoint},当前在线人数：{clients.Count}/{MAX_PLAYERS}");

                        players[playerId] = client.Player;

                        List<int> list = new List<int>();
                        foreach (var player in clients)
                        {
                            list.Add(player.Player.PlayerId);
                        }

                        // 第一步：给客户端分配玩家ID
                        client.Send(NetworkMessage.Create_Connect(playerId,list));

                        // 第二步：通知已有玩家“有人加入”
                        BroadcastExcept(client, NetworkMessage.Create_PlayerJoined(playerId, $"玩家{playerId}"));

                        // 第三步：给新客户端全量同步已有玩家、操作台和盘子状态
                        SendFullSyncToClient(client);

                        // 第五步：开始接受这个客户端的消息
                        client.BeginReceive();

                        nextPlayerId += 1;
                        gameManager.OnPlayerConnected(playerId);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (isRunning)
                    {
                        Console.WriteLine($"[GameServer] 接受连接出错：{ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 客户端断开连接时的回调
        /// </summary>
        /// <param name="_client"></param>
        public void OnClientDisconnected(ServerClient _client)
        {
            players.Remove(_client.Player.PlayerId);
            gameManager?.OnPlayerLefted(_client.Player.PlayerId);  // ★ 新增

            lock (clientsLock)
            {
                clients.Remove(_client);
                Console.WriteLine($"[GameServer] 玩家 {_client.Player.PlayerId} 断开连接,当前在线：{clients.Count}/{MAX_PLAYERS}");
            }

            // 通知其他玩家有人离开
            BroadcastAll(NetworkMessage.Create_PlayerLeft(_client.Player.PlayerId));
        }

        /// <summary>
        /// 广播消息给所有客户端
        /// </summary>
        /// <param name="_data"></param>
        public void BroadcastAll(byte[] _data)
        {
            lock (clientsLock)
            {
                foreach (var client in clients)
                {
                    client.Send(_data);
                }
            }
        }

        /// <summary>
        /// 广播消息给除了指定客户端以外的所有人
        /// 用在“新玩家加入”等场景--新玩家不需要收到“自己加入”的消息
        /// </summary>
        /// <param name="_except"></param>
        /// <param name="_data"></param>
        public void BroadcastExcept(ServerClient _except, byte[] _data)
        {
            lock (clientsLock)
            {
                foreach (var client in clients)
                {
                    if (client != _except)
                    {
                        client.Send(_data);
                    }
                }
            }
        }

        /// <summary>
        /// 收到客户端消息时的处理入口
        /// </summary>
        /// <param name="_sender">收到消息的客户端</param>
        /// <param name="_type">消息类型</param>
        /// <param name="_body">消息体</param>
        public void OnMessageReceived(ServerClient _sender, MessageType _type, byte[] _body)
        {
            Console.WriteLine($"[GameServer] 收到消息 玩家{_sender.Player.PlayerId} 类型={_type}");

            switch (_type)
            {
                case MessageType.C2S_Disconnect:
                    _sender.Disconnect();
                    break;
                default:
                    Console.WriteLine($"[GameServer] 未处理的消息类型: {_type}");
                    break;
            }
        }

        /// <summary>
        /// 新一局游戏开始时重置订单与分数（由 ServerGameManager 调用）。
        /// </summary>
        public void StartNewGame()
        {
            deliveryManager?.StartNewGame();
        }
    }
}