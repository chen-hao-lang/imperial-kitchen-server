using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server.Counter
{
    public class StreamingCounter : BaseCounter
    {
        public List<StreamingRecipeSO> Recipes { get; private set; }

        /// <summary>正在蒸制的原料（KitchenObjectRegistry 全局索引），-1 = 无</summary>
        public int Ingredient { get; private set; } = -1;

        /// <summary>当前激活配方在 Recipes 中的下标（用于完成后取 output）</summary>
        public int ActiveRecipeIndex { get; private set; } = -1;

        /// <summary>蒸制计时器（秒）</summary>
        public float StreamingTimer { get; private set; }

        /// <summary>当前激活配方蒸制总时长（秒），由客户端上报</summary>
        public float MaxStreamingTimer { get; private set; }

        /// <summary>是否正在蒸制</summary>
        public bool IsSteaming { get; private set; }

        /// <summary>蒸制台状态码：0=空闲, 1=蒸制中, 3=蒸制完成（客户端显式映射）</summary>
        public byte SteamingCounterState { get; private set; }

        /// <summary>蒸制进度归一化值</summary>
        public float SteamingProgressNormalized =>
            MaxStreamingTimer > 0f ? Math.Min(StreamingTimer / MaxStreamingTimer, 1f) : 0f;

        public StreamingCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        /// <summary>客户端上报蒸制配方（C2S_RegisterStreamingRecipes）。</summary>
        public void SetRecipes(List<StreamingRecipeSO> _recipes) => Recipes = _recipes;

        // ────────────────────────── Tick ──────────────────────────
        /// <summary>每 tick：推进蒸制并广播进度；完成时产出成品并广播。</summary>
        public override void Update(float _deltaTime)
        {
            // ★ S2：没在蒸制（笼内既无原料也无成品待取）时不推进、不广播
            if (!IsSteaming) return;

            StreamingTimer += _deltaTime;
            float progress = SteamingProgressNormalized;

            // ★ S3：蒸制中每个 tick 广播进度（同时是中途加入客户端的恢复源）
            Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, progress));

            // 蒸制完成
            if (StreamingTimer >= MaxStreamingTimer)
            {
                IsSteaming = false;
                StreamingTimer = 0f;
                SteamingCounterState = 3;   // 完成

                // ★ S4：用 ActiveRecipeIndex（配方下标）取成品
                if (ActiveRecipeIndex >= 0 && Recipes != null && ActiveRecipeIndex < Recipes.Count)
                {
                    ItemIndex = Recipes[ActiveRecipeIndex].output;
                }
                Ingredient = -1;
                ActiveRecipeIndex = -1;

                Console.WriteLine($"[GameServer] StreamingCounter {CounterId} 蒸制完成 → 物品{ItemIndex}");

                Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, 1.0f));
                Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, ItemIndex, -1, -1));
                Room.BroadcastAll(NetworkMessage.Create_CounterStateSync(CounterId, SteamingCounterState));
            }
        }

        // ─────────────────────── 主交互 ───────────────────────
        /// <summary>笼空且未蒸 → 放入原料并开蒸；笼内有成品 → 装盘。</summary>
        public override void OnPlayerInteract(ServerPlayer _player)
        {
            // ── 笼内无成品、且当前没在蒸制：允许放入原料 ──
            if (ItemIndex == -1 && !IsSteaming)
            {
                if (Room.TryGetPlateByOwner(_player.PlayerId, out var heldPlate))   // 玩家手持盘子（倒入盘中生料）
                {
                    foreach (var ingredients in new List<int>(heldPlate.CurrentIngredients))   // 副本遍历
                    {
                        if (TryAddIngredients(ingredients))
                        {
                            heldPlate.TryRemoveIngredient(ingredients);
                            Room.BroadcastAll(NetworkMessage.Create_PlateRemoveIngredient(heldPlate.PlateId, ingredients));
                            Room.BroadcastAll(NetworkMessage.Create_CounterAddIngredient(CounterId, ingredients));
                        }
                    }
                }
                else if (_player.HeldItemIndex != -1)   // 玩家手持原料
                {
                    int ingredients = _player.HeldItemIndex;
                    if (TryAddIngredients(ingredients))
                    {
                        _player.HeldItemIndex = -1;
                        Room.BroadcastAll(NetworkMessage.Create_CounterAddIngredient(CounterId, ingredients));
                    }
                }

                // ★ S5：放入第一个合法原料后即开始蒸制 → 广播“蒸制中(1)”与 0 进度
                if (IsSteaming)
                {
                    Room.BroadcastAll(NetworkMessage.Create_CounterStateSync(CounterId, SteamingCounterState)); // = 1
                    Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, 0f));
                }

                // 让所有客户端清空“放原料玩家”的手持（原料为笼内内部状态，不生成台面实体）
                Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, -1, _player.PlayerId, -1));
                return;
            }

            // ── 笼内有成品：装盘 ──
            if (ItemIndex != -1 && Room.TryGetPlateByOwner(_player.PlayerId, out var plateOnPlayer))
            {
                if (plateOnPlayer.TryAddIngredient(ItemIndex))
                {
                    int outIdx = ItemIndex;
                    ItemIndex = -1;
                    Ingredient = -1;              // 清笼
                    ActiveRecipeIndex = -1;
                    IsSteaming = false;
                    StreamingTimer = 0f;
                    MaxStreamingTimer = 0f;
                    SteamingCounterState = 0;     // 回到空闲
                    Room.BroadcastAll(NetworkMessage.Create_PlateAddIngredient(plateOnPlayer.PlateId, outIdx));
                    Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, ItemIndex, _player.PlayerId, -1));
                    Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, 0f));
                    Room.BroadcastAll(NetworkMessage.Create_CounterStateSync(CounterId, SteamingCounterState));
                }
            }
        }

        // ─────────────────────── 内部规则 ───────────────────────
        /// <summary>原料入笼：匹配配方后记录配方下标、重置计时并进入蒸制。</summary>
        public bool TryAddIngredients(int _ingredientsId)
        {
            if (IsSteaming || _ingredientsId < 0 || Recipes == null) return false;

            for (int i = 0; i < Recipes.Count; i++)
            {
                if (Recipes[i].input == _ingredientsId)
                {
                    Ingredient = _ingredientsId;
                    ActiveRecipeIndex = i;
                    MaxStreamingTimer = Recipes[i].streamingMaxTimer;
                    StreamingTimer = 0f;
                    IsSteaming = true;
                    SteamingCounterState = 1;
                    return true;
                }
            }
            return false;
        }
    }
}