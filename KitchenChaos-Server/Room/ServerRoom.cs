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
            plates = new Dictionary<int, ServerPlate>();

            gameCts = new CancellationTokenSource();
            CounterManager = new ServerCounterManager(this);
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

        public void EnterGameScene()
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
                CounterManager.Clear();
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
                        //UpdateCounters(deltaTime);
                        CounterManager.UpdateAll(deltaTime);
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
                    HandlePlayerReady(_data);
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
        /// 处理玩家交互
        /// </summary>
        private void HandlePlayerInteract(ServerClient _sender, byte[] _data) 
        {
            var (playerId,counterId) = NetworkMessage.Parse_PlayerInteract(_data);
            CounterManager.HandlePlayerInteract(_sender.Player,counterId);
        }
        #region HandlePlayerInteract
        /// <summary>
        /// 处理客户端提交的交付请求：转交给服务器权威的订单管理器校验并广播结果。
        /// </summary>
        public void HandleDeliveryRequest(ServerPlayer _player,int _plateId)
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
        public bool TryGetPlateByOwner(int _playerId, out ServerPlate _plate)
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
        public bool TryGetPlateOnCounter(int _counterId, out ServerPlate _plate)
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
            CounterManager.HandlePlayerInteractAlternate(_sender.Player, counterId);
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
        /// <returns>盘子是否有配料</returns>
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

        public bool TryGetPlate(int _plateId, out ServerPlate _plate)
        {
            return plates.TryGetValue(_plateId, out _plate);
        }

        public ServerPlate SpawnPlateOnCounter(int _plateSOId, int _ownerCounterId)
        {
            ServerPlate newPlate = new ServerPlate(nextPlateId, _plateSOId)
            {
                OwnerType = 1,                     // 操作台
                OwnerId = _ownerCounterId,
            };
            nextPlateId++;
            plates.Add(newPlate.PlateId, newPlate);
            return newPlate;
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