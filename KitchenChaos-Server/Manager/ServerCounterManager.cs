using KitchenChaos_Server.Counter;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
using System;
using System.Collections.Generic;
using System.IO;

namespace KitchenChaos_Server.Manager
{
    public class ServerCounterManager
    {
        private readonly ServerRoom room;
        private readonly Dictionary<int, BaseCounter> counters = new Dictionary<int, BaseCounter>();
        private bool initialized;

        public int Count => counters.Count;

        /// <summary>操作台类型映射是否已初始化（房主是否已上报台子）</summary>
        public bool IsInitialized => initialized;

        public ServerCounterManager(ServerRoom _room)
        {
            room = _room;
        }

        /// <summary>房间销毁时清理。调用方需持有房间锁。</summary>
        public void Clear()
        {
            counters.Clear();
            initialized = false;
        }

        /// <summary>处理客户端上报的操作台类型映射（第一个上报者优先）。调用方需持有房间锁。</summary>
        public void HandleCounterRegister(byte[] _data)
        {
            if (initialized)
            {
                Console.WriteLine("[GameServer] 操作台已初始化，忽略重复上报");
                return;
            }

            var mapping = NetworkMessage.Parse_CounterRegister(_data);
            counters.Clear();

            foreach (var (counterId, typeByte) in mapping)
            {
                var type = (CounterType)typeByte;
                counters.Add(counterId, CounterFactory.Create(room, counterId, type));
                Console.WriteLine($"[GameServer] 注册操作台: ID={counterId}, 类型={type}");
            }

            initialized = true;
            Console.WriteLine($"[GameServer] 操作台类型映射初始化完成，共 {counters.Count} 个");
        }

        /// <summary>处理各类操作台注册消息（容器物品/切菜配方/煮菜配方/盘子SO/蒸制配方）。调用方需持有房间锁。</summary>
        public void HandleRegisterMessage(MessageType _type, byte[] _data)
        {
            switch (_type)
            {
                case MessageType.C2S_RegisterContainerItem:
                    {
                        var (ctId, soIndex) = NetworkMessage.Parse_RegisterContainerItem(_data);
                        if (counters.TryGetValue(ctId, out var ct) && ct is ContainerCounter container)
                        {
                            container.SetSpawnItem(soIndex);
                            Console.WriteLine($"[GameServer] 容器{ctId} 注册固定物品: {soIndex}");
                        }
                        return;
                    }
                case MessageType.C2S_RegisterCuttingRecipes:
                    {
                        var (counterId, recipes) = NetworkMessage.Parse_RegisterCuttingRecipes(_data);
                        if (counters.TryGetValue(counterId, out var c) && c is CuttingCounter cutting)
                            cutting.SetRecipes(recipes);
                        return;
                    }
                case MessageType.C2S_RegisterBoilingRecipes:
                    {
                        var (counterId, recipes) = NetworkMessage.Parse_RegisterBoilingRecipes(_data);
                        if (counters.TryGetValue(counterId, out var c) && c is BoilingCounter boiling)
                            boiling.SetRecipes(recipes);
                        return;
                    }
                case MessageType.C2S_RegisterPlateSpawnId:
                    {
                        var (counterId, plateSOId) = NetworkMessage.Parse_RegisterPlateSpawnId(_data);
                        if (counters.TryGetValue(counterId, out var c) && c is PlateCounter plate)
                            plate.SetPlateSOId(plateSOId);
                        return;
                    }
                case MessageType.C2S_RegisterStreamingRecipes:
                    {
                        var (counterId, recipes) = NetworkMessage.Parse_RegisterStreamingRecipes(_data);
                        if (counters.TryGetValue(counterId, out var c) && c is StreamingCounter streaming)
                            streaming.SetRecipes(recipes);
                        return;
                    }
            }
        }

        /// <summary>主交互分发：交给台子自身实现。调用方需持有房间锁。</summary>
        public void HandlePlayerInteract(ServerPlayer _player, int _counterId)
        {
            if (!counters.TryGetValue(_counterId, out var counter)) return;
            counter.OnPlayerInteract(_player);
        }

        /// <summary>副交互分发：交给台子自身实现。调用方需持有房间锁。</summary>
        public void HandlePlayerInteractAlternate(ServerPlayer _player, int _counterId)
        {
            if (!counters.TryGetValue(_counterId, out var counter)) return;
            Console.WriteLine($"[GameServer] 玩家{_player.PlayerId} 副交互: 台子{_counterId} 类型={counter.Type}");
            counter.OnPlayerInteractAlternate(_player);
        }

        /// <summary>逐 tick 推进所有操作台（含 Delivery 以外、需要持续计时的台）。调用方需持有房间锁。</summary>
        public void UpdateAll(float _deltaTime)
        {
            foreach (var counter in counters.Values)
            {
                counter.Update(_deltaTime);
            }
        }

        /// <summary>FullSync 用的操作台快照：(counterId, itemIndex)[]</summary>
        public (int counterId, int itemIndex)[] GetCounterSnapshot()
        {
            var list = new (int, int)[counters.Count];
            int i = 0;
            foreach (var c in counters.Values)
            {
                list[i++] = (c.CounterId, c.ItemIndex);
            }
            return list;
        }

        public void WriteSnapshot(BinaryWriter _writer)
        {
            _writer.Write(counters.Count);
            foreach (var counter in counters.Values) counter.WriteSnapshot(_writer);
        }
    }
}
