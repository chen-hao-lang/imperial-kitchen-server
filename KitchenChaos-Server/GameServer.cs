using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server
{
    public class GameServer
    {
        /// <summary>
        /// 最大同时在线人数
        /// </summary>
        public const int MAX_PLAYERS = 16;

        private TcpListener listener;
        private bool isRunning;
        private readonly object serverStateLock = new object();
        private int nextPlayerId = 1;

        /// <summary>
        /// 当前已连接的所有客户端
        /// </summary>
        private readonly List<ServerClient> clients = new List<ServerClient>();
        private readonly object clientsLock = new object();

        private ServerRoomManager roomManager;

        private const int HEARTBEAT_INTERVAL_MS = 1000; // 心跳检查间隔，单位毫秒
        private const int CLIENT_TIMEOUT_SECONDS = 10;  // 普通客户端：超过该时长未收到任何消息即判定掉线

        /// <summary>
        /// 调试客户端（编辑器 / 开发版）的保活超时（秒）。
        /// 断点调试、编辑器失去焦点时心跳会长时间停发，若仍按 <see cref="CLIENT_TIMEOUT_SECONDS"/> 判定会误断开，
        /// 因此调试客户端使用较宽松的超时。可用环境变量 KC_DEBUG_CLIENT_TIMEOUT 覆盖（单位：秒）。
        /// </summary>
        private static readonly int CLIENT_DEBUG_TIMEOUT_SECONDS = ResolveDebugClientTimeoutSeconds();

        /// <summary>
        /// 解析调试客户端超时：优先读环境变量 KC_DEBUG_CLIENT_TIMEOUT，否则默认 600 秒（10 分钟）。
        /// </summary>
        private static int ResolveDebugClientTimeoutSeconds()
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("KC_DEBUG_CLIENT_TIMEOUT"), out int seconds)
                && seconds > 0)
            {
                return seconds;
            }

            return 600;
        }

        /// <summary>
        /// 启动服务器，开始监听
        /// </summary>
        /// <param name="port"></param>
        public void Start(int port)
        {
            lock (serverStateLock)
            {
                if (isRunning)
                {
                    throw new InvalidOperationException("服务器已经启动，不能重复启动。");
                }

                TcpListener newListener = new TcpListener(IPAddress.Any, port);
                try
                {
                    // 只有监听器成功绑定后，才发布状态并启动后台循环。
                    newListener.Start();
                    listener = newListener;
                    roomManager = new ServerRoomManager();
                    isRunning = true;
                }
                catch (SocketException ex)
                {
                    newListener.Stop();
                    listener = null;
                    throw new InvalidOperationException(
                        $"无法监听端口 {port}。该端口可能已被其他进程或另一个服务器实例占用。", ex);
                }
            }

            _ = HeartbeatLoopAsync();
            _ = AcceptLoopAsync();
        }

        /// <summary>
        /// 停止服务器，断开所有客户端连接
        /// </summary>
        public void Stop()
        {
            lock (serverStateLock)
            {
                isRunning = false;
                listener?.Stop();
                listener = null;
            }

            lock (clientsLock)
            {
                foreach (var client in clients)
                {
                    client.Disconnect();
                }
                clients.Clear();

                roomManager?.RemoveAllRoom();
            }
        }

        #region 心跳检测与客户端超时处理
        private async Task HeartbeatLoopAsync()
        {
            while (isRunning)
            {
                try
                {
                    await Task.Delay(HEARTBEAT_INTERVAL_MS);
                    CheckClientTimeouts();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"[GameServer] 心跳检查出错：{ex.Message}");
                }
            }
        }

        private void CheckClientTimeouts()
        {
            List<ServerClient> timedOutClients = new List<ServerClient>();
            lock (clientsLock)
            {
                foreach (var client in clients)
                {
                    // 调试客户端（编辑器/开发版）在断点暂停期间不发心跳，使用放宽后的超时
                    int timeoutSeconds = client.IsDebugClient
                        ? CLIENT_DEBUG_TIMEOUT_SECONDS
                        : CLIENT_TIMEOUT_SECONDS;

                    if ((DateTime.UtcNow - client.LastReceiveTimeUtc).TotalSeconds > timeoutSeconds)
                    {
                        timedOutClients.Add(client);
                    }
                }
                foreach (var client in timedOutClients)
                {
                    Console.WriteLine($"[GameServer] 玩家 {client.Player.PlayerId} 超时（超过 " +
                                      $"{(client.IsDebugClient ? CLIENT_DEBUG_TIMEOUT_SECONDS : CLIENT_TIMEOUT_SECONDS)}s 未收到消息），断开连接");
                    client.Disconnect();
                }
            }

            // 顺带清理各房间过期的重连暂存席
            roomManager?.PruneReconnectSeats();
        }
        /// <summary>
        /// 处理心跳消息：解析标志位（bit0 = 调试客户端）。
        /// 保活时间已在 ServerClient.ReceiveLoopAsync 中统一刷新，此处不重复刷新。
        /// </summary>
        /// <param name="_client">发送心跳的客户端</param>
        /// <param name="_data">心跳消息体：flags(byte)，可为空（旧版客户端）</param>
        private void HandleHeartbeat(ServerClient _client, byte[] _data)
        {
            if (_data == null || _data.Length < 1) return;

            if (_client.MarkHeartbeatFlags(_data[0]))
            {
                Console.WriteLine($"[GameServer] 玩家 {_client.Player.PlayerId} 为调试客户端，" +
                                  $"保活超时放宽至 {CLIENT_DEBUG_TIMEOUT_SECONDS}s");
            }
        }
        #endregion

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

                        client.Send(NetworkMessage.Create_Connect(playerId));

                        client.BeginReceive();

                        nextPlayerId += 1;
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
            lock (clientsLock)
            {
                clients.Remove(_client);
                Console.WriteLine($"[GameServer] 玩家 {_client.Player.PlayerId} 断开连接,当前在线：{clients.Count}/{MAX_PLAYERS}");
            }

            // 通知其他玩家有人离开
            roomManager.OnClientDisconnected(_client);
        }

        /// <summary>
        /// 收到客户端消息时的处理入口
        /// </summary>
        /// <param name="_client">收到消息的客户端</param>
        /// <param name="_type">消息类型</param>
        /// <param name="_data">消息体</param>
        public void OnMessageReceived(ServerClient _client, MessageType _type, byte[] _data)
        {
            // 心跳每隔几秒一次，单独过滤避免日志刷屏
            if (_type != MessageType.C2S_HeartBeat)
            {
                Console.WriteLine($"[GameServer] 收到消息 玩家{_client.Player.PlayerId} 类型={_type}");
            }

            switch (_type)
            {
                case MessageType.C2S_Connect:
                    // 连接消息已经在 OnClientConnected 中处理过了，这里不再处理
                    break;
                case MessageType.C2S_HeartBeat:
                    HandleHeartbeat(_client, _data);
                    break;
                // 断线重连（D7）已暂时禁用
                case MessageType.C2S_ResumeGame:
                    HandleResumeGame(_client, _data);
                    break;
                case MessageType.C2S_CreateRoom:
                    HandleCreateRoom(_client,_data);
                    break;
                case MessageType.C2S_JoinRoom:
                    HandleJoinRoom(_client,_data);
                    break;
                case MessageType.C2S_LeaveRoom:
                    roomManager.LeaveRoomExplicit(_client);
                    break;
                case MessageType.C2S_DestroyRoom:
                    HandleDestryoRoom(_client,_data);
                    break;
                case MessageType.C2S_RequestRoomList:
                    _client.Send(NetworkMessage.Create_RequestRoomList(roomManager.GetRoomList()));
                    break;
                case MessageType.C2S_Disconnect:
                    _client.Disconnect();
                    break;
                case MessageType.C2S_RequestEnterGameScene:
                    HandleRequestEnterGameScene(_client,_data);
                    break;
                default:// 其他游戏的内的同步
                    var room = _client.CurrentRoom;
                    if (room == null)
                    {
                        Console.WriteLine("该玩家并未加入房间，忽略该消息");
                    }
                    else
                    {
                        room.HandleGameMessage(_client,_type,_data);
                    }
                    Console.WriteLine($"[GameServer] 未处理的消息类型: {_type}");
                    break;
            }
        }

        /// <summary>
        /// 处理来自客户端上传的房间创建逻辑
        /// </summary>
        /// <param name="_client">客户端</param>
        /// <param name="_data">房间参数</param>
        private void HandleCreateRoom(ServerClient _client,byte[] _data)
        {
            var (maxPlayers,name) = NetworkMessage.Parse_CreateRoom(_data);// 解析客户端上传的房间参数：最多游玩玩家，房间名字
            ServerRoom room = roomManager.CreateRoom(_client,name,maxPlayers);// 创建房间

            lock (clientsLock)
            {
                // 向其他客户端发送房间列表更新的信息
                foreach (var client in clients)
                {
                    if (client != _client)
                    {
                        client.Send(NetworkMessage.Create_RequestRoomList(roomManager.GetRoomList()));
                    }
                }
            }
        }

        /// <summary>
        /// 处理来自客户端加入房间的逻辑
        /// </summary>
        /// <param name="_client">客户端</param>
        /// <param name="_data">房间ID</param>
        private void HandleJoinRoom(ServerClient _client, byte[] _data)
        {
            var roomId = NetworkMessage.Parse_JoinRoom(_data);
            if (!roomManager.JoinRoom(roomId, _client))
            {
                Console.WriteLine($"[GameServer] 客户端：{_client} 加入房间：{roomId} 失败");
            }
        }

        private void HandleDestryoRoom(ServerClient _client,byte[] _data)
        {
            var roomId = NetworkMessage.Parse_DestroyRoom(_data);
            roomManager.RemoveRoom(_client,roomId);
        }

        /// <summary>
        /// 请求进入游戏场景
        /// </summary>
        /// <param name="_data"></param>
        private void HandleRequestEnterGameScene(ServerClient _client,byte[] _data)
        {
            var roomId = NetworkMessage.Parse_StartGame(_data);
            // 通知该房间内的所有客户端，转移场景
            roomManager.EnterGameScene(_client,roomId);
        }

        /// <summary>
        /// 重连认领：玩家重新连上后，请求认领自己在某房间的局内席位。
        /// </summary>
        private void HandleResumeGame(ServerClient _client, byte[] _data)
        {
            var (roomId, oldPlayerId) = NetworkMessage.Parse_ResumeGame(_data);
            roomManager.ResumeGame(_client, roomId, oldPlayerId);
        }

    }
}