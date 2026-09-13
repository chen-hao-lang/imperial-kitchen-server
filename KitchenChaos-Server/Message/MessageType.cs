public enum MessageType : byte
{
    // ══════════════ 连接与玩家生命周期 ══════════════
    // 上传（客户端 → 服务端）
    C2S_Connect = 100,            // 请求连接（附带玩家名字）
    C2S_Disconnect = 105,         // 主动断开连接

    // 解析（服务端 → 客户端）
    Connect = 0,                  // 分配玩家ID（连接成功后）
    PlayerLeft = 2,               // 有玩家离开

    // ══════════════ 玩家同步（移动 / 交互 / 手持）══════════════
    // 上传（客户端 → 服务端）
    C2S_PlayerMove = 101,         // 玩家移动（位置 + Y轴旋转）
    C2S_PlayerInteract = 102,     // 玩家按交互键（E键）——意图：counterId(int)
    C2S_PlayerInteractAlternate = 103, // 玩家按副交互键（空格）——意图：counterId(int)

    // 解析（服务端 → 客户端）
    PlayerPosition = 3,           // 广播玩家位置
    InteractionSync = 5,          // 交互同步：一次原子操作，同时携带操作台和玩家的物品状态

    // 操作台
    // 上传（客户端 → 服务端）
    C2S_RegisterContainerItem = 119,
    C2S_RegisterCuttingRecipes = 110,// 切菜配方上报
    C2S_RegisterBoilingRecipes = 111,// 煮菜配方上报
    C2S_RegisterPlateSpawnId = 112,// 注册操作台生成盘子类型
    C2S_RegisterStreamingRecipes = 114,
    C2S_CounterRegister = 108,    // 客户端上报操作台类型映射（连接后首次发送）

    // 解析（服务端 → 客户端）
    CounterProgressSync = 14,     // 操作台进度同步（切菜/煎/煮的进度条）
    CounterAddIngredient = 15,    // 操作台添加配料广播
    RemotePlate = 17,             // 盘子与操作台的同步（放到操作台上，或从操作台拿走）
    CounterStateSync = 25,// 操作台状态同步

    // ══════════════ 盘子 ══════════════
    // 上传（客户端 → 服务端）
    C2S_RegisterPlateSO = 113,    // 上报盘子 SO 索引（FullSync 恢复用）
    C2S_RegisterPlateValidIngredients = 129, // 上报盘子可装配料列表（内容注册，服务器裁决）——原 125 与 C2S_RequestRoomList 冲突，已改 129

    // 解析（服务端 → 客户端）
    PlateSpawned = 18,            // 通知所有客户端新盘子出现
    PlateOwnerSync = 19,          // 盘子持有者变更广播（plateId + ownerType + ownerId）
    PlateAddIngredient = 20,      // 盘子配料变化广播（plateId + ingredientIndex）
    PlateDestroy = 21,            // 盘子销毁（plateId）
    PlateRemoveIngredient = 22,   // 盘子配料移除广播（plateId + ingredientIndex）

    // ══════════════ 游戏流程（状态 / 计时 / 订单 / 分数）══════════════
    // 上传（客户端 → 服务端）
    C2S_DeliveryRequest = 117,    // 客户端提交交付请求（plateId）——服务器权威校验订单
    C2S_RegisterRecipes = 118,    // 客户端上报配方表（连接后注册）——内容由客户端提供，裁决由服务端执行
    C2S_ScucessEnterGameScene = 128, // 成功进入游戏场景（服务器在此启动操作台更新循环）
    C2S_RequestBackRoom = 130,// 向服务器请求返回房间列表

    // 解析（服务端 → 客户端）
    GameStartCD = 12,             // 开局倒计时
    GameingTimerSync = 7,         // 同步倒计时
    GameOver = 24,                // 游戏结束——原 18 与 PlateSpawned 冲突，已改 24
    BackRoom = 131,
    RecipeSpawned = 8,            // 新订单出现
    RecipeCompleted = 9,          // 订单完成
    RecipeFailed = 10,            // 交付失败
    ScoreUpdate = 11,             // 分数更新
    FullSync = 13,                // 新玩家加入时的全量同步

    // ══════════════ 聊天 ══════════════
    // 上传（客户端 → 服务端）
    C2S_SendMessage = 116,        // 客户端发送聊天消息（playerId + message）
    // 解析（服务端 → 客户端）
    SendMessage = 23,             // 聊天消息广播（playerId + message）

    #region Room
    // 上传（客户端 → 服务端）
    C2S_CreateRoom = 120,         // 创建房间（附带最大玩家数）
    C2S_JoinRoom = 121,           // 加入房间（附带房间号）
    C2S_LeaveRoom = 122,          // 离开房间
    C2S_DestroyRoom = 123,        // 房主解散房间
    C2S_RequestRoomList = 125,    // 请求房间列表
    C2S_PlayerReady = 126,        // 玩家准备就绪
    C2S_RequestEnterGameScene = 127,          // 开始游戏

    // 解析（服务端 → 客户端）
    RoomCreated = 30,             // 房间创建成功，附带房间号
    RoomJoined = 31,              // 成功加入房间
    RoomJoinFailed = 32,          // 加入房间失败（附带原因码）
    RoomPlayerListSync = 33,      // 房间玩家列表更新
    RoomDestroyed = 34,           // 房间被解散
    RequestRoomList = 35,         // 房间列表
    PlayerReady = 36,             // 玩家准备
    StartGame = 37,               // 开始游戏
    RoomOwnerChanged = 38,          // 房主变更 
    ResumeGameResult = 40,          // 断线重连（D7）已暂时禁用

    C2S_HeartBeat = 138,
    C2S_ResumeGame = 132// 断线重连（D7）已暂时禁用
    #endregion
}