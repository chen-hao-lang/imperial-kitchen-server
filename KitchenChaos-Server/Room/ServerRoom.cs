using KitchenChaos_Server.KitchenObject;
using KitchenChaos_Server.Manager;
using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KitchenChaos_Server.Room
{
    public class ServerRoom
    {
        public int RoomId { get; private set; }
        public string RoomName { get; private set; }
        public int MaxPlayer { get; private set; }
        public int OwnPlayerId { get; private set; }

        public List<ServerClient> clients;

        public Dictionary<int,ServerPlayer> players;

        public ServerCounterManager CounterManager { get; private set; }

        public bool IsFull
        {
            get
            {
                lock (stateLock)
                {
                    return players.Count >= MaxPlayer;
                }
            }
        }

        public ServerGameManager RoomGameManager { get; private set; }
        public ServerDeliveryManager RoomDeliveryManager { get; private set; }
        public ServerPlateManager ServerPlateManager { get; private set; }

        /// <summary>
        /// 房间级状态锁：串行化所有对共享状态（clients/players/counters/plates）的访问。
        /// 锁顺序约定：先取 room 锁，再取 DeliveryManager 锁；严禁在 DeliveryManager 锁内调用 room 方法（避免死锁）。
        /// </summary>
        private readonly object stateLock = new object();

        /// <summary>
        /// 游戏主循环用的 CancellationTokenSource（ServerGameManager 消费）
        /// </summary>
        private CancellationTokenSource gameCts;

        private bool gameSceneTransitionStarted = false;
        public bool SceneTransitionStarted
        {
            get { lock (stateLock) { return gameSceneTransitionStarted; } }
        }

        // 断线重连（D7）已暂时禁用
        ///// <summary>局内掉线席位保留时长（秒），超过即彻底移除</summary>
        //private const double RECONNECT_GRACE_SECONDS = 20.0;

        ///// <summary>旧 PlayerId → 局内掉线暂存席（D7）</summary>
        //private readonly Dictionary<int, ReconnectSlot> reconnectSlots = new Dictionary<int, ReconnectSlot>();

        /// <summary>已经上报 GameScene 加载完成的玩家</summary>
        private readonly HashSet<int> loadedGameScenePlayerIds = new HashSet<int>();

        /// <summary>开局转场发起时间（UTC），用于“有人始终不上报”时的兜底放行</summary>
        private DateTime sceneTransitionStartTimeUtc;

        /// <summary>等齐所有人上报的最长等待（秒），防开局死锁</summary>
        private const double LOADED_WAIT_TIMEOUT_SECONDS = 8.0;

        // 断线重连（D7）已暂时禁用
        ///// <summary>局内掉线暂存席（D7），大厅阶段不启用</summary>
        //public class ReconnectSlot
        //{
        //    /// <summary>保留玩家对象（位置/手持/座位）</summary>
        //    public ServerPlayer Player;
        //    /// <summary>认领截止时间</summary>
        //    public DateTime ExpireUtc;
        //}

        public ServerRoom(int _id, string _name, int _maxPlayer, int _ownerPlayerId)
        {
            RoomId = _id;
            RoomName = _name;
            MaxPlayer = _maxPlayer;
            OwnPlayerId = _ownerPlayerId;

            clients = new List<ServerClient>();
            players = new Dictionary<int, ServerPlayer>();

            gameCts = new CancellationTokenSource();
            CounterManager = new ServerCounterManager(this);
            RoomGameManager = new ServerGameManager(this, gameCts);
            RoomDeliveryManager = new ServerDeliveryManager(this);
            ServerPlateManager = new ServerPlateManager(this);  
        }

        public int AddClient(ServerClient _client)
        {
            lock (stateLock)
            {
                if (_client.CurrentRoom == this)
                {
                    Console.WriteLine($"[ServerRoom] 房间：{RoomId} 已经 连接了客户端{_client}");
                    return -1;
                }

                if (!clients.Contains(_client))
                {
                    clients.Add(_client);

                    _client.CurrentRoom = this;
                    int seatId = players.Count;// 给客户端的玩家分配座位id
                    _client.Player.SeatId = seatId;
                    players.Add(_client.Player.PlayerId,_client.Player);

                    //gameManager.OnPlayerConnected(_client.Player.PlayerId);

                    return seatId;
                }
                else
                {
                    Console.WriteLine($"[ServerRoom] 房间：{RoomId} 已经 连接了客户端{_client}");
                }

                return -1;
            }
        }

        public bool RemoveClient(ServerClient _client)
        {
            lock (stateLock)
            {
                if (!clients.Contains(_client))
                {
                    Console.WriteLine($"[ServerRoom] 房间：{RoomId} 不存在客户端{_client}");
                    return false;
                }

                bool inLobby = RoomGameManager.GameStata == EGameState.WaitingForPlayers
                               && !gameSceneTransitionStarted;

                // ── 局内（对局进行中/转场中）：保留席位，进入重连窗口（D7） ──
                if (!inLobby)
                {
                    return HandleInGameDisconnect(_client);
                }

                // ── 大厅：正常移除 ──
                int leavingPlayerId = _client.Player.PlayerId;
                clients.Remove(_client);
                players.Remove(leavingPlayerId);
                _client.CurrentRoom = null;

                // 房主离开且还有人 → 选最小 PlayerId 作为新房主并广播
                if (leavingPlayerId == OwnPlayerId && players.Count > 0)
                {
                    int newOwnerPlayerId = ServerRule.PickNewOwnerPlayerId(players.Keys);
                    OwnPlayerId = newOwnerPlayerId;
                    Console.WriteLine($"[ServerRoom] 房间 {RoomId} 房主转移：{leavingPlayerId} → {newOwnerPlayerId}");
                    BroadcastAll(NetworkMessage.Create_RoomOwnerChanged(newOwnerPlayerId));
                }

                // 通知剩余客户端玩家离开，并刷新权威房间列表（含准备状态）
                BroadcastExcept(_client, NetworkMessage.Create_PlayerLeft(leavingPlayerId));
                BroadcastExcept(_client, NetworkMessage.Create_RoomPlayerListSync(GetRoomPlayerInfo()));

                return clients.Count == 0;
            }
        }

        /// <summary>
        /// 局内断开：把玩家移入暂存席（保留 ServerPlayer 对象：位置/手持/座位），
        /// 广播 PlayerLeft 让其他客户端移除其远程模型。
        /// 返回“房间是否已空”（空则 RoomManager 回收）。
        /// 调用方已持有 stateLock。
        /// </summary>
        private bool HandleInGameDisconnect(ServerClient _client)
        {
            //int leavingPlayerId = _client.Player.PlayerId;

            //// 从“在线集合”移除
            //clients.Remove(_client);
            //players.Remove(leavingPlayerId);
            //_client.CurrentRoom = null;

            //// 若该玩家已有暂存席（异常重复断开），先清旧
            //reconnectSlots.Remove(leavingPlayerId);

            //// 保留玩家对象，等待重连认领（保留位置/手持，FullSync 负责重建世界）
            //reconnectSlots[leavingPlayerId] = new ReconnectSlot
            //{
            //    Player = _client.Player,
            //    ExpireUtc = DateTime.UtcNow.AddSeconds(RECONNECT_GRACE_SECONDS),
            //};

            //Console.WriteLine($"[ServerRoom] 房间 {RoomId} 玩家 {leavingPlayerId} 局内掉线，席位保留至 "
            //                  + $"{reconnectSlots[leavingPlayerId].ExpireUtc:HH:mm:ss}");

            //// 通知其他客户端：该玩家离开（远程模型移除）
            //BroadcastAll(NetworkMessage.Create_PlayerLeft(leavingPlayerId));

            //// 转场中有人掉线：剩余玩家可能已全部上报 loaded，重估开局（避免死等 8s 超时兜底）
            //if (gameSceneTransitionStarted)
            //{
            //    TryStartGameplayIfAllLoaded();
            //}

            //// 房间已无人（在线）→ 返回空；RoomManager 会销毁房间（暂存席一并作废）
            //return clients.Count == 0;
            return false;
        }

        // 断线重连（D7）已暂时禁用
        /*
        /// <summary>清理已过期的局内暂存席（线程安全，可由游戏循环等任意线程周期调用）。</summary>
        public void PruneReconnectSlots()
        {
            //lock (stateLock)
            //{
            //    if (reconnectSlots.Count == 0) return;

            //    foreach (var kv in reconnectSlots.ToList())
            //    {
            //        if (DateTime.UtcNow > kv.Value.ExpireUtc)
            //        {
            //            Console.WriteLine($"[ServerRoom] 房间 {RoomId} 玩家 {kv.Key} 重连窗口过期，席位已释放");
            //            reconnectSlots.Remove(kv.Key);
            //        }
            //    }
            //}
        }
        */

        // 断线重连（D7）已暂时禁用
        /*
        /// <summary>
        /// 认领局内席位：验证暂存席存在/未过期，把新连接绑定到旧 ServerPlayer 并重新加入在线集合。
        /// </summary>
        public bool TryResumePlayer(ServerClient _client, int _oldPlayerId)
        {
            lock (stateLock)
            {
                if (!reconnectSlots.TryGetValue(_oldPlayerId, out var slot))
                {
                    Console.WriteLine($"[ServerRoom] 房间 {RoomId} 无 {_oldPlayerId} 的暂存席，认领失败");
                    return false;
                }

                if (DateTime.UtcNow > slot.ExpireUtc)
                {
                    reconnectSlots.Remove(_oldPlayerId);
                    Console.WriteLine($"[ServerRoom] 房间 {RoomId} {_oldPlayerId} 暂存席已过期，认领失败");
                    return false;
                }

                if (_client.Player.PlayerId == _oldPlayerId)
                {
                    Console.WriteLine($"[ServerRoom] 房间 {RoomId} 玩家 {_oldPlayerId} 已在房间，无需认领");
                    return false;
                }

                ServerPlayer resumed = slot.Player;
                reconnectSlots.Remove(_oldPlayerId);

                // 新连接认领旧身份：把新 ServerClient 的 Player 替换成旧玩家对象，并重入在线集合
                _client.Player = resumed;
                resumed.AttachClient(_client);
                _client.CurrentRoom = this;

                clients.Add(_client);
                players[resumed.PlayerId] = resumed;

                Console.WriteLine($"[ServerRoom] 房间 {RoomId} 玩家 {resumed.PlayerId} 认领成功，重新加入");

                // 给认领者本人：旧 PlayerId、最后位置、手持（普通物）；盘子由 FullSync 恢复
                _client.Send(NetworkMessage.Create_ResumeGameResult(
                    true,
                    resumed.PlayerId,
                    resumed.LastPosX,
                    resumed.LastPosY,
                    resumed.LastPosZ,
                    resumed.LastRotY,
                    resumed.HeldItemIndex));

                // 给全房间（含认领者）重发 FullSync：远程玩家/台子/盘子/订单一键重建
                SendFullSyncToAllLocked();

                return true;
            }
        }
        */

        public void BroadcastAll(byte[] _data)
        {
            // 锁内快照客户端列表，锁外发送：避免持有锁时阻塞在网络写入上
            List<ServerClient> snapshot;
            lock (stateLock)
            {
                snapshot = new List<ServerClient>(clients);
            }

            foreach (var client in snapshot)
            {
                client.Send(_data);
            }
        }

        public void BroadcastExcept(ServerClient _client, byte[] _data)
        {
            // 锁内快照客户端列表，锁外发送
            List<ServerClient> snapshot;
            lock (stateLock)
            {
                snapshot = new List<ServerClient>(clients);
            }

            foreach (var client in snapshot)
            {
                if (client != _client)
                {
                    client.Send(_data);
                }
            }
        }

        /// <summary>
        /// 进入游戏场景
        /// </summary>
        public void EnterGameScene()
        {
            lock (stateLock)
            {
                if (gameSceneTransitionStarted) return;
                gameSceneTransitionStarted = true;
                loadedGameScenePlayerIds.Clear();
                sceneTransitionStartTimeUtc = DateTime.UtcNow;

                // 先广播权威玩家列表（含座位与准备状态），再广播场景切换
                BroadcastAll(NetworkMessage.Create_RoomPlayerListSync(GetRoomPlayerInfo()));
                BroadcastAll(NetworkMessage.Create_StartGame(RoomId));

                Console.WriteLine($"[ServerRoom] 房间 {RoomId} 开始转场 GameScene");
            }
        }

        public bool CanStartGame(ServerClient _requestClient)
        {
            lock (stateLock)
            {
                if (gameSceneTransitionStarted) return false;
                if(RoomGameManager.GameStata != EGameState.WaitingForPlayers) return false;
                if(_requestClient.Player.PlayerId != OwnPlayerId) return false;
                if(players.Count < 2) return false;

                foreach(var player in players.Values)
                {
                    if(!player.IsReady) return false;// 所有玩家都必须准备好
                }

                return true;
            }
        }

        /// <summary>
        /// 收到客户端“GameScene 加载完成”：记录后尝试开局。
        /// 全员到齐或超过等待时限（防有人掉线卡死开局）都会进入开局流程。
        /// 调用方已持有 stateLock（ProcessGameMessage 内）。
        /// </summary>
        private void HandleSuccessEnterGameScene(ServerClient _sender)
        {
            if (!gameSceneTransitionStarted) return;      // 还没转场（异常上报）
            if (!clients.Contains(_sender)) return;

            loadedGameScenePlayerIds.Add(_sender.Player.PlayerId);
            TryStartGameplayIfAllLoaded();
        }

        /// <summary>
        /// 尝试开局：全员到齐或超时即启动（幂等，只启动一次）。调用方需持有 stateLock。
        /// </summary>
        private void TryStartGameplayIfAllLoaded()
        {
            // 还没切换到游戏场景
            if (!gameSceneTransitionStarted) return;

            // 没人上报 → 继续等待
            if (loadedGameScenePlayerIds.Count == 0) return;

            bool allLoaded = loadedGameScenePlayerIds.Count >= clients.Count;
            bool timedOut = (DateTime.UtcNow - sceneTransitionStartTimeUtc).TotalSeconds
                            >= LOADED_WAIT_TIMEOUT_SECONDS;

            if (!allLoaded && !timedOut) return;

            if (!CounterManager.IsInitialized)
            {
                Console.WriteLine("[ServerRoom] 开局中止：操作台尚未注册（房主未上报台子）");
                return;
            }

            Console.WriteLine($"[ServerRoom] 房间 {RoomId} 开局：loaded={loadedGameScenePlayerIds.Count}/{clients.Count}");

            RoomDeliveryManager.StartGame();
            RoomGameManager.StartGame();

            gameSceneTransitionStarted = false;
        }

        /// <summary>
        /// 给房间内所有在线客户端各发一份 FullSync（玩家列表排除收件人自己）。
        /// 调用方需持有 stateLock；消息体在锁内构建，网络发送也在锁内（开局/重连的一次性低频操作，可接受）。
        /// </summary>
        private void SendFullSyncToAllLocked()
        {
            var counters = CounterManager.GetCounterSnapshot();

            var platesList = new List<(int plateId, byte ownerType, int ownerId, int soIndex, List<int> ingredients)>();
            foreach (var plate in ServerPlateManager.Plates .Values)
            {
                platesList.Add((plate.PlateId, plate.OwnerType, plate.OwnerId,
                    plate.platerKitchenobjectSOId, new List<int>(plate.CurrentIngredients)));
            }

            int[] recipes = RoomDeliveryManager.GetWaitingRecipeIndexes();

            foreach (var target in clients)
            {
                var playersList = new List<(int id, string name, float x, float y, float z, float rotY)>();
                foreach (var other in clients)
                {
                    if (other == target) continue;
                    var p = other.Player;
                    playersList.Add((p.PlayerId, p.Name, p.LastPosX, p.LastPosY, p.LastPosZ, p.LastRotY));
                }

                target.Send(NetworkMessage.Create_FullSync(
                    playersList.ToArray(), counters, platesList.ToArray(), recipes));
            }
        }

        /// <summary>
        /// 房间停止：取消游戏循环、清理客户端/玩家/操作台/盘子/暂存席等状态。
        /// </summary>
        public void Stop()
        {
            lock (stateLock)
            {
                gameCts?.Cancel();

                foreach (var client in clients)
                {
                    client.CurrentRoom = null;
                }
                clients.Clear();
                players.Clear();
                CounterManager.Clear();
                RoomDeliveryManager.Clear();
                ServerPlateManager.Clear();
                //reconnectSlots.Clear(); // 断线重连（D7）已暂时禁用
                loadedGameScenePlayerIds.Clear();
                gameSceneTransitionStarted = false;
            }
        }

        public void BackRoomScene()
        {
            lock (stateLock)
            {
                gameCts?.Cancel();

                foreach (var player in players.Values)
                {
                    player.IsReady = false; 
                }

                CounterManager.Clear();
                RoomDeliveryManager.Clear();
                ServerPlateManager.Clear();
                //reconnectSlots.Clear(); // 断线重连（D7）已暂时禁用
                loadedGameScenePlayerIds.Clear();
                gameSceneTransitionStarted = false;
            }
        }

        /// <summary>
        /// 处理来自客户端的消息（入口加房间锁，串行化本房间所有消息处理）。
        /// </summary>
        public void HandleGameMessage(ServerClient _sender, MessageType _type, byte[] _data)
        {
            lock (stateLock)
            {
                // 操作台注册类消息（容器物品/切菜配方/煮菜配方/盘子SO/蒸制配方）→ CounterManager
                if (_type == MessageType.C2S_RegisterContainerItem
                    || _type == MessageType.C2S_RegisterCuttingRecipes
                    || _type == MessageType.C2S_RegisterBoilingRecipes
                    || _type == MessageType.C2S_RegisterPlateSpawnId
                    || _type == MessageType.C2S_RegisterStreamingRecipes)
                {
                    CounterManager.HandleRegisterMessage(_type, _data);
                    return;
                }

                switch (_type)
                {
                    case MessageType.C2S_PlayerReady:// 成功
                        HandlePlayerReady(_sender, _data);
                        break;
                    case MessageType.C2S_PlayerMove:// 成功
                        HandlePlayerMove(_sender, _data);
                        break;
                    case MessageType.C2S_PlayerInteract:// 成功
                        HandlePlayerInteract(_sender, _data);
                        break;
                    case MessageType.C2S_PlayerInteractAlternate:// 成功
                        HandleInteractAlternate(_sender, _data);
                        break;
                    case MessageType.C2S_CounterRegister:// 成功
                        CounterManager.HandleCounterRegister(_data);
                        break;
                    case MessageType.C2S_RegisterPlateSO:// 成功
                        HandleRegisterPlateSO(_sender, _data);
                        break;
                    case MessageType.C2S_RegisterRecipes:// 成功
                        HandleRegisterRecipes(_sender, _data);
                        break;
                    case MessageType.C2S_SendMessage:
                        HandleSendMessage(_sender, _data);
                        break;
                    case MessageType.C2S_ScucessEnterGameScene:
                        HandleSuccessEnterGameScene(_sender);
                        break;
                    case MessageType.C2S_RequestBackRoom:
                        HandleRequestBackRoom();
                        break;
                    default:
                        // 未处理的消息类型
                        break;
                }
            }
        }

        #region 消息处理

        private void HandlePlayerReady(ServerClient _client,byte[] _data)
        {
            var (playerId, isRead) = NetworkMessage.Parse_PlayerReady(_data);
            if(playerId != _client.Player.PlayerId)
            {
                Console.WriteLine($"[ServerRoom] 玩家ID不匹配: {playerId} != {_client.Player.PlayerId}");
                return;
            }
    
            lock (stateLock)
            {
                foreach (var id in players.Keys)
                {
                    if (id == playerId)
                    {
                        players[playerId].IsReady = isRead;
                        BroadcastAll(NetworkMessage.Create_PlayerReady(playerId, isRead));
                    }
                }
            }
        }
        /// <summary>
        /// 同步玩家移动
        /// </summary>
        private void HandlePlayerMove(ServerClient _sender, byte[] _data)
        {
            var (x, y, z, rotY) = NetworkMessage.Parse_PlayerMove(_data);
            _sender.Player.LastPosX = x;
            _sender.Player.LastPosY = y;
            _sender.Player.LastPosZ = z;
            _sender.Player.LastRotY = rotY;
            BroadcastExcept(_sender, NetworkMessage.Create_PlayerPosition(_sender.Player.PlayerId, x, y, z, rotY));
        }
        
        /// <summary>
        /// 处理玩家交互
        /// </summary>
        private void HandlePlayerInteract(ServerClient _sender, byte[] _data) 
        {
            var (playerId,counterId) = NetworkMessage.Parse_PlayerInteract(_data);
            CounterManager.HandlePlayerInteract(_sender.Player,counterId);
        }

        /// <summary>
        /// 处理副交互（空格键）——切菜等进度操作台
        /// </summary>
        private void HandleInteractAlternate(ServerClient _sender, byte[] _data)
        {
            int counterId = NetworkMessage.Parse_InteractAlternate(_data);
            CounterManager.HandlePlayerInteractAlternate(_sender.Player, counterId);
        }

        private void HandleRegisterPlateSO(ServerClient _sender, byte[] _data) 
        {
            var (plateId, soid) = NetworkMessage.Parse_RegisterPlateSO(_data);

            if (!ServerPlateManager.Plates.TryGetValue(plateId, out ServerPlate plate))
            {
                Console.WriteLine($"[GameServer] 要注册SO的盘子不存在: plateId={plateId}");
                return;
            }

            plate.SetPlateSO(soid);
        }

        /// <summary>
        /// 处理客户端上报的配方表：服务端校验并注册（第一个上报者优先，与 C2S_CounterRegister 一致）。
        /// 内容由客户端提供，但订单的生成/校验/计分仍由服务端执行。
        /// </summary>
        private void HandleRegisterRecipes(ServerClient _sender, byte[] _data)
        {
            var recipes = NetworkMessage.Parse_RegisterRecipes(_data);
            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 上报配方表，共 {recipes.Count} 条");

            if (RecipeRegistry.RegisterRecipes(recipes))
            {
                Console.WriteLine("[GameServer] 配方表注册成功");
            }
            else
            {
                Console.WriteLine("[GameServer] 配方表注册被拒绝（已注册或校验失败）");
            }
        }

        /// <summary>
        /// 处理聊天消息：使用服务器记录的发送者ID，转发给其他客户端。
        /// 发送者本地已显示自己的消息，因此使用 BroadcastExcept。
        /// </summary>
        private void HandleSendMessage(ServerClient _sender, byte[] _data) 
        {
            var (_, message) = NetworkMessage.Parse_SendMessage(_data);
            if (string.IsNullOrEmpty(message)) return;

            int playerId = _sender.Player.PlayerId;
            Console.WriteLine($"[GameServer] 玩家{playerId} 发送聊天消息: {message}");

            // 转发给除发送者以外的所有玩家
            BroadcastExcept(_sender, NetworkMessage.Create_SendMessage(playerId, message));
        }

        private void HandleRequestBackRoom()
        {
            BackRoomScene();
            BroadcastAll(NetworkMessage.Create_BackRoom());
            BroadcastAll(NetworkMessage.Create_RoomPlayerListSync(GetRoomPlayerInfo()));
        }
        #endregion

        public List<ServerPlayer> GetRoomPlayerInfo()
        {
            lock (stateLock)
            {
                List<ServerPlayer> list = players.Values.ToList();
                return list;
            }
        }
    }
}