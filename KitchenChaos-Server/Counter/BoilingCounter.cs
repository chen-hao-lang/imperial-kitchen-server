using KitchenChaos_Server.Message;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace KitchenChaos_Server.Counter
{
    public class BoilingCounter : BaseCounter
    {
        public List<BoilingRecipeSO> Recipes { get; set; }
        public List<int> Ingredients { get; }

        /// <summary>
        /// 煮沸计数器状态：0=空闲, 1=准备中, 2=煮沸中, 3=完成
        /// </summary>
        public byte BoilintCounterState { get; set; }
        /// <summary>
        /// 是否正在煮沸
        /// </summary>
        public bool IsBoiling { get; set; }
        
        /// <summary>
        /// 煮沸计时器（秒）
        /// </summary>
        public float BoilingTimer { get; set; }
        
        /// <summary>
        /// 当前激活的配方需要的煮沸总时长（秒），由客户端上报
        /// </summary>
        public float MaxBoilingTimer { get; set; }

        /// <summary>
        /// 当前激活的，正在制作的配方
        /// </summary>
        public int ActiveRecipeIndex { get; set; }

        /// <summary>
        /// 煮沸进度归一化值
        /// </summary>
        public float BoilingProgressNormalized =>
            MaxBoilingTimer > 0f ? Math.Min(BoilingTimer / MaxBoilingTimer, 1f) : 0f;
        
        public BoilingCounter(int _counterId,CounterType _counterType) : base(_counterId, _counterType)
        {
            Ingredients = new List<int>();
        }

        public bool TryAddIngredients(int _ingredientsId)
        {
            if (!Ingredients.Contains(_ingredientsId) && Recipes != null)// 先查看现有的配料中有没有这个配料
            {
                foreach (var recipe in Recipes)
                {
                    if (recipe.inputs.Contains(_ingredientsId))
                    {
                        Ingredients.Add(_ingredientsId);
                        BoilintCounterState = 1;
                        return true;
                    }
                }
            }

            return false;   
        }

        public bool TryStartBoiling()
        {
            if (Recipes == null || IsBoiling) return false;

            for (int i = 0; i < Recipes.Count; i++)
            {
                if (IsMatch(Recipes[i].inputs))
                {
                    ActiveRecipeIndex = i;
                    IsBoiling = true;
                    BoilingTimer = 0f;
                    MaxBoilingTimer = Recipes[i].maxBoilingTimer;
                    BoilintCounterState = 2;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 查看当前操作台上的配料是否满足某个配方的要求
        /// </summary>
        /// <param name="recipeInputs"></param>
        /// <param name="currentIngredients"></param>
        /// <returns></returns>
        private bool IsMatch(List<int> recipeInputs)
        {
            if (recipeInputs == null || recipeInputs.Count == 0) return false;

            // 复制当前配料列表，用于模拟消耗
            List<int> temp = new List<int>(Ingredients);

            // 遍历配方所需的所有配料
            foreach (var input in recipeInputs)
            {
                if (temp.Contains(input))
                {
                    temp.Remove(input);
                }
                else
                {
                    return false;
                }
            }

            return temp.Count == 0;
        }
    }
}
