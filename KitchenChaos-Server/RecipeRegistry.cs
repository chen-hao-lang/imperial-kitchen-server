using System;
using System.Collections.Generic;

namespace KitchenChaos_Server
{
    /// <summary>
    /// 服务器配方注册表（内容由客户端上报，裁决由服务端执行）。
    ///
    /// 设计说明：
    /// - 客户端连接后通过 C2S_RegisterRecipes 上报配方表（每个配方 = 配料索引列表），
    ///   服务端校验后注册；"第一个上报者优先"（与 C2S_CounterRegister 一致）。
    /// - 上报的配方顺序必须与客户端 RecipeListSO.asset 的 recipeSOList 保持一致，
    ///   这样 RecipeSpawned/RecipeCompleted 下发的 recipeIndex 才能在客户端正确解析。
    /// - 配料索引使用 KitchenObjectRegistry 索引空间；允许 -1（客户端引用注册表外原料），
    ///   此类配方在 Matches() 中永远不匹配（实际不可交付），仅用于保持索引对齐。
    /// - 内容虽由客户端提供，但订单的生成/校验/计分仍由服务端执行，状态层仍为服务端权威。
    /// </summary>
    public static class RecipeRegistry
    {
        /// <summary>一条配方的定义</summary>
        public sealed class RecipeDefinition
        {
            /// <summary>配方名称（仅供日志）</summary>
            public readonly string Name;

            /// <summary>配方所需配料（KitchenObjectRegistry 索引，顺序无关）</summary>
            public readonly int[] IngredientIndexes;

            public RecipeDefinition(string _name, int[] _ingredientIndexes)
            {
                Name = _name;
                IngredientIndexes = _ingredientIndexes;
            }
        }

        private static readonly object StateLock = new object();

        /// <summary>已注册的配方表（内容来自客户端上报）</summary>
        private static RecipeDefinition[] recipes = new RecipeDefinition[0];

        /// <summary>是否已完成注册（第一个上报者优先）</summary>
        private static bool registered;

        /// <summary>防御性校验上限：配方总数</summary>
        public const int MAX_RECIPES = 32;

        /// <summary>防御性校验上限：单配方配料数</summary>
        public const int MAX_INGREDIENTS_PER_RECIPE = 6;

        private static readonly Random Random = new Random();

        /// <summary>是否已注册配方表（未注册时订单逻辑应跳过）</summary>
        public static bool IsRegistered
        {
            get { lock (StateLock) { return registered; } }
        }

        /// <summary>配方总数（线程安全）</summary>
        public static int Count
        {
            get { lock (StateLock) { return recipes.Length; } }
        }

        /// <summary>
        /// 注册客户端上报的配方表。
        /// 校验规则（防御性，防篡改客户端塞入非法内容）：
        /// - 配方总数 1..MAX_RECIPES；
        /// - 每条配方配料数 1..MAX_INGREDIENTS_PER_RECIPE；
        /// - 每个配料索引在 [-1, KitchenObjectRegistry.Count) 内（-1 = 注册表外原料，允许但不可匹配）。
        /// 第一个上报者优先：已注册后再次调用返回 false。
        /// </summary>
        /// <param name="_recipes">客户端上报的配方表：每个元素 = 配料索引列表</param>
        /// <returns>是否注册成功（已注册过返回 false）</returns>
        public static bool RegisterRecipes(List<int[]> _recipes)
        {
            if (_recipes == null || _recipes.Count == 0) return false;
            if (_recipes.Count > MAX_RECIPES) return false;

            // 先做完整校验，全部合法才提交
            var validated = new RecipeDefinition[_recipes.Count];
            for (int i = 0; i < _recipes.Count; i++)
            {
                int[] ingredients = _recipes[i];
                if (ingredients == null || ingredients.Length == 0 || ingredients.Length > MAX_INGREDIENTS_PER_RECIPE)
                {
                    return false;
                }

                foreach (int index in ingredients)
                {
                    // 允许 -1（注册表外原料），拒绝其他非法值
                    if (index < -1 || index >= KitchenObjectRegistry.Count)
                    {
                        return false;
                    }
                }

                validated[i] = new RecipeDefinition($"Recipe{i}", ingredients);
            }

            lock (StateLock)
            {
                if (registered) return false;

                recipes = validated;
                registered = true;
            }

            Console.WriteLine($"[RecipeRegistry] 已注册 {recipes.Length} 条配方（内容由客户端上报）");
            return true;
        }

        /// <summary>按索引获取配方，越界返回 null（线程安全）</summary>
        public static RecipeDefinition Get(int _index)
        {
            lock (StateLock)
            {
                if (_index < 0 || _index >= recipes.Length) return null;
                return recipes[_index];
            }
        }

        /// <summary>按索引获取配方名（仅供日志）</summary>
        public static string GetName(int _index)
        {
            var recipe = Get(_index);
            return recipe == null ? "UnKnown" : recipe.Name;
        }

        /// <summary>随机选一个配方索引（生成订单用）；未注册时返回 -1</summary>
        public static int RandomRecipeIndex()
        {
            lock (StateLock)
            {
                if (recipes.Length == 0) return -1;
                return Random.Next(0, recipes.Length);
            }
        }

        /// <summary>
        /// 校验盘子的配料是否与指定配方匹配。
        /// 规则：数量一致，且配方的每个原料都能在盘子中找到（顺序无关）。
        /// 配方包含注册表外原料（索引 &lt; 0）时永远返回 false。
        /// </summary>
        /// <param name="_recipeIndex">配方索引</param>
        /// <param name="_plateIngredients">盘子上的配料（KitchenObjectRegistry 索引）</param>
        public static bool Matches(int _recipeIndex, List<int> _plateIngredients)
        {
            var recipe = Get(_recipeIndex);
            if (recipe == null) return false;

            if (recipe.IngredientIndexes.Length != _plateIngredients.Count) return false;

            foreach (int required in recipe.IngredientIndexes)
            {
                if (required < 0) return false; // 引用了注册表外原料，无法匹配
                if (!_plateIngredients.Contains(required)) return false;
            }

            return true;
        }
    }
}

