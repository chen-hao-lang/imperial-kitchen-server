using KitchenChaos_Server.KitchenObject;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server.Manager
{
    public class ServerPlateManager
    {
        private readonly ServerRoom room;

        private int nextPlateId;


        /// <summary>
        /// Key: PlateId, Value: ServerPlate
        /// </summary>
        public Dictionary<int, ServerPlate> Plates { get; private set; }

        public ServerPlateManager(ServerRoom _room)
        {
            room = _room;
            Plates = new Dictionary<int, ServerPlate>();
        }

        public ServerPlate SpawnPlateOnCounter(int _plateSOId, int _ownerCounterId)
        {
            ServerPlate newPlate = new ServerPlate(nextPlateId, _plateSOId, _ownerCounterId)
            {
                OwnerType = 1,                     // 操作台
            };
            nextPlateId++;
            Plates.Add(newPlate.PlateId, newPlate);
            return newPlate;
        }

        /// <summary>
        /// 销毁服务器登记的盘子并广播（供订单交付等场景统一调用）。
        /// </summary>
        /// <param name="_plateId">盘子 ID</param>
        public void DestroyPlate(int _plateId)
        {
            if (!Plates.Remove(_plateId))
            {
                Console.WriteLine($"[GameServer] DestroyPlate: 盘子 {_plateId} 不存在");
                return;
            }

            Console.WriteLine($"[GameServer] 盘子 {_plateId} 已销毁（交付消费）");
            room.BroadcastAll(NetworkMessage.Create_PlateDestroy(_plateId));
        }

        /// <summary>
        /// 取服务器权威的盘子配料数据（供订单校验使用）。
        /// </summary>
        /// <param name="_plateId">盘子 ID</param>
        /// <param name="_ingredients">输出：盘子当前配料索引列表</param>
        /// <returns>盘子是否有配料</returns>
        public bool TryGetPlateIngredients(int _plateId, out List<int> _ingredients)
        {
            if (Plates.TryGetValue(_plateId, out ServerPlate plate))
            {
                _ingredients = plate.CurrentIngredients;
                return true;
            }

            _ingredients = null;
            return false;
        }

        public bool TryGetPlate(int _plateId, out ServerPlate _plate)
        {
            return Plates.TryGetValue(_plateId, out _plate);
        }

        /// <summary>
        /// 查看玩家手里的盘子
        /// </summary>
        /// <param name="_playerId"></param>
        /// <param name="_plate"></param>
        /// <returns></returns>
        public bool TryGetPlateByOwner(int _playerId, out ServerPlate _plate)
        {
            foreach (var plate in Plates.Values)
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
            foreach (var plate in Plates.Values)
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

        public void Clear()
        {
            Plates.Clear();
            nextPlateId = 0;
        }
    }
}