using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server.Counter
{
    public class PlateCounter : BaseCounter
    {
        public int PlateKitchenObjectSOId { get; private set; }

        public int PlateCount { get; set; }

        /// <summary>盘子生成计时器（秒）</summary>
        public float PlateSpawnTimer { get; set; }

        public int PLATE_MAX = 4;
        public float PLATE_SPAWN_INTERVAL = 4f;

        /// <summary>当前位于该操作台上的盘子ID列表（LIFO 取用）</summary>
        public List<int> Plates { get; } = new List<int>();

        public PlateCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        /// <summary>客户端上报盘子 SO 索引（C2S_RegisterPlateSpawnId）。</summary>
        public void SetPlateSOId(int _soId) => PlateKitchenObjectSOId = _soId;

        // ────────────────────────── Tick ──────────────────────────
        /// <summary>按间隔生成新盘子（登记到房间 plates 并广播 PlateSpawned）。</summary>
        public override void Update(float _deltaTime)
        {
            PlateSpawnTimer += _deltaTime;
            if (PlateSpawnTimer < PLATE_SPAWN_INTERVAL) return;
            PlateSpawnTimer = 0f;
            if (PlateCount >= PLATE_MAX) return;

            ServerPlate newPlate = Room.SpawnPlateOnCounter(PlateKitchenObjectSOId, CounterId);
            if (newPlate == null) return;

            Plates.Add(newPlate.PlateId);
            PlateCount++;

            Console.WriteLine($"[GameServer] PlateCounter {CounterId} 生成盘子，当前数量={PlateCount}");
            Room.BroadcastAll(NetworkMessage.Create_PlateSpawned(CounterId, newPlate.PlateId));
        }

        // ─────────────────────── 主交互 ───────────────────────
        /// <summary>玩家空手时取走一张盘子（LIFO）。</summary>
        public override void OnPlayerInteract(ServerPlayer _player)
        {
            if (_player.HeldItemIndex != -1) return;   // 玩家手里有东西
            if (Plates.Count == 0) return;             // 台上没盘子

            int plateId = Plates[Plates.Count - 1];
            Plates.RemoveAt(Plates.Count - 1);
            PlateCount--;

            if (!Room.TryGetPlate(plateId, out var plate)) return;

            plate.OwnerType = 0;
            plate.OwnerId = _player.PlayerId;
            _player.HeldItemIndex = PlateKitchenObjectSOId;

            Room.BroadcastAll(NetworkMessage.Create_PlateOwnerSync(plate));   // 实体归玩家
            Room.BroadcastAll(NetworkMessage.Create_RemotePlate(CounterId, plateId));
        }
    }
}