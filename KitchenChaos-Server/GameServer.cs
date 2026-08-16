using KitchenChaos_Server.Counter;
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

        /// <summary>
        /// 所有玩家
        /// </summary>
        private readonly Dictionary<int, ServerPlayer> players = new Dictionary<int, ServerPlayer>();

        /// <summary>
        /// 场景中的所有操作台
        /// </summary>
        private readonly Dictionary<int, BaseCounter> counters = new Dictionary<int, BaseCounter>();

        /// <summary>
        /// 所有服务器管理的盘子（plateId → ServerPlate）
        /// </summary>
        private readonly Dictionary<int, ServerPlate> plates = new Dictionary<int, ServerPlate>();
        private int nextPlateId = 0;

        /// <summary>
        /// 是否已从客户端收到操作台类型映射
        /// </summary>
        private bool countersInitialized;

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
                case MessageType.C2S_Connect:
                    break;
                case MessageType.C2S_PlayerReady:
                    var (id, isReady) = NetworkMessage.Parse_PlayerReade(_body);
                    if (players.TryGetValue(id, out ServerPlayer player))
                    {
                        player.IsReady = isReady;
                        // 广播玩家准备状态给所有客户端
                        BroadcastAll(NetworkMessage.Create_PlayerReady(id, isReady ? 1 : 0));
                        Console.WriteLine($"[GameServer] 玩家{id} 准备状态: {isReady}");
                        if (isReady)
                        {
                            gameManager.OnPlayerReady(_sender.Player.PlayerId);
                        }
                    }
                    break;
                case MessageType.C2S_PlayerMove:
                    var (x, y, z, rotY) = NetworkMessage.Parse_PlayerMove(_body);
                    _sender.Player.LastPosX = x;
                    _sender.Player.LastPosY = y;
                    _sender.Player.LastPosZ = z;
                    _sender.Player.LastRotY = rotY;
                    BroadcastExcept(_sender, NetworkMessage.Create_PlayerPosition(_sender.Player.PlayerId, x, y, z, rotY));
                    break;
                case MessageType.C2S_CounterAddIngredient:
                    HandleCounterAddIngredient(_sender, _body);
                    break;

                case MessageType.C2S_PlayerInteractAlternate:
                    HandlePlayerInteractAlternate(_sender, _body);
                    break;

                case MessageType.C2S_PlayerInteract:
                    // 玩家主交互：上报当前手持物品索引
                    var (playerId, playerItem) = NetworkMessage.Parse_PlayerInteract(_body);
                    if (players.TryGetValue(playerId, out var interactPlayer))
                    {
                        interactPlayer.HeldItemIndex = playerItem;
                    }
                    BroadcastExcept(_sender, NetworkMessage.Create_PlayerInteract(playerId, playerItem));
                    Console.WriteLine($"[GameServer] 玩家{playerId} 主交互: 物品{playerItem}");
                    break;
                case MessageType.C2S_Disconnect:
                    _sender.Disconnect();
                    break;
                case MessageType.C2S_CounterRegister:
                    HandleCounterRegister(_body);
                    break;
                case MessageType.C2S_CounterStartMark:
                    HandleCounterStartMark(_body);
                    break;
                // 盘子相关消息
                case MessageType.C2S_PlateAddIngredient:
                    HandlePlateAddIngredient(_sender, _body);
                    break;
                case MessageType.C2S_PlateRemoveIngredient:
                    HandlePlateRemoveIngredient(_sender, _body);
                    break;
                case MessageType.C2S_PlateOwnerSync:
                    HandlePlateOwnerSync(_sender, _body);
                    break;
                case MessageType.C2S_PlateDestroy:
                    HandlePlateDestroy(_body);
                    break;
                case MessageType.C2S_RegisterPlateSO:
                    HandlePlateRegisterSO(_body);
                    break;
                case MessageType.C2S_RemotePlate:
                    HandlePlateRemote(_sender, _body);
                    break;
                case MessageType.C2S_SendMessage:
                    HandleSendMessage(_sender, _body);
                    break;
                case MessageType.C2S_DeliveryRequest:
                    HandleDeliveryRequest(_sender, _body);
                    break;
                case MessageType.C2S_RegisterRecipes:
                    HandleRegisterRecipes(_sender, _body);
                    break;
                default:
                    Console.WriteLine($"[GameServer] 未处理的消息类型: {_type}");
                    break;
            }
        }

        private void HandlePlateRemote(ServerClient _client, byte[] body)
        {
            var (counterId, plateId) = NetworkMessage.Parse_RemotePlate(body);

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

                        BroadcastExcept(_client, NetworkMessage.Create_RemotePlate(counterId, plateId));

                        Console.WriteLine($"[GameServer] 移除盘子成功 盘子:{plateId} 操作台:{counterId}");
                    }
                    else
                    {
                        Console.WriteLine($"[GameServer] 移除盘子出错 盘子:{plateId} 不在操作台:{counterId} 的列表中");
                    }
                }
            }
        }

        /// <summary>
        /// 处理聊天消息：使用服务器记录的发送者ID，转发给其他客户端。
        /// 发送者本地已显示自己的消息，因此使用 BroadcastExcept。
        /// </summary>
        private void HandleSendMessage(ServerClient _sender, byte[] _body)
        {
            var (_, message) = NetworkMessage.Parse_SendMessage(_body);
            if (string.IsNullOrEmpty(message)) return;

            int playerId = _sender.Player.PlayerId;
            Console.WriteLine($"[GameServer] 玩家{playerId} 发送聊天消息: {message}");

            // 转发给除发送者以外的所有玩家
            BroadcastExcept(_sender, NetworkMessage.Create_SendMessage(playerId, message));
        }

        private void HandlePlateRegisterSO(byte[] _body)
        {
            var (plateId, soid) = NetworkMessage.Parse_RegisterPlateSO(_body);

            if (!plates.TryGetValue(plateId, out ServerPlate plate))
            {
                Console.WriteLine($"[GameServer] 要注册SO的盘子不存在: plateId={plateId}");
                return;
            }

            plate.SetPlateSO(soid);
        }

        /// <summary>
        /// 处理客户端上报的盘子销毁（交付/丢弃）。
        /// 从 plates 字典移除并广播，保证 FullSync 只下发"在场"的盘子。
        /// </summary>
        private void HandlePlateDestroy(byte[] _body)
        {
            int plateId = NetworkMessage.Parse_PlateDestroy(_body);

            if (!plates.Remove(plateId))
            {
                Console.WriteLine($"[GameServer] HandlePlateDestroy: 盘子 {plateId} 不存在");
                return;
            }

            Console.WriteLine($"[GameServer] 盘子 {plateId} 已销毁");
            BroadcastAll(NetworkMessage.Create_PlateDestroy(plateId));
        }

        /// <summary>
        /// 处理客户端提交的交付请求：转交给服务器权威的订单管理器校验并广播结果。
        /// </summary>
        private void HandleDeliveryRequest(ServerClient _sender, byte[] _body)
        {
            int plateId = NetworkMessage.Parse_DeliveryRequest(_body);
            Console.WriteLine($"[GameServer] 玩家{_sender.Player.PlayerId} 提交交付请求: 盘子 {plateId}");
            deliveryManager?.HandleDeliveryRequest(plateId);
        }

        /// <summary>
        /// 处理客户端上报的配方表：服务端校验并注册（第一个上报者优先，与 C2S_CounterRegister 一致）。
        /// 内容由客户端提供，但订单的生成/校验/计分仍由服务端执行。
        /// </summary>
        private void HandleRegisterRecipes(ServerClient _sender, byte[] _body)
        {
            var recipes = NetworkMessage.Parse_RegisterRecipes(_body);
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
        /// 新一局游戏开始时重置订单与分数（由 ServerGameManager 调用）。
        /// </summary>
        public void StartNewGame()
        {
            deliveryManager?.StartNewGame();
        }

        /// <summary>
        /// 处理客户端上报的交互结果（简单操作台：ClearCounter, ContainerCounter 等）
        /// </summary>
        private void HandleCounterAddIngredient(ServerClient _sender, byte[] _body)
        {
            var (counterId, ingredientId) = NetworkMessage.Parse_CounterAddIngredient(_body);

            if (!counters.TryGetValue(counterId, out var counter))
            {
                // 第一次遇到这个台子，创建它
                Console.WriteLine($"[GameServer] 服务器中未注册该操作台：{counter.CounterId}-{counter.Type}");
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
        /// 处理副交互（空格键）——切菜等进度操作台
        /// </summary>
        private void HandlePlayerInteractAlternate(ServerClient _sender, byte[] _body)
        {
            int counterId = NetworkMessage.Parse_InteractAlternate(_body);

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
        /// 处理客户端上报的操作台状态更新（煮沸等进度类操作台启动）。
        /// 客户端在匹配到配方后调用，通知服务器开始计时。
        /// </summary>
        private void HandleCounterStartMark(byte[] _body)
        {
            var (counterId, maxBoilingTimer) = NetworkMessage.Parse_CounterStartMark(_body);

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
        /// 处理客户端上报的盘子添加配料（plateId + ingredientIndex）。
        /// </summary>
        private void HandlePlateAddIngredient(ServerClient _sender, byte[] _body)
        {
            var (plateId, ingredientIndex) = NetworkMessage.Parse_PlateAddIngredient(_body);

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
        private void HandlePlateRemoveIngredient(ServerClient _sender, byte[] _body)
        {
            var (plateId, ingredientIndex) = NetworkMessage.Parse_PlateRemoveIngredient(_body);

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
        private void HandlePlateOwnerSync(ServerClient _sender, byte[] _body)
        {
            var (plateId, ownerType, ownerId, ingredients) = NetworkMessage.Parse_PlateOwnerSync(_body);

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

        private void SendFullSyncToClient(ServerClient _targetClient)
        {
            lock (clientsLock)
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
                foreach (var kv in this.counters)
                {
                    countersList[i] = (kv.Key, kv.Value.ItemIndex);
                    i++;
                }

                // 收集所有盘子
                var platesList = new (int plateId, byte ownerType, int ownerId, int soIndex, List<int> ingredients)[plates.Count];
                int j = 0;
                foreach (var kv in plates)
                {
                    platesList[j] = (kv.Key, kv.Value.OwnerType, kv.Value.OwnerId, kv.Value.SoIndex, kv.Value.Ingredients);
                    j++;
                }

                // 收集等待中的订单（服务器权威，中途加入的玩家恢复订单状态）
                int[] recipeIndexes = deliveryManager?.GetWaitingRecipeIndexes() ?? new int[0];

                _targetClient.Send(NetworkMessage.Create_FullSync(
                    playersList.ToArray(),
                    countersList,
                    platesList,
                    recipeIndexes));
            }
        }

        /// <summary>
        /// 处理客户端上报的操作台类型映射。
        /// 只在第一次收到时初始化，后续客户端上报的映射会被忽略（以第一个上报的为准）。
        /// </summary>
        private void HandleCounterRegister(byte[] _body)
        {
            if (countersInitialized)
            {
                Console.WriteLine("[GameServer] 操作台已初始化，忽略重复上报");
                return;
            }

            var mapping = NetworkMessage.Parse_CounterRegister(_body);
            counters.Clear();

            foreach (var (counterId, typeByte) in mapping)
            {
                var type = (CounterType)typeByte;
                //var counter = new ServerCounter(counterId, type);

                // PlateCounter 初始化盘子数量
                if (type == CounterType.PlateCounter)
                {
                    PlateCounter plateCounter = new PlateCounter(counterId, type);
                    plateCounter.PlateCount = 0;
                    plateCounter.PlateSpawnTimer = 0f;

                    counters[counterId] = plateCounter;
                }
                else
                {
                    var counter = new ServerCounter(counterId, type);
                    counters[counterId] = counter;
                }

                Console.WriteLine($"[GameServer] 注册操作台: ID={counterId}, 类型={type}");
            }

            countersInitialized = true;
            Console.WriteLine($"[GameServer] 操作台类型映射初始化完成，共 {counters.Count} 个");
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
            deliveryManager?.Update(_deltaTime);

            foreach (var kv in counters)
            {
                var counter = kv.Value;

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
                            if (!plates.ContainsKey(nextPlateId))
                            {
                                plates[nextPlateId] = newPlate;
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
    }
}