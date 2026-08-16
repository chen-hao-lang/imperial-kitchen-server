using KitchenChaos_Server.Counter;
using System;
using System.Collections.Generic;
using KitchenChaos_Server.Message;

namespace KitchenChaos_Server.Room
{
    public class ServerRoom
    {
        public int RoomId { get; private set; }
        public string RoomName { get; private set; }    
        public int MaxPlayer { get; private set; }
        public int OwnPlayerId { get; private set; }
        public List<ServerClient> clients;
        public HashSet<ServerPlayer> players;
        public HashSet<BaseCounter> counters;
        public HashSet<ServerPlate> plates;

        public ServerRoom(int _id,string _name,int _maxPlayer,int _ownerPlayerId)
        {
            RoomId = _id;
            RoomName = _name;
            MaxPlayer = _maxPlayer; 
            OwnPlayerId = _ownerPlayerId;

            clients = new List<ServerClient>(); 
            players = new HashSet<ServerPlayer>();  
            counters = new HashSet<BaseCounter>();
            plates = new HashSet<ServerPlate>();    
        }

        public void AddClient(ServerClient _client)
        {
            if (!clients.Contains(_client))
            {
                clients.Add(_client);
            }
            else
            {
                Console.WriteLine($"[ServerRoom] 房间：{RoomId} 已经 连接了客户端{_client}");
            }
        }

        public void RemoveClient(ServerClient _client)
        {
            if (clients.Contains(_client))
            {
                clients.Remove(_client);
            }
            else
            {
                Console.WriteLine($"[ServerRoom] 房间：{RoomId} 不存在客户端{_client}");
            } 
        }
        
        /// <summary>
        /// 判断当前房间是否满员
        /// </summary>
        /// <returns></returns>
        public bool IsFull()
        {
            return players.Count >= MaxPlayer;
        }

        public void BroadcastAll(byte[] _data)
        {
            foreach (var client in clients)
            {
                client.Send(_data);
            }
        }

        public void BroadcastEscept(ServerClient _client, byte[] _data)
        {
            foreach (var client in clients)
            {
                if (client != _client)
                {
                    client.Send(_data);
                }
            }
        }

        public void OnMessageReceived(ServerClient _client,MessageType _type, byte[] _data)
        {
            if (_type == MessageType.C2S_JoinRoom)
            {
                var roomId = NetworkMessage.Parse_JoinRoom(_data);
                //查看id对不对
                if (roomId != RoomId)
                {
                    Console.WriteLine();
                    return;
                }

                //查看客户端满了没
                if (clients.Count >= MaxPlayer)
                {
                    Console.WriteLine();
                    return;
                }

                //查看这个客户端是否已经在列表里
                if (clients.Contains(_client))
                {
                    Console.WriteLine();
                    return;
                }
                else
                {
                    clients.Add(_client);

                    players.Add(_client.Player);
                }

                //通知当前房间的其他客户端有新玩家加入
                BroadcastAll(NetworkMessage.Create_PlayerJoined(_client.Player.PlayerId,_client.Player.Name));
                return;
            }

            if (_type == MessageType.C2S_LeaveRoom)
            {
                if (clients.Contains(_client))
                {
                    clients.Remove(_client);

                    
                    //BroadcastEscept(_client, NetworkMessage.Create_RoomPlayerListSync());
                }
            }
        }
    }
}