using System.Collections.Generic;
using System.ComponentModel;

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
        public List<int> CurrentIngredients { get; private set; } = new List<int>();

        /// <summary>盘子当前持有者类型：0=玩家, 1=操作台</summary>
        public byte OwnerType { get; set; }

        /// <summary>盘子当前持有者ID（玩家ID 或 操作台网络ID）</summary>
        public int OwnerId { get; set; }

        /// <summary>盘子的 KitchenObjectSO 索引（用于远程客户端生成）</summary>
        public int SoIndex { get; private set; } = -1;
        public HashSet<int> ValidIngredientIndexes { get; private set; } = new HashSet<int>();

        public ServerPlate(int _plateId)
        {
            PlateId = _plateId;
        }

        public void SetPlateSO(int _plateSOIndex)
        {
            SoIndex = _plateSOIndex;
        }

        public bool TryAddIngredient(int _ingredients)
        {
            if (ValidIngredientIndexes != null && ValidIngredientIndexes.Count > 0)
            {
                if (!ValidIngredientIndexes.Contains(_ingredients)) return false;
            }

            if (!CurrentIngredients.Contains(_ingredients))
            {
                CurrentIngredients.Add(_ingredients);
                return true;
            }

            return false;
        }

        public bool TryRemoveIngredient(int _ingredients)
        {
            if (CurrentIngredients.Contains(_ingredients))
            {
                CurrentIngredients.Remove(_ingredients);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判断当前盘子的拥有者是不是玩家
        /// </summary>
        /// <param name="_playerId"></param>
        /// <returns></returns>
        public bool IsHeldByPlayer(int _playerId)
        {
            return OwnerType == 0 && OwnerId == _playerId;
        }

        /// <summary>
        /// 判断盘子当前的拥有者是不是操作台
        /// </summary>
        /// <param name="_counterId"></param>
        /// <returns></returns>
        public bool IsOnCounter(int _counterId)
        {
            return OwnerType == 1 && OwnerId == _counterId;
        }

        public void SetIngredients(List<int> _ingredients)
        {
            CurrentIngredients = _ingredients;
        }
    }
}
