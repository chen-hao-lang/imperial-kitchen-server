using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
using System.Linq;

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

        public ServerRoom CreateRoom(ServerClient _owner, string _roomName, int _maxPlayers)
        {
            lock (roomsLock)
            {
                if (_owner.CurrentRoom != null)
                {
                    Console.WriteLine($"[ServerRoomManager] 房间创建失败,该客户端{_owner} 已在房间中");
                    return null;
                }

                int roomId = nextRoomId++;
                // 创建房间
                ServerRoom room = new ServerRoom(roomId, _roomName, _maxPlayers, _owner.Player.PlayerId);
                if (!rooms.ContainsKey(roomId))
                {
                    rooms.Add(roomId, room);
                    int seadId = room.AddClient(_owner);// 房主直接加入房间
                    if (seadId != -1)
                    {
                        // 向客户端发送房间创建的消息
                        _owner.Send(NetworkMessage.Create_RoomCreated(roomId));
                        _owner.Send(NetworkMessage.Create_RoomJoined(roomId, seadId, _owner.Player.PlayerId, _owner.Player.Name));
                    }
                }
                else
                {
                    return rooms[roomId];
                }

                return room;
            }
        }

        public bool JoinRoom(int _roomId, ServerClient _client)
        {
            if (_client.CurrentRoom != null)
            {
                return false;
            }

            lock (roomsLock)
            {
                if (rooms.TryGetValue(_roomId, out var room))
                {
                    if (!room.clients.Contains(_client))
                    {
                        if (!room.IsFull)
                        {
                            if (room.RoomGameManager.GameStata == EGameState.WaitingForPlayers)
                            {
                                int seadId = room.AddClient(_client);
                                // 给要加入房间的客户端发送加入成功的消息（RoomJoined），让客户端进入房间并切换场景
                                _client.Send(NetworkMessage.Create_RoomJoined(_roomId, seadId, _client.Player.PlayerId, _client.Player.Name));
                                _client.Send(NetworkMessage.Create_RoomPlayerListSync(room.GetRoomPlayerInfo()));

                                // 给房间内的其他客户端发送当前加入房间的玩家，方便客户端更新玩家模型
                                room.BroadcastExcept(_client, NetworkMessage.Create_RoomJoined(_roomId, seadId, _client.Player.PlayerId,_client.Player.Name));
                                return true;
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
            var room = _client.CurrentRoom;
            if (room == null)
                return;

            bool roomIsEmpty = room.RemoveClient(_client);
            if (roomIsEmpty)
                RemoveRoom(_client, room.RoomId);
        }

        /// <summary>
        /// 移除房间
        /// </summary>
        /// <param name="_roomId">要移除的房间ID</param>
        public void RemoveRoom(ServerClient _client, int _roomId)
        {
            lock (roomsLock)
            {
                // 只有房主才可以移除房间
                if (rooms.ContainsKey(_roomId) && _client.Player.PlayerId == rooms[_roomId].OwnPlayerId)
                {
                    rooms[_roomId].BroadcastAll(NetworkMessage.Create_RoomDestroyed(_roomId));
                    rooms[_roomId].Stop();

                    rooms.Remove(_roomId);

                    //向其他客户端更新当前的房间列表
                    NetworkMessage.Create_RequestRoomList(GetRoomList());
                }
            }
        }

        /// <summary>
        /// 移除所有房间
        /// </summary>
        public void RemoveAllRoom()
        {
            foreach (var roomId in rooms.Keys.ToList())
            {
                rooms[roomId].Stop();
                rooms.Remove(roomId);
            }

            rooms.Clear();
        }

        public void StartGame(int _roomId)
        {
            lock (roomsLock)
            {
                foreach (var roomId in rooms.Keys)
                {
                    if (roomId == _roomId)
                    {
                        // 先查看当前房间的人数是否满足
                        if (rooms[roomId].players.Count == rooms[roomId].MaxPlayer)
                            rooms[roomId].StartGame();
                    }
                }
            }
        }

        public void OnClientDisconnected(ServerClient _client)
        {
            LeaveRoom(_client);
        }

        public List<(int, string, int, int)> GetRoomList()
        {
            lock (roomsLock)
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