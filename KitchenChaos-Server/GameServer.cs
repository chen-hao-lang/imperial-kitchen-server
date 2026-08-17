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
        private int nextPlayerId = 1;

        /// <summary>
        /// 当前已连接的所有客户端
        /// </summary>
        private readonly List<ServerClient> clients = new List<ServerClient>();
        private readonly object clientsLock = new object();

        private ServerRoomManager roomManager;

        /// <summary>
        /// 启动服务器，开始监听
        /// </summary>
        /// <param name="port"></param>
        public void Start(int port)
        {
            roomManager = new ServerRoomManager();

            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            isRunning = true;

            _ = AcceptLoopAsync();
        }

        /// <summary>
        /// 停止服务器，断开所有客户端连接
        /// </summary>
        public void Stop()
        {
            isRunning = false;
            listener?.Stop();

            lock (clientsLock)
            {
                foreach (var client in clients)
                {
                    client.Disconnect();
                }
                clients.Clear();

                roomManager.RemoveAllRoom();
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
            Console.WriteLine($"[GameServer] 收到消息 玩家{_client.Player.PlayerId} 类型={_type}");

            switch (_type)
            {
                case MessageType.C2S_CreateRoom:
                    HandleCreateRoom(_client,_data);
                    break;
                case MessageType.C2S_JoinRoom:
                    HandleJoinRoom(_client,_data);
                    break;
                case MessageType.C2S_LeaveRoom:
                    roomManager.LeaveRoom(_client);
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
                default:
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
            roomManager.CreateRoom(_client,name,maxPlayers);// 创建房间
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
    }
}