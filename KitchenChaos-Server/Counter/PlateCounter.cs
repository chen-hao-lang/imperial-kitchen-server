using System.Collections.Generic;

namespace KitchenChaos_Server.Counter
{
    public class PlateCounter : BaseCounter
    {
        public int PlateKitchenObjectSOId { get; }
        public int PlateCount { get; set; }
        /// <summary>盘子生成计时器（秒）</summary>
        public float PlateSpawnTimer { get; set; }
        /// <summary>盘子数量上限</summary>
        public int PLATE_MAX = 4;
        /// <summary>盘子生成间隔（秒）</summary>
        public float PLATE_SPAWN_INTERVAL = 4f;
        /// <summary>当前位于该操作台上的盘子ID列表（LIFO 取用）</summary>
        public List<int> Plates { get; set; } = new List<int>();

        public PlateCounter(int _counterId,CounterType _type) : base(_counterId,_type)
        {
            
        }
    }
}