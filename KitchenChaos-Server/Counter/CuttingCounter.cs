using KitchenChaos_Server.Counter;
using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server
{
    /// <summary>
    /// 服务端维护的单个台子状态。
    /// 根据 CounterType 不同，使用不同的字段。
    /// </summary>
    public class CuttingCounter : BaseCounter
    {
        /// <summary>
        /// 当前切了几刀
        /// </summary>
        private int cutCount;

        /// <summary>
        /// 总共需要切几刀
        /// </summary>
        private int cutNeed;

        public List<CuttingRecipeSO> Recipes { get; set; }

        /// <summary>
        /// 切完没有
        /// </summary>
        public bool IsCutEnd { get; set; }

        /// <summary>
        /// 切菜进度归一化值
        /// </summary>
        public float CuttingProgressNormalized =>
            cutNeed > 0 ? (float)cutCount / cutNeed : 0f;

        public CuttingCounter(int _counterId, CounterType _type = CounterType.ClearCounter) : base(_counterId, _type)
        { 
            
        }

        public bool CanCat(int _input) => TryGetCuttingRecipe(_input, out _);

        public float Cut(int _inputId)
        {
            if (!TryGetCuttingRecipe(_inputId, out var so)) return CuttingProgressNormalized;

            IsCutEnd = false;
            cutNeed = so.cuttingPrograssMax;
            cutCount++;

            float progress = Math.Min((float)cutCount / cutNeed, 1f);

            if (cutCount >= cutNeed)
            {
                IsCutEnd = true;
                ItemIndex = so.output;
                cutCount = 0;
                cutNeed = 0;
            }
            return progress;
        }

        private bool TryGetCuttingRecipe(int _input, out CuttingRecipeSO _recipe)
        {
            if (Recipes != null)
            {
                foreach (var value in Recipes)
                {
                    if (value.input == _input)
                    {
                        _recipe = value;
                        return true;
                    }
                }
            }

            _recipe = default;
            return false;
        }
    }
}