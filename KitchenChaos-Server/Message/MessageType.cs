namespace KitchenChaos_Server.Message
{
    public enum MessageType : byte
    {
        // ========== 服务端 → 客户端 ==========
        Connect = 0,   // 分配玩家ID（连接成功后）
        PlayerJoined = 1,   // 有新玩家加入
        PlayerLeft = 2,   // 有玩家离开
        PlayerPosition = 3,   // 广播玩家位置
                              // 4 已废弃（原 PlayerHeldKitchenObjUpdate），合并到 InteractionSync
        InteractionSync = 5,   // 交互同步：一次原子操作，同时携带操作台和玩家的物品状态
        GameStateChange = 6,   // 游戏状态变化
        TimerSync = 7,   // 同步倒计时
        RecipeSpawned = 8,   // 新订单出现
        RecipeCompleted = 9,   // 订单完成
        RecipeFailed = 10,  // 交付失败
        ScoreUpdate = 11,  // 分数更新
        GameStartCD = 12,  // 开局倒计时
        FullSync = 13,  // 新玩家加入时的全量同步
        CounterProgressSync = 14, // 操作台进度同步（切菜/煎/煮的进度条）
        CounterAddIngredient = 15, // 操作台添加配料（BoilingCounter）
        PlayerInteract = 16, // 玩家交互
        RemotePlate = 17,// 盘子与操作台的同步（盘子放到操作台上，或从操作台拿走）

        // 盘子相关
        PlateSpawned = 18,     // 通知所有客户端新盘子出现
        PlateOwnerSync = 19,     // 盘子持有者变更（plateId + ownerType + ownerId）
        PlateAddIngredient = 20, // 盘子配料变化广播（plateId + ingredientIndex）
        PlateDestroy = 21,      // 盘子销毁（plateId
        PlateRemoveIngredient = 22, // 盘子配料移除广播（plateId + ingredientIndex）
        SendMessage = 23, // 聊天消息广播（服务端 → 客户端，playerId + message）

        PlayerReady = 24,
        ClientInit = 25,



        // ========== 客户端 → 服务端 ==========
        C2S_Connect = 100, // 请求连接（附带玩家名字）
        C2S_PlayerMove = 101, // 玩家移动
        C2S_PlayerInteract = 102, // 玩家按交互键（E键）
        C2S_PlayerInteractAlternate = 103, // 玩家按副交互键（空格）
        C2S_PlayerReady = 104, // 玩家准备就绪
        C2S_Disconnect = 105, // 主动断开连接
        C2S_InteractResult = 106, // 客户端上报交互结果
        C2S_CounterStartMark = 107, //台子的状态发生变化
        C2S_CounterRegister = 108, // 客户端上报操作台类型映射（连接后首次发送）
        C2S_CounterAddIngredient = 109, // BoilingCounter 放入配料时，通知服务器

        // 盘子相关
        C2S_PlateOwnerSync = 110, // 盘子持有者变更（plateId + ownerType + ownerId）
        C2S_PlateAddIngredient = 111, // 盘子添加配料（plateId + ingredientIndex）
        C2S_PlateDestroy = 112, // 盘子销毁（plateId）
        C2S_RegisterPlateSO = 113,
        C2S_RemotePlate = 114,
        C2S_PlateRemoveIngredient = 115, // 盘子移除配料（plateId + ingredientIndex）
        C2S_SendMessage = 116, // 客户端发送聊天消息（playerId + message）
        C2S_DeliveryRequest = 117, // 客户端提交交付请求（plateId）——服务器权威校验订单
        C2S_RegisterRecipes = 118, // 客户端上报配方表（连接后注册）——内容由客户端提供，裁决由服务端执行


        #region Room
        RoomCreated = 30,          // 房间创建成功，附带房间号
        RoomJoined = 31,           // 成功加入房间
        RoomJoinFailed = 32,       // 加入房间失败（附带原因码）
        RoomPlayerListSync = 33,       // 房间玩家列表更新
        RoomDestroyed = 34,        // 房间被解散

        // 房间相关 (上行: 120-129)
        C2S_CreateRoom = 120,      // 创建房间（附带最大玩家数）
        C2S_JoinRoom = 121,        // 加入房间（附带房间号）
        C2S_LeaveRoom = 122,       // 离开房间
        C2S_DestroyRoom = 123,     // 房主解散房间
        C2S_StartGame = 124,       // 房主开始游戏
        #endregion
    }
}