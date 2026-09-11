using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
                        _owner.Send(NetworkMessage.Create_RoomCreated(roomId,_roomName,_maxPlayers));
                        _owner.Send(NetworkMessage.Create_RoomJoined(roomId, seadId, _owner.Player.PlayerId, _owner.Player.Name, room.MaxPlayer));
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
                                _client.Send(NetworkMessage.Create_RoomJoined(_roomId, seadId, _client.Player.PlayerId, _client.Player.Name, room.MaxPlayer));
                                _client.Send(NetworkMessage.Create_RoomPlayerListSync(room.GetRoomPlayerInfo()));

                                // 给房间内的其他客户端发送当前加入房间的玩家，方便客户端更新玩家模型
                                room.BroadcastExcept(_client, NetworkMessage.Create_RoomJoined(_roomId, seadId, _client.Player.PlayerId, _client.Player.Name, room.MaxPlayer));
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
                RemoveEmptyRoom(room.RoomId);
        }

        /// <summary>最后一名成员离开/掉线后回收空房（不要求离开者是原始房主）</summary>
        public void RemoveEmptyRoom(int _roomId)
        {
            lock (roomsLock)
            {
                DestroyRoomInternal(_roomId);
            }
        }

        /// <summary>
        /// 房主主动解散房间（仅房主可解散）。
        /// </summary>
        /// <param name="_roomId">要移除的房间ID</param>
        public void RemoveRoom(ServerClient _client, int _roomId)
        {
            lock (roomsLock)
            {
                // 只有房主才可以移除房间
                if (rooms.ContainsKey(_roomId) && _client.Player.PlayerId == rooms[_roomId].OwnPlayerId)
                {
                    DestroyRoomInternal(_roomId);
                }
            }
        }

        /// <summary>销毁房间：广播 RoomDestroyed、停止循环、移除登记。调用方需持有 roomsLock（可重入）。</summary>
        private void DestroyRoomInternal(int _roomId)
        {
            if (!rooms.ContainsKey(_roomId)) return;

            rooms[_roomId].BroadcastAll(NetworkMessage.Create_RoomDestroyed(_roomId));
            rooms[_roomId].Stop();
            rooms.Remove(_roomId);
            Console.WriteLine($"[ServerRoomManager] 房间 {_roomId} 已销毁");
        }

        // 断线重连（D7）已暂时禁用
        /*
        /// <summary>
        /// 重连认领入口。认领成功：房间把新连接绑定为旧玩家、给本人 ResumeGameResult、给全房间 FullSync。
        /// </summary>
        public void ResumeGame(ServerClient _client, int _roomId, int _oldPlayerId)
        {
            ServerRoom room;
            lock (roomsLock)
            {
                if (!rooms.TryGetValue(_roomId, out room))
                {
                    room = null;
                }
            }

            if (room == null)
            {
                SendResumeFailed(_client);
                return;
            }

            if (!room.TryResumePlayer(_client, _oldPlayerId))
            {
                SendResumeFailed(_client);
                return;
            }

            // 认领成功后：通知其他客户端该玩家回来了（客户端会重新生成其远程模型）
            room.BroadcastAll(NetworkMessage.Create_RoomPlayerListSync(room.GetRoomPlayerInfo()));
        }

        private static void SendResumeFailed(ServerClient _client)
        {
            // ok=0；其余字段填充占位，客户端只认 ok
            _client.Send(NetworkMessage.Create_ResumeGameResult(
                false, -1, 0f, 0f, 0f, 0f, -1));
        }
        */

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

        /// <summary>
        /// 开始游戏
        /// </summary>
        /// <param name="_client"></param>
        /// <param name="_roomId"></param>
        public void EnterGameScene(ServerClient _client,int _roomId)
        {
            lock (roomsLock)
            {
                if (!rooms.TryGetValue(_roomId, out var room))
                {
                    Console.WriteLine($"[ServerRoomManager] 房间 {_roomId} 不存在，无法开局");
                    return;
                }

                if (!room.CanStartGame(_client))
                {
                    Console.WriteLine($"[ServerRoomManager] 玩家 {_client.Player.PlayerId} 请求开局被拒绝：非房主/未全员Ready/状态不允许");
                    return;
                }

                room.EnterGameScene();
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