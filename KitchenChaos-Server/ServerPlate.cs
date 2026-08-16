using System.Collections.Generic;

namespace KitchenChaos_Server
{
    /// <summary>
    /// 服务端盘子实体。盘子是独立对象，拥有唯一ID和配料列表，
    /// 不依附于玩家或操作台。
    /// </summary>
    public class ServerPlate
    {
        /// <summary>服务器分配的全局唯一盘子ID</summary>
        public int PlateId { get; }

        /// <summary>盘子上的配料索引列表</summary>
        public List<int> Ingredients { get; private set; } = new List<int>();

        /// <summary>盘子当前持有者类型：0=玩家, 1=操作台</summary>
        public byte OwnerType { get; set; }

        /// <summary>盘子当前持有者ID（玩家ID 或 操作台网络ID）</summary>
        public int OwnerId { get; set; }

        /// <summary>盘子的 KitchenObjectSO 索引（用于远程客户端生成）</summary>
        public int SoIndex { get; private set; } = -1;

        public ServerPlate(int _plateId)
        {
            PlateId = _plateId;
        }

        public void SetPlateSO(int _plateSOIndex)
        {
            SoIndex = _plateSOIndex;
        }

        public void AddIngredients(int _ingredients)
        {
            if (!Ingredients.Contains(_ingredients))
            {
                Ingredients.Add(_ingredients);
            }
        }

        public void SetIngredients(List<int> _ingredients)
        {
            Ingredients = _ingredients;
        }
    }
}
