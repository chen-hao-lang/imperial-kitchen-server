using KitchenChaos_Server.Counter;
using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
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
        public ERoomState roomState { get; private set; }

        public List<ServerClient> clients;

        public Dictionary<int,ServerPlayer> players;

        private bool countersInitialized = false;
        public Dictionary<int,BaseCounter> counters;

        private int nextPlateId;
        public Dictionary<int,ServerPlate> plates;

        public bool IsFull => players.Count >= MaxPlayer;

        private ServerGameManager gameManager;
        public ServerDeliveryManager DeliveryManager { get; private set; }

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
            gameManager = new ServerGameManager(this, gameCts.Token);
            DeliveryManager = new ServerDeliveryManager(this);

            counterCts = new CancellationTokenSource();
            _ = CounterUpdateLoopAsync(counterCts.Token);
        }

        public int AddClient(ServerClient _client)
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

                gameManager.OnPlayerConnected(_client.Player.PlayerId);

                return seatId;
            }
            else
            {
                Console.WriteLine($"[ServerRoom] 房间：{RoomId} 已经 连接了客户端{_client}");
            }

            return -1;
        }

        public bool RemoveClient(ServerClient _client)
        {
            if (clients.Contains(_client))
            {
                clients.Remove(_client);

                players.Remove(_client.Player.PlayerId);
                gameManager.OnPlayerLefted(_client.Player.PlayerId);
                _client.CurrentRoom = null;

                //TODO:房主转移

                //广播给其他客户端进行更新
                List<int> currentPlayerIds = new List<int>();
                List<string> currentPlayersName = new List<string>();
                foreach (var player in players.Values)
                {
                    currentPlayerIds.Add(player.PlayerId);
                    currentPlayersName.Add(player.Name);
                }
                BroadcastExcept(_client, NetworkMessage.Create_RoomPlayerListSync(currentPlayerIds, currentPlayersName));

                return clients.Count == 0;
            }
            else
            {
                Console.WriteLine($"[ServerRoom] 房间：{RoomId} 不存在客户端{_client}");
                return false;
            }
        }

        public void SetRoomState(ERoomState _state)
        {
            roomState = _state;
        }

        public void BroadcastAll(byte[] _data)
        {
            foreach (var client in clients)
            {
                client.Send(_data);
            }
        }

        public void BroadcastExcept(ServerClient _client, byte[] _data)
        {
            foreach (var client in clients)
            {
                if (client != _client)
                {
                    client.Send(_data);
                }
            }
        }

        public void Stop()
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
                    UpdateCounters(deltaTime);
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
            if (gameManager.State != EGameState.GamePlaying)
            {
                return;
            }

            // 服务器权威订单生成（仅游戏进行中）
            DeliveryManager?.Update(_deltaTime);

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
                platesList[j] = (kv.PlateId, kv.OwnerType, kv.OwnerId, kv.SoIndex, kv.Ingredients);
                j++;
            }

            // 收集等待中的订单（服务器权威，中途加入的玩家恢复订单状态）
            int[] recipeIndexes = DeliveryManager?.GetWaitingRecipeIndexes() ?? new int[0];

            _targetClient.Send(NetworkMessage.Create_FullSync(
                playersList.ToArray(),
                countersList,
                platesList,
                recipeIndexes));
        }

        /// <summary>
        /// 处理来自客户端的消息
        /// </summary>
        public void HandleGameMessage(ServerClient _sender, MessageType _type, byte[] _date)
        {
            switch (_type)
            {
                case MessageType.C2S_PlayerReady:
                    HandlePlayerReady(_sender, _date);
                    break;
                case MessageType.C2S_PlayerMove:
                    HandlePlayerMove(_sender, _date);
                    break;
                case MessageType.C2S_CounterRegister:
                    HandleCounterRegister(_sender, _date);
                    break;
                case MessageType.C2S_CounterAddIngredient:
                    HandleCounterAddIngredient(_sender, _date);
                    break;
                case MessageType.C2S_CounterStartMark:
                    HandleCounterStartMark(_sender, _date);
                    break;
                case MessageType.C2S_PlayerInteract:
                    HandlePlayerInteract(_sender, _date);
                    break;
                case MessageType.C2S_PlayerInteractAlternate:
                    HandleInteractAlternate(_sender, _date);
                    break;
                case MessageType.C2S_PlateAddIngredient:
                    HandlePlateAddIngredient(_sender, _date);
                    break;
                case MessageType.C2S_PlateRemoveIngredient:
                    HandlePlateRemoveIngredient(_sender, _date);
                    break;
                case MessageType.C2S_PlateOwnerSync:
                    HandlePlateOwnerSync(_sender, _date);
                    break;
                case MessageType.C2S_PlateDestroy:
                    HandlePlateDestroy(_sender, _date);
                    break;
                case MessageType.C2S_RemotePlate:
                    HandleRemotePlate(_sender, _date);
                    break;
                case MessageType.C2S_RegisterPlateSO:
                    HandleRegisterPlateSO(_sender, _date);
                    break;
                case MessageType.C2S_DeliveryRequest:
                    HandleDeliveryRequest(_sender, _date);
                    break;
                case MessageType.C2S_RegisterRecipes:
                    HandleRegisterRecipes(_sender, _date);
                    break;
                case MessageType.C2S_SendMessage:
                    HandleSendMessage(_sender, _date);
                    break;
                default:
                    // 未处理的消息类型
                    break;
            }
        }

        #region 消息处理
        /// <summary>
        /// 同步玩家主备
        /// </summary>
        private void HandlePlayerReady(ServerClient _sender, byte[] _data)
        {
            var (id, isReady) = NetworkMessage.Parse_PlayerReade(_data);
            foreach (var player in players.Values)
            {
                if (player.PlayerId == id)
                {
                    player.IsReady = isReady;
                    // 广播玩家准备状态给所有客户端
                    BroadcastAll(NetworkMessage.Create_PlayerReady(id, isReady ? 1 : 0));
                    Console.WriteLine($"[GameServer] 玩家{id} 准备状态: {isReady}");
                    if (isReady)
                    {
                        gameManager.OnPlayerReady(_sender.Player.PlayerId);
                    }
                    return;
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
        /// 同步操作台配料添加
        /// </summary>
        private void HandleCounterAddIngredient(ServerClient _sender, byte[] _data) 
        {
            var (counterId, ingredientId) = NetworkMessage.Parse_CounterAddIngredient(_data);

            if (!counters.TryGetValue(counterId, out BaseCounter counter))
            {
                Console.WriteLine($"未知操作台: {counterId}");
                return;
            }

            if (counter.Type == CounterType.PlateCounter && counter is PlateCounter plateCounter)
            {
                plateCounter.PlateCount--;
                //BroadcastExcept(_sender,NetworkMessage.Create_PlateCounterSync(counter.CounterId, counter.PlateCount));
                return;
            }
            else
            {
                counter.ItemIndex = ingredientId;
            }

            // ingredientId >= 0: 玩家把东西放到操作台上 → 玩家手中变空
            // ingredientId == -1: 操作台被清空（如盘子装走） → 玩家手中物品不变
            if (ingredientId >= 0)
            {
                _sender.Player.HeldItemIndex = -1;
            }

            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 交互: 台子{counterId} → 物品{ingredientId}, 手中 → 物品{_sender.Player.HeldItemIndex}");

            // ★ 合并为一条原子消息：同时广播操作台和玩家状态
            BroadcastExcept(_sender, NetworkMessage.Create_CounterAddIngredient(
                counterId,
                ingredientId));
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
            // 玩家主交互：上报当前手持物品索引
            var (playerId, playerItem) = NetworkMessage.Parse_PlayerInteract(_data);
            if (players.TryGetValue(playerId, out var interactPlayer))
            {
                interactPlayer.HeldItemIndex = playerItem;
            }
            BroadcastExcept(_sender, NetworkMessage.Create_PlayerInteract(playerId, playerItem));
            Console.WriteLine($"[GameServer] 玩家{playerId} 主交互: 物品{playerItem}");
        }

        /// <summary>
        /// 处理副交互（空格键）——切菜等进度操作台
        /// </summary>
        private void HandleInteractAlternate(ServerClient _sender, byte[] _data)
        {
            int counterId = NetworkMessage.Parse_InteractAlternate(_data);

            if (!counters.TryGetValue(counterId, out var counter))
                return;

            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 副交互: 台子{counterId} 类型={counter.Type}");

            if (counter.Type == CounterType.CuttingCounter && counter is ServerCounter serverCounter)
            {
                HandleCuttingInteract(serverCounter);
            }
        }

        /// <summary>
        /// 处理切菜操作台的副交互
        /// </summary>
        private void HandleCuttingInteract(ServerCounter _counter)
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

        /// <summary>
        /// 处理客户端上报的盘子添加配料（plateId + ingredientIndex）。
        /// </summary>
        private void HandlePlateAddIngredient(ServerClient _sender, byte[] _data) 
        {
            var (plateId, ingredientIndex) = NetworkMessage.Parse_PlateAddIngredient(_data);

            if (!plates.TryGetValue(plateId, out var plate))
            {
                Console.WriteLine($"[GameServer] HandlePlateAddIngredient: 找不到盘子 {plateId}");
                return;
            }

            if (plate.Ingredients.Contains(ingredientIndex))
            {
                Console.WriteLine($"[GameServer] HandlePlateAddIngredient: 盘子 {plateId} 已有配料 {ingredientIndex}");
                return;
            }

            plate.Ingredients.Add(ingredientIndex);
            Console.WriteLine($"[GameServer] 盘子 {plateId} 添加配料 {ingredientIndex}，当前: [{string.Join(",", plate.Ingredients)}]");

            BroadcastAll(NetworkMessage.Create_PlateAddIngredient(plateId, ingredientIndex));
        }

        /// <summary>
        /// 处理客户端上报的盘子移除配料（plateId + ingredientIndex）。
        /// </summary>
        private void HandlePlateRemoveIngredient(ServerClient _sender, byte[] _data)
        {
            var (plateId, ingredientIndex) = NetworkMessage.Parse_PlateRemoveIngredient(_data);

            if (!plates.TryGetValue(plateId, out var plate))
            {
                Console.WriteLine($"[GameServer] HandlePlateRemoveIngredient: 找不到盘子 {plateId}");
                return;
            }

            if (!plate.Ingredients.Remove(ingredientIndex))
            {
                Console.WriteLine($"[GameServer] HandlePlateRemoveIngredient: 盘子 {plateId} 没有配料 {ingredientIndex}");
                return;
            }

            Console.WriteLine($"[GameServer] 盘子 {plateId} 移除配料 {ingredientIndex}，当前: [{string.Join(",", plate.Ingredients)}]");

            BroadcastAll(NetworkMessage.Create_PlateRemoveIngredient(plateId, ingredientIndex));
        }

        /// <summary>
        /// 处理客户端上报的盘子持有者变更。
        /// </summary>
        private void HandlePlateOwnerSync(ServerClient _sender, byte[] _data) 
        {
            var (plateId, ownerType, ownerId, ingredients) = NetworkMessage.Parse_PlateOwnerSync(_data);

            if (!plates.TryGetValue(plateId, out var plate))
            {
                Console.WriteLine($"[GameServer] HandlePlateOwnerSync: 找不到盘子 {plateId}");
                return;
            }

            plate.OwnerType = ownerType;
            plate.OwnerId = ownerId;
            plate.SetIngredients(ingredients);
            Console.WriteLine($"[GameServer] 盘子 {plateId} 变更持有者 → ({ownerType},{ownerId})");

            BroadcastExcept(_sender, NetworkMessage.Create_PlateOwnerSync(plateId, ownerType, ownerId, ingredients));
        }

        private void HandlePlateDestroy(ServerClient _sender, byte[] _data) 
        {
            int plateId = NetworkMessage.Parse_PlateDestroy(_data);

            if (!plates.Remove(plateId))
            {
                Console.WriteLine($"[GameServer] HandlePlateDestroy: 盘子 {plateId} 不存在");
                return;
            }

            Console.WriteLine($"[GameServer] 盘子 {plateId} 已销毁");
            BroadcastAll(NetworkMessage.Create_PlateDestroy(plateId));
        }


        private void HandleRemotePlate(ServerClient _sender, byte[] _data)
        {
            var (counterId, plateId) = NetworkMessage.Parse_RemotePlate(_data);

            if (!counters.TryGetValue(counterId, out BaseCounter counter))
                return;

            if (counter is PlateCounter plateCounter)
            {
                if (plates.ContainsKey(plateId))
                {
                    // 从操作台的盘子ID列表中移除该盘子（不依赖取用顺序）
                    if (plateCounter.Plates.Remove(plateId))
                    {
                        plateCounter.PlateCount--;

                        BroadcastExcept(_sender, NetworkMessage.Create_RemotePlate(counterId, plateId));

                        Console.WriteLine($"[GameServer] 移除盘子成功 盘子:{plateId} 操作台:{counterId}");
                    }
                    else
                    {
                        Console.WriteLine($"[GameServer] 移除盘子出错 盘子:{plateId} 不在操作台:{counterId} 的列表中");
                    }
                }
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
        /// 处理客户端提交的交付请求：转交给服务器权威的订单管理器校验并广播结果。
        /// </summary>
        private void HandleDeliveryRequest(ServerClient _sender, byte[] _data) 
        {
            int plateId = NetworkMessage.Parse_DeliveryRequest(_data);
            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 提交交付请求: 盘子 {plateId}");
            DeliveryManager?.HandleDeliveryRequest(plateId);
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
                _ingredients = plate.Ingredients;
                return true;
            }

            _ingredients = null;
            return false;
        }

        public List<int> GetRoomsPlayer()
        {
            List<int> list = new List<int>();

            foreach (var player in players.Values)
            {
                list.Add(player.PlayerId);
            }
            return list;
        }

        public List<string> GetRoomsPlayerName()
        {
            List<string> list = new List<string>();

            foreach (var player in players.Values)
            {
                list.Add(player.Name);
            }
            return list;
        }
    }
}