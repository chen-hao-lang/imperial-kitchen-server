using KitchenChaos_Server.Counter;
using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

        private bool countersInitialized = false;
        public Dictionary<int,BaseCounter> counters;

        private int nextPlateId;
        public Dictionary<int,ServerPlate> plates;

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

        /// <summary>
        /// 房间级状态锁：串行化所有对共享状态（clients/players/counters/plates）的访问。
        /// 锁顺序约定：先取 room 锁，再取 DeliveryManager 锁；严禁在 DeliveryManager 锁内调用 room 方法（避免死锁）。
        /// </summary>
        private readonly object stateLock = new object();

        /// <summary>
        /// 操作台更新间隔（秒），约 20 次/秒
        /// </summary>
        private const float COUNTER_TICK_INTERVAL = 0.05f;

        /// <summary>
        /// 操作台更新用的 CancellationTokenSource
        /// </summary>
        private CancellationTokenSource counterCts;
        private CancellationTokenSource gameCts;

        public ServerRoom(int _id, string _name, int _maxPlayer, int _ownerPlayerId)
        {
            RoomId = _id;
            RoomName = _name;
            MaxPlayer = _maxPlayer;
            OwnPlayerId = _ownerPlayerId;

            clients = new List<ServerClient>();
            players = new Dictionary<int, ServerPlayer>();
            counters = new Dictionary<int, BaseCounter>();
            plates = new Dictionary<int, ServerPlate>();

            gameCts = new CancellationTokenSource();
            RoomGameManager = new ServerGameManager(this, gameCts.Token);
            RoomDeliveryManager = new ServerDeliveryManager(this);

            counterCts = new CancellationTokenSource();
            //_ = CounterUpdateLoopAsync(counterCts.Token);
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
                if (clients.Contains(_client))
                {
                    clients.Remove(_client);

                    players.Remove(_client.Player.PlayerId);
                    //gameManager.OnPlayerLefted(_client.Player.PlayerId);
                    _client.CurrentRoom = null;

                    //TODO:房主转移

                    //广播给其他客户端进行更新
                    List<int> seadIds = new List<int>();
                    List<int> currentPlayerIds = new List<int>();
                    List<string> currentPlayersName = new List<string>();
                    foreach (var player in players.Values)
                    {
                        seadIds.Add(player.SeatId);
                        currentPlayerIds.Add(player.PlayerId);
                        currentPlayersName.Add(player.Name);
                    }
                    BroadcastExcept(_client, NetworkMessage.Create_RoomPlayerListSync(GetRoomPlayerInfo()));

                    return clients.Count == 0;
                }
                else
                {
                    Console.WriteLine($"[ServerRoom] 房间：{RoomId} 不存在客户端{_client}");
                    return false;
                }
            }
        }

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

        public void StartGame()
        {
            // 先广播权威的玩家列表（含座位号），保证所有客户端进入游戏场景时
            // NetworkManager.RoomPlayers 是完整且新鲜的（房主此前从未收到过 RoomPlayerListSync）。
            BroadcastAll(NetworkMessage.Create_RoomPlayerListSync(GetRoomPlayerInfo()));
            // 再广播开始游戏，客户端收到后切换到 GameScene
            BroadcastAll(NetworkMessage.Create_StartGame(RoomId));
        }

        public void Stop()
        {
            lock (stateLock)
            {
                counterCts?.Cancel();
                gameCts?.Cancel();

                foreach (var client in clients)
                {
                    client.CurrentRoom = null;
                }
                clients.Clear();
                players.Clear();
                counters.Clear();
                plates.Clear();
            }
        }

        /// <summary>
        /// 操作台更新循环（独立后台任务）。
        /// 负责 PlateCounter 盘子生成计时等需要持续更新的逻辑。
        /// </summary>
        private async Task CounterUpdateLoopAsync(CancellationToken _ct)
        {
            Console.WriteLine("[GameServer] 操作台更新循环已启动");

            while (!_ct.IsCancellationRequested)
            {
                try
                {
                    float deltaTime = COUNTER_TICK_INTERVAL;
                    lock (stateLock)
                    {
                        RoomDeliveryManager.Update(deltaTime);
                        UpdateCounters(deltaTime);
                    }
                    await Task.Delay((int)(COUNTER_TICK_INTERVAL * 1000), _ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameServer] 操作台更新出错: {ex.Message}");
                }
            }

            Console.WriteLine("[GameServer] 操作台更新循环已停止");
        }

        /// <summary>
        /// 每帧更新所有操作台
        /// </summary>
        private void UpdateCounters(float _deltaTime)
        { 
            foreach (var counter in counters.Values)
            {
                if (counter.Type == CounterType.BoilingCounter && counter is ServerCounter serverCounter)
                {
                    // 如果这个煮沸台当前没有在煮，则跳过该台的后续处理（不要 return，会中断对所有台的更新）
                    if (!serverCounter.IsBoiling) continue;

                    serverCounter.BoilingTimer += _deltaTime;
                    float progress = serverCounter.BoilingProgressNormalized;

                    // 广播煮沸进度给所有客户端
                    BroadcastAll(NetworkMessage.Create_CounterProgressSync(serverCounter.CounterId, progress));

                    // 煮沸完成
                    if (serverCounter.BoilingTimer >= serverCounter.MaxBoilingTimer)
                    {
                        serverCounter.IsBoiling = false;
                        serverCounter.BoilingTimer = 0f;
                        serverCounter.MaxBoilingTimer = 0f;
                        Console.WriteLine($"[GameServer] BoilingCounter {counter.CounterId} 煮沸完成！");

                        // 发送最终进度 1.0（客户端 ApplyServerBoilingProgress 会自行做物品转换）
                        BroadcastAll(NetworkMessage.Create_CounterProgressSync(counter.CounterId, 1.0f));
                    }
                    continue;
                }

                if (counter.Type == CounterType.PlateCounter && counter is PlateCounter plateCounter)
                {
                    plateCounter.PlateSpawnTimer += _deltaTime;
                    if (plateCounter.PlateSpawnTimer >= plateCounter.PLATE_SPAWN_INTERVAL)
                    {
                        plateCounter.PlateSpawnTimer = 0f;
                        if (plateCounter.PlateCount < plateCounter.PLATE_MAX)
                        {
                            plateCounter.PlateCount++;
                            plateCounter.Plates.Add(nextPlateId);

                            // 生成一个新的盘子（初始持有者是当前 PlateCounter）
                            ServerPlate newPlate = new ServerPlate(nextPlateId)
                            {
                                OwnerType = 1,                    // 操作台
                                OwnerId = plateCounter.CounterId  // 当前 PlateCounter
                            };
                            if (!plates.ContainsValue(newPlate))
                            {
                                plates.Add(newPlate.PlateId,newPlate);
                            }
                            Console.WriteLine($"[GameServer] PlateCounter {plateCounter.CounterId} 生成盘子，当前数量={plateCounter.PlateCount}");

                            BroadcastAll(NetworkMessage.Create_PlateSpawned(plateCounter.CounterId, newPlate.PlateId));
                            nextPlateId++;
                        }
                    }
                    continue;
                }
            }
        }

        /// <summary>
        /// 发送全量更新
        /// </summary>
        private void SendFullSyncToClient(ServerClient _targetClient)
        {
            lock (stateLock)
            {
                var playersList = new List<(int id, string name, float x, float y, float z, float rotY)>();
                foreach (var c in clients)
                {
                    if (c != _targetClient)
                    {
                        playersList.Add((c.Player.PlayerId, $"玩家{c.Player.PlayerId}", c.Player.LastPosX, c.Player.LastPosY, c.Player.LastPosZ, c.Player.LastRotY));
                    }
                }

                var countersList = new (int counterId, int itemIndex)[this.counters.Count];
                int i = 0;
                foreach (var kv in this.counters.Values)
                {
                    countersList[i] = (kv.CounterId, kv.ItemIndex);
                    i++;
                }

                // 收集所有盘子
                var platesList = new (int plateId, byte ownerType, int ownerId, int soIndex, List<int> ingredients)[plates.Count];
                int j = 0;
                foreach (var kv in plates.Values)
                {
                    platesList[j] = (kv.PlateId, kv.OwnerType, kv.OwnerId, kv.SoIndex, kv.CurrentIngredients);
                    j++;
                }

                // 收集等待中的订单（服务器权威，中途加入的玩家恢复订单状态）
                int[] recipeIndexes = RoomDeliveryManager?.GetWaitingRecipeIndexes() ?? new int[0];

                _targetClient.Send(NetworkMessage.Create_FullSync(
                    playersList.ToArray(),
                    countersList,
                    platesList,
                    recipeIndexes));
            }
        }

        /// <summary>
        /// 处理来自客户端的消息（入口加房间锁，串行化本房间所有消息处理）。
        /// </summary>
        public void HandleGameMessage(ServerClient _sender, MessageType _type, byte[] _date)
        {
            lock (stateLock)
            {
                ProcessGameMessage(_sender, _type, _date);
            }
        }

        /// <summary>
        /// 实际的消息处理逻辑（调用方需持有 stateLock）。
        /// </summary>
        private void ProcessGameMessage(ServerClient _sender, MessageType _type, byte[] _data)
        {
            switch (_type)
            {
                case MessageType.C2S_PlayerReady:// 成功
                    HandlePlayerReady(_data);
                    break;
                case MessageType.C2S_PlayerMove:// 成功
                    HandlePlayerMove(_sender, _data);
                    break;
                case MessageType.C2S_CounterRegister:// 成功
                    HandleCounterRegister(_sender, _data);
                    break;
                case MessageType.C2S_CounterStartMark:
                    HandleCounterStartMark(_sender, _data);
                    break;
                case MessageType.C2S_PlayerInteract:// 成功
                    HandlePlayerInteract(_sender, _data);
                    break;
                case MessageType.C2S_PlayerInteractAlternate:// 成功
                    HandleInteractAlternate(_sender, _data);
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
                    RoomDeliveryManager.StartGame();
                    // 开始同步操作台的状态
                    _ = CounterUpdateLoopAsync(counterCts.Token);
                    break;
                default:
                    // 未处理的消息类型
                    break;
            }
        }

        #region 消息处理

        private void HandlePlayerReady(byte[] _data)
        {
            var (playerId, isRead) = NetworkMessage.Parse_PlayerReady(_data);
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
        /// 注册当前房间内的操作台
        /// </summary>
        /// <param name="_sender"></param>
        /// <param name="_data"></param>
        private void HandleCounterRegister(ServerClient _sender, byte[] _data)
        {
            if (countersInitialized)
            {
                Console.WriteLine("[GameServer] 操作台已初始化，忽略重复上报");
                return;
            }

            var mapping = NetworkMessage.Parse_CounterRegister(_data);
            counters.Clear();

            foreach (var (counterId, typeByte) in mapping)
            {
                var type = (CounterType)typeByte;

                // PlateCounter 初始化盘子数量
                if (type == CounterType.PlateCounter)
                {
                    PlateCounter plateCounter = new PlateCounter(counterId, type);
                    plateCounter.PlateCount = 0;
                    plateCounter.PlateSpawnTimer = 0f;

                    counters.Add(counterId,plateCounter);
                }
                else
                {
                    var counter = new ServerCounter(counterId, type);
                    counters.Add(counterId,counter);
                }

                Console.WriteLine($"[GameServer] 注册操作台: ID={counterId}, 类型={type}");
            }

            countersInitialized = true;
            Console.WriteLine($"[GameServer] 操作台类型映射初始化完成，共 {counters.Count} 个");
        }

        /// <summary>
        /// 同步操作台制作
        /// </summary>
        private void HandleCounterStartMark(ServerClient _sender, byte[] _data) 
        {
            var (counterId, maxBoilingTimer) = NetworkMessage.Parse_CounterStartMark(_data);

            if (!counters.TryGetValue(counterId, out var counter))
            {
                Console.WriteLine($"[GameServer] 收到未知操作台的状态更新: ID={counterId}");
                return;
            }

            if (counter.Type == CounterType.BoilingCounter && counter is ServerCounter serverCounter)
            {
                serverCounter.IsBoiling = true;
                serverCounter.BoilingTimer = 0f;
                serverCounter.MaxBoilingTimer = maxBoilingTimer;
                Console.WriteLine($"[GameServer] BoilingCounter {counterId} 开始煮沸，时长={maxBoilingTimer:F1}s");
            }
        }

        /// <summary>
        /// 处理玩家交互
        /// </summary>
        private void HandlePlayerInteract(ServerClient _sender, byte[] _data) 
        {
            int counterId = NetworkMessage.Parse_InteractAlternate(_data);
            ServerPlayer player = _sender.Player;

            if (!counters.TryGetValue(counterId, out var counter))
                return;

            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 副交互: 台子{counterId} 类型={counter.Type}");

            if (counter is ServerCounter serverCounter)
            {
                if (counter.Type == CounterType.ContainerCounter)// 如果玩家与容器交互
                {
                    if (player.HeldItemIndex == -1)// 如果玩家手里没东西
                    {
                        player.HeldItemIndex = serverCounter.ItemIndex;// 将容器里的东西给玩家
                        BroadcastAll(NetworkMessage.Create_InteractionSync(-1, -1, player.PlayerId, player.HeldItemIndex));// 网络同步
                    }
                    return;
                }

                // 与切菜台 和 桌子交互
                if (counter.Type == CounterType.ClearCounter && counter.Type == CounterType.CuttingCounter)
                {
                    HandleClearCounterInteract(serverCounter, player);
                    return;
                }

                // 与煮器 交互
                if (counter.Type == CounterType.BoilingCounter)
                {
                    HandleBoilingCounterInteract(serverCounter,player);
                    return;
                }

                // 与垃圾桶交互
                if (counter.Type == CounterType.TrashCounter)
                {
                    if (player.HeldItemIndex != -1 && !TryGetPlateByOwner(player.PlayerId, out _))
                    {
                        player.HeldItemIndex = -1;
                        BroadcastAll(NetworkMessage.Create_InteractionSync(counter.CounterId, -1, player.PlayerId, -1));
                    }
                    return;
                }

                // 和交付台交互
                if (counter.Type == CounterType.DeliveryCounter)
                {
                    if (TryGetPlateByOwner(player.PlayerId, out var plate))
                        HandleDeliveryRequest(player, plate.PlateId);
                }
            }

            if (counter is PlateCounter plateCounter)
            {
                if (counter.Type == CounterType.PlateCounter)
                {
                    HandlePlateCounterInteract(plateCounter, player);
                }
            }
        }
        #region HandlePlayerInteract
        /// <summary>
        /// 处理八仙桌的逻辑
        /// </summary>
        /// <param name="_counter"></param>
        /// <param name="_player"></param>
        private void HandleClearCounterInteract(ServerCounter _counter, ServerPlayer _player)
        {
            // 检查操作台上有没有盘子
            bool counterHasPlate = TryGetPlateOnCounter(_counter.CounterId, out ServerPlate plateOnCounter);

            if (_counter.ItemIndex == -1 && !counterHasPlate)// 台子上面什么都没有
            {
                if (_player.HeldItemIndex == -1) return;// 玩家手里也没东西，直接返回

                if (TryGetPlateByOwner(_player.PlayerId, out var heldPlate))// 玩家手里有盘子
                {
                    heldPlate.OwnerType = 1;
                    heldPlate.OwnerId = _counter.CounterId;
                    BroadcastAll(NetworkMessage.Create_PlateOwnerSync(heldPlate));
                }
                else
                {
                    _counter.ItemIndex = _player.HeldItemIndex;
                    _player.HeldItemIndex = -1;
                }
                BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId,_counter.ItemIndex,_player.PlayerId,_player.HeldItemIndex));
                return;
            }

            if (counterHasPlate)// 如果台子上是盘子
            {
                if (_player.HeldItemIndex == -1)// 玩家手里没东西
                {
                    plateOnCounter.OwnerType = 0;// 把盘子给玩家
                    plateOnCounter.OwnerId = _player.PlayerId;
                    BroadcastAll(NetworkMessage.Create_PlateOwnerSync(plateOnCounter));
                    BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId,-1,_player.PlayerId,-1));
                }
                else if(!TryGetPlateByOwner(_player.PlayerId,out var plateOnPlayer))// 玩家手里有配料
                {
                    int ing = _player.HeldItemIndex;
                    _player.HeldItemIndex = -1;
                    BroadcastAll(NetworkMessage.Create_PlateAddIngredient(plateOnPlayer.PlateId,ing));
                    BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
                }

                return;
            }

            // 玩家手里没东西，操作台上有东西，玩家拿起操作台上的东西
            if (_player.HeldItemIndex == -1)
            {
                // 玩家拿起台上的普通物
                _player.HeldItemIndex = _counter.ItemIndex;
                _counter.ItemIndex = -1;
                BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, _player.HeldItemIndex));
            }
            else if (TryGetPlateByOwner(_player.PlayerId, out var heldPlate2))// 玩家手里有盘子，把操作台上的配料加到玩家手里的盘子上
            {
                if (heldPlate2.TryAddIngredient(_counter.ItemIndex))
                {
                    int ing = _counter.ItemIndex;
                    _counter.ItemIndex = -1;
                    BroadcastAll(NetworkMessage.Create_PlateAddIngredient(heldPlate2.PlateId, ing));
                    BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
                }
            }

        }

        private void HandleBoilingCounterInteract(ServerCounter _counter, ServerPlayer _player)
        {
            // ── 锅空闲：倒料 ──
            if (_counter.ItemIndex == -1 && !_counter.IsBoiling)
            {
                if (TryGetPlateByOwner(_player.PlayerId, out var heldPlate))
                {
                    foreach (var ing in new List<int>(heldPlate.CurrentIngredients))   // 副本遍历，边遍历边删
                    {
                        //if (锅可放(ing))        // ⚠️ 依赖：服务器煮菜配方表（本阶段可先宽松：仅索引合法）
                        //{
                        //    sc.Ingredients.Add(ing);
                        //    heldPlate.Ingredients.Remove(ing);
                        //    BroadcastAll(Create_PlateRemoveIngredient(heldPlate.PlateId, ing));
                        //}
                    }
                    BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
                }
                //else if (_player.HeldItemIndex != -1 && 锅可放(_player.HeldItemIndex))
                //{
                //    sc.Ingredients.Add(_player.HeldItemIndex);
                //    _player.HeldItemIndex = -1;
                //    BroadcastAll(Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
                //}
                // 服务器侧配方匹配 + CounterStateSync（开始煮）→ 留到下阶段
                return;
            }

            // 锅里面有成品，装盘
            if (_counter.ItemIndex != -1 && TryGetPlateByOwner(_player.PlayerId, out var plateOnPlayer))
            {
                if (plateOnPlayer.TryAddIngredient(_counter.ItemIndex))
                {
                    int outIdx = _counter.ItemIndex;
                    _counter.ItemIndex = -1;
                    _counter.Ingredients.Clear(); 
                    _counter.IsBoiling = false;
                    BroadcastAll(NetworkMessage.Create_PlateAddIngredient(plateOnPlayer.PlateId, outIdx));
                    BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
                }
            }
        }

        private void HandlePlateCounterInteract(PlateCounter _counter, ServerPlayer _player)
        {
            if (_player.HeldItemIndex != -1) return;// 玩家手里有东西，直接退出
            if (_counter.Plates.Count == 0) return;// 操作台上没盘子，也直接退出

            // 拿走盘子
            int plateId = _counter.Plates[_counter.Plates.Count - 1];
            _counter.Plates.RemoveAt(_counter.Plates.Count - 1);
            _counter.PlateCount--;

            if (!plates.TryGetValue(plateId, out var plate)) return;

            plate.OwnerType = 0; 
            plate.OwnerId = _player.PlayerId;

            // 网络广播
            BroadcastAll(NetworkMessage.Create_PlateOwnerSync(plate));// 实体归玩家
            BroadcastAll(NetworkMessage.Create_RemotePlate(_counter.CounterId, plateId));
            BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
        }

        /// <summary>
        /// 处理客户端提交的交付请求：转交给服务器权威的订单管理器校验并广播结果。
        /// </summary>
        private void HandleDeliveryRequest(ServerPlayer _player,int _plateId)
        {
            RoomDeliveryManager?.HandleDeliveryRequest(_plateId);   // 已有：校验 + DestroyPlate + 广播

            // 交付后玩家手里为空
            if (_player.HeldItemIndex != -1 || TryGetPlateByOwner(_player.PlayerId, out _))
            {
                _player.HeldItemIndex = -1;
                BroadcastAll(NetworkMessage.Create_InteractionSync(-1, -1, _player.PlayerId, -1));
            }
        }

        /// <summary>
        /// 查看玩家手里的盘子
        /// </summary>
        /// <param name="_playerId"></param>
        /// <param name="_plate"></param>
        /// <returns></returns>
        private bool TryGetPlateByOwner(int _playerId, out ServerPlate _plate)
        {
            foreach (var plate in plates.Values)
            {
                if (plate.IsHeldByPlayer(_playerId))
                {
                    _plate = plate;
                    return true;
                }
            }

            _plate = null;
            return false;
        }

        /// <summary>
        /// 查看操作台上的盘子
        /// </summary>
        /// <param name="_counterId"></param>
        /// <param name="_plate"></param>
        /// <returns></returns>
        private bool TryGetPlateOnCounter(int _counterId, out ServerPlate _plate)
        {
            foreach (var plate in plates.Values)
            {
                if (plate.IsOnCounter(_counterId))
                {
                    _plate = plate;
                    return true;
                }
            }

            _plate = null;
            return false;
        }
        #endregion

        /// <summary>
        /// 处理副交互（空格键）——切菜等进度操作台
        /// </summary>
        private void HandleInteractAlternate(ServerClient _sender, byte[] _data)
        {
            int counterId = NetworkMessage.Parse_InteractAlternate(_data);
            ServerPlayer player = _sender.Player;

            if (!counters.TryGetValue(counterId, out var counter))
                return;

            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 副交互: 台子{counterId} 类型={counter.Type}");

            if (counter is ServerCounter serverCounter)
            {
                if (counter.Type == CounterType.CuttingCounter)
                {
                    HandleCuttingInteractAlternate(serverCounter);
                }
            }
        }

        /// <summary>
        /// 处理切菜操作台的副交互
        /// </summary>
        private void HandleCuttingInteractAlternate(ServerCounter _counter)
        {
            // 台子上必须有东西才能切
            if (_counter.ItemIndex < 0)
            {
                Console.WriteLine($"[GameServer] CuttingCounter {_counter.CounterId} 上面没有物品，忽略切菜");
                return;
            }

            _counter.IsCutting = true;
            _counter.CutCount++;
            float progress = _counter.CuttingProgressNormalized;

            Console.WriteLine($"[GameServer] CuttingCounter {_counter.CounterId} 切菜进度: {_counter.CutCount}/{ServerCounter.CUTS_NEEDED} ({progress:P0})");

            // 广播切菜进度给所有客户端
            BroadcastAll(NetworkMessage.Create_CounterProgressSync(_counter.CounterId, progress));

            // 切菜完成
            if (_counter.CutCount >= ServerCounter.CUTS_NEEDED)
            {
                // 物品转换：切好的食材在原索引基础上变化
                // 实际项目中应根据配方表确定 output，这里简化为：完成时 itemIndex 不变
                // （客户端 CuttingCounter.ApplyServerCutProgress 会在 progress>=1 时自行做物品转换）
                _counter.CutCount = 0;
                _counter.IsCutting = false;
                _counter.Progress = 0f;
                Console.WriteLine($"[GameServer] CuttingCounter {_counter.CounterId} 切菜完成！");
            }
        }

        private void HandleRegisterPlateSO(ServerClient _sender, byte[] _data) 
        {
            var (plateId, soid) = NetworkMessage.Parse_RegisterPlateSO(_data);

            if (!plates.TryGetValue(plateId, out ServerPlate plate))
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
        #endregion

        /// <summary>
        /// 销毁服务器登记的盘子并广播（供订单交付等场景统一调用）。
        /// </summary>
        /// <param name="_plateId">盘子 ID</param>
        public void DestroyPlate(int _plateId)
        {
            if (!plates.Remove(_plateId))
            {
                Console.WriteLine($"[GameServer] DestroyPlate: 盘子 {_plateId} 不存在");
                return;
            }

            Console.WriteLine($"[GameServer] 盘子 {_plateId} 已销毁（交付消费）");
            BroadcastAll(NetworkMessage.Create_PlateDestroy(_plateId));
        }

        /// <summary>
        /// 取服务器权威的盘子配料数据（供订单校验使用）。
        /// </summary>
        /// <param name="_plateId">盘子 ID</param>
        /// <param name="_ingredients">输出：盘子当前配料索引列表</param>
        /// <returns>盘子是否存在</returns>
        public bool TryGetPlateIngredients(int _plateId, out List<int> _ingredients)
        {
            if (plates.TryGetValue(_plateId, out ServerPlate plate))
            {
                _ingredients = plate.CurrentIngredients;
                return true;
            }

            _ingredients = null;
            return false;
        }

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