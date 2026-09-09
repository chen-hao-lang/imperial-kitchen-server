using KitchenChaos_Server.KitchenObject;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server.Counter
{
    public class PlateCounter : BaseCounter
    {
        /// <summary>
        /// 该操作台能生成的盘子类型
        /// </summary>
        public int PlateKitchenObjectSOId { get; private set; }

        /// <summary>
        /// 当前位于该操作台上的盘子数量（LIFO 取用）
        /// </summary>
        public int plateCouunt;

        /// <summary>
        /// 盘子生成计时器（秒）
        /// </summary>
        public float plateSpawnTimer;

        /// <summary>
        /// 该操作台上盘子的最大数量
        /// </summary>
        public int PLATE_MAX = 4;

        /// <summary>
        /// 盘子生成间隔（秒）
        /// </summary>
        public float PLATE_SPAWN_INTERVAL = 4f;

        /// <summary>当前位于该操作台上的盘子ID列表（LIFO 取用）</summary>
        public List<int> Plates { get; } = new List<int>();

        public PlateCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) 
        {
            plateCouunt = 0;
            plateSpawnTimer = 0;
        }

        /// <summary>客户端上报盘子 SO 索引（C2S_RegisterPlateSpawnId）。</summary>
        public void SetPlateSOId(int _soId) => PlateKitchenObjectSOId = _soId;
 
        /// <summary>
        /// 按间隔生成新盘子（登记到房间 plates 并广播 PlateSpawned）
        /// </summary>
        public override void Update(float _deltaTime)
        {
            plateSpawnTimer += _deltaTime;
            if (plateSpawnTimer < PLATE_SPAWN_INTERVAL) return;
            plateSpawnTimer = 0f;
            if (plateCouunt >= PLATE_MAX) return;

            // 生成新盘子
            ServerPlate newPlate = Room.ServerPlateManager.SpawnPlateOnCounter(PlateKitchenObjectSOId, CounterId);
            if (newPlate == null) return;

            Plates.Add(newPlate.PlateId);
            plateCouunt++;

            Console.WriteLine($"[GameServer] PlateCounter {CounterId} 生成盘子，当前数量={plateCouunt}");
            Room.BroadcastAll(NetworkMessage.Create_PlateSpawned(CounterId, newPlate.PlateId));
        }

        /// <summary>
        /// 玩家空手时取走一张盘子（LIFO）
        /// </summary>
        public override void OnPlayerInteract(ServerPlayer _player)
        {
            if (_player.HeldItemIndex != -1) return;   // 玩家手里有东西
            if (Plates.Count == 0) return;             // 台上没盘子

            int plateId = Plates[Plates.Count - 1];
            Plates.RemoveAt(Plates.Count - 1);
            plateCouunt--;

            if (!Room.ServerPlateManager.TryGetPlate(plateId, out var plate)) return;

            // 修改盘子归属信息
            plate.OwnerType = 0;
            plate.OwnerId = _player.PlayerId;
            _player.HeldItemIndex = PlateKitchenObjectSOId;

            // 广播
            Room.BroadcastAll(NetworkMessage.Create_PlateOwnerSync(plate));   // 实体归玩家
            Room.BroadcastAll(NetworkMessage.Create_RemotePlate(CounterId, plateId));
        }
    }
}