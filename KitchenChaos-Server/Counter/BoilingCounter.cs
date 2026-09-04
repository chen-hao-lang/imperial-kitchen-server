using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server.Counter
{
    public class BoilingCounter : BaseCounter
    {
        public List<BoilingRecipeSO> Recipes { get; private set; }
        public List<int> Ingredients { get; } = new List<int>();

        /// <summary>煮沸计数器状态：0=空闲, 1=准备中, 2=煮沸中, 3=完成</summary>
        public byte BoilintCounterState { get; set; }
        /// <summary>是否正在煮沸</summary>
        public bool IsBoiling { get; set; }
        /// <summary>煮沸计时器（秒）</summary>
        public float BoilingTimer { get; set; }
        /// <summary>当前激活配方煮沸总时长（秒），由客户端上报</summary>
        public float MaxBoilingTimer { get; set; }
        /// <summary>当前激活配方在 Recipes 中的下标</summary>
        public int ActiveRecipeIndex { get; set; } = -1;

        /// <summary>煮沸进度归一化值</summary>
        public float BoilingProgressNormalized =>
            MaxBoilingTimer > 0f ? Math.Min(BoilingTimer / MaxBoilingTimer, 1f) : 0f;

        public BoilingCounter(ServerRoom _room, int _counterId, CounterType _counterType) : base(_room, _counterId, _counterType) { }

        /// <summary>客户端上报煮沸配方（C2S_RegisterBoilingRecipes）。</summary>
        public void SetRecipes(List<BoilingRecipeSO> _recipes) => Recipes = _recipes;

        // ────────────────────────── Tick ──────────────────────────
        /// <summary>每 tick：推进煮沸计时、广播进度；完成时产出成品并广播。</summary>
        public override void Update(float _deltaTime)
        {
            // 如果当前没有在煮，则跳过（不要用 return 外的逻辑中断其他台——本方法只处理自己）
            if (!IsBoiling) return;

            BoilingTimer += _deltaTime;
            float progress = BoilingProgressNormalized;

            // 广播煮沸进度给所有客户端
            Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, progress));

            // 煮沸完成
            if (BoilingTimer >= MaxBoilingTimer)
            {
                IsBoiling = false;
                BoilingTimer = 0f;
                BoilintCounterState = 3;

                if (ActiveRecipeIndex >= 0 && Recipes != null && ActiveRecipeIndex < Recipes.Count)
                {
                    ItemIndex = Recipes[ActiveRecipeIndex].output;
                }
                Ingredients.Clear();
                ActiveRecipeIndex = -1;

                Console.WriteLine($"[GameServer] BoilingCounter {CounterId} 煮沸完成 → 物品{ItemIndex}");

                Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, 1.0f));
                Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, ItemIndex, -1, -1));
                Room.BroadcastAll(NetworkMessage.Create_CounterStateSync(CounterId, BoilintCounterState));
            }
        }

        // ─────────────────────── 主交互 ───────────────────────
        /// <summary>加料（手持/盘中）→ 满足配方即开煮；锅内有成品则装盘。</summary>
        public override void OnPlayerInteract(ServerPlayer _player)
        {
            // 锅里没东西
            if (ItemIndex == -1 && !IsBoiling)
            {
                if (Room.TryGetPlateByOwner(_player.PlayerId, out var heldPlate))   // 玩家手里有盘子
                {
                    foreach (var ingredients in new List<int>(heldPlate.CurrentIngredients))   // 副本遍历，边遍历边删
                    {
                        if (TryAddIngredients(ingredients))
                        {
                            heldPlate.TryRemoveIngredient(ingredients);
                            Room.BroadcastAll(NetworkMessage.Create_PlateRemoveIngredient(heldPlate.PlateId, ingredients));
                            Room.BroadcastAll(NetworkMessage.Create_CounterAddIngredient(CounterId, ingredients));
                        }
                    }
                }
                else if (_player.HeldItemIndex != -1)   // 玩家手里是配料
                {
                    int ingredients = _player.HeldItemIndex;
                    if (TryAddIngredients(ingredients))
                    {
                        _player.HeldItemIndex = -1;
                        Room.BroadcastAll(NetworkMessage.Create_CounterAddIngredient(CounterId, ingredients));
                    }
                }

                if (TryStartBoiling())
                {
                    Room.BroadcastAll(NetworkMessage.Create_CounterStateSync(CounterId, BoilintCounterState));
                    Console.WriteLine($"[GameServer] BoilingCounter {CounterId} 开始煮沸: 配方{ActiveRecipeIndex}, 时长={MaxBoilingTimer:F1}s");
                }

                Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, -1, _player.PlayerId, -1));
                return;
            }

            // 锅里面有成品，装盘
            if (ItemIndex != -1 && Room.TryGetPlateByOwner(_player.PlayerId, out var plateOnPlayer))
            {
                if (plateOnPlayer.TryAddIngredient(ItemIndex))
                {
                    int outIdx = ItemIndex;
                    ItemIndex = -1;
                    Ingredients.Clear();   // 清空锅里的配料
                    IsBoiling = false;
                    BoilintCounterState = 0;
                    Room.BroadcastAll(NetworkMessage.Create_PlateAddIngredient(plateOnPlayer.PlateId, outIdx));
                    Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, -1, _player.PlayerId, -1));
                    Room.BroadcastAll(NetworkMessage.Create_CounterStateSync(CounterId, BoilintCounterState));
                }
            }
        }

        // ─────────────────────── 内部规则 ───────────────────────
        public bool TryAddIngredients(int _ingredientsId)
        {
            if (!Ingredients.Contains(_ingredientsId) && Recipes != null)
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

        /// <summary>当前配料是否恰好满足某配方（数量与种类完全一致）。</summary>
        private bool IsMatch(List<int> recipeInputs)
        {
            if (recipeInputs == null || recipeInputs.Count == 0) return false;

            List<int> temp = new List<int>(Ingredients);
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