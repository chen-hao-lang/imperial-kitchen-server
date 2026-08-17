using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
using System.Security.Policy;

namespace KitchenChaos_Server.Room
{
    public class ServerRoomManager
    {
        /// <summary>
        /// Key:RoomId
        /// Value:RooM
        /// </summary>
        public Dictionary<int, ServerRoom> rooms;
        public object roomsLock;
        private int nextRoomId;

        public ServerRoomManager()
        {
            rooms = new Dictionary<int, ServerRoom>();
            roomsLock = new object();
            nextRoomId = 10000;
        }

        public ServerRoom CreateRoom(ServerClient _owner, string _roomName, int _maxPlayers, GameServer _server)
        {
            lock (roomsLock)
            {
                if (_owner.CurrentRoom != null)
                    return null;
            }

            int roomId = nextRoomId++;
            ServerRoom room = new ServerRoom(roomId, _roomName, _maxPlayers, _owner.Player.PlayerId, _server);
            if (!rooms.ContainsKey(roomId))
            {
                rooms.Add(roomId, room);
                int seadId = room.AddClient(_owner);
                if (seadId != -1)
                {
                    _owner.Send(NetworkMessage.Create_RoomCreated(roomId, _roomName, _maxPlayers));
                    //_owner.Send(NetworkMessage.Create_RoomJoined(roomId,seadId));
                }
            }
            else 
            {
                return rooms[roomId];
            }

            return room;
        }

        public bool JoinRoom(int _roomId, ServerClient _client)
        {
            lock (roomsLock)
            {
                if (rooms.TryGetValue(_roomId, out var room))
                {
                    if (!room.clients.Contains(_client))
                    {
                        if (!room.IsFull)
                        {
                            if (!room.IsStartGame)
                            {
                                //?
                                int seadId = room.AddClient(_client);
                                _client.Send(NetworkMessage.Create_RoomJoined(_roomId, seadId, _client.Player.PlayerId, _client.Player.Name));
                                room.BroadcastAll(NetworkMessage.Create_RoomPlayerListSync(room.GetRoomsPlayer(),room.GetRoomsPlayerName()));
                            }
                            else
                            {
                                _client.Send(NetworkMessage.Create_JoinedFailed(ERoomJoinFailReason.GameAlreadyStarted));
                                Console.WriteLine($"[RoomManager] 当前房间：{_roomId} 已经开始游戏");
                            }
                        }
                        else
                        {
                            _client.Send(NetworkMessage.Create_JoinedFailed(ERoomJoinFailReason.RoomFull));
                            Console.WriteLine($"[RoomManager] 当前房间：{_roomId} 已经满员");
                        }
                    }
                    else
                    {
                        _client.Send(NetworkMessage.Create_JoinedFailed(ERoomJoinFailReason.PlayerAlreadyInRoom));
                        Console.WriteLine($"[RoomManager] 该用户已经在房间中：{_client.Player.PlayerId}");
                    }
                }
                else
                {
                    _client.Send(NetworkMessage.Create_JoinedFailed(ERoomJoinFailReason.RoomNotFound));
                    Console.WriteLine($"[RoomManager] 不存在该房间：{_roomId}");
                }
            }

            return false;
        }

        /// <summary>
        /// 玩家离开房间
        /// </summary>
        /// <param name="_roomId">房间id</param>
        /// <param name="_client">玩家对应的客户端</param>
        public void LeaveRoom(ServerClient _client)
        {
            if (rooms.TryGetValue(_client.CurrentRoom.RoomId, out ServerRoom room))// 查看当前房间列表是否存在该房间
            {
                if (room == _client.CurrentRoom)// 如果存在
                {
                    bool roomIsEmpty = room.RemoveClient(_client);// 查看房间中存不存在客户端
                    if (roomIsEmpty)// 如果移除客户端后房间的人数为0，删除房间
                    {
                        RemoveRoom(_client.CurrentRoom.RoomId);
                    }
                }
            }
        }

        /// <summary>
        /// 移除房间
        /// </summary>
        /// <param name="_roomId">要移除的房间ID</param>
        public void RemoveRoom(int _roomId)
        {
            if (rooms.ContainsKey(_roomId))
            {
                rooms[_roomId].Stop();
                rooms.Remove(_roomId);
                NetworkMessage.Create_RoomDestroyed(_roomId);
            }
        }

        public void RemoveAllRoom()
        {
            foreach (var roomId in rooms.Keys)
            {
                RemoveRoom(roomId);
            }
        }

        public void OnClientDisconnected(ServerClient _client)
        {
            LeaveRoom(_client);
        }

        public List<(int,string,int,int)> GetRoomList()
        {
            lock (rooms)
            {
                List<(int, string, int, int)> list = new List<(int, string, int, int)>();
                foreach (var room in rooms.Values)
                {
                    list.Add((
                        room.RoomId,
                        room.RoomName,
                        room.players.Count,
                        room.MaxPlayer
                        ));
                }

                return list;
            }
        }

        public ServerRoom GetRoom(int _roomId)
        {
            if (rooms.TryGetValue(_roomId, out var room))
            {
                return room;
            }
            else
            {
                Console.WriteLine($"[ServerRoomManager] 当前房间：{_roomId} 不存在");
                return null;
            }
        }
    }
}