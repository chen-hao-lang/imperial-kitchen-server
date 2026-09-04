using KitchenChaos_Server.Counter;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;
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

        public List<CuttingRecipeSO> Recipes { get; private set; }

        /// <summary>
        /// 切完没有
        /// </summary>
        public bool IsCutEnd { get; private set; }

        /// <summary>
        /// 切菜进度归一化值
        /// </summary>
        public float CuttingProgressNormalized =>
            cutNeed > 0 ? (float)cutCount / cutNeed : 0f;

        public CuttingCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        public void SetRecipes(List<CuttingRecipeSO> _recipes) => Recipes = _recipes;

        public override void OnPlayerInteract(ServerPlayer _player)
        {
            CounterInteractionHelper.PlaceOrTakeItem(this,_player);
        }

        public override void OnPlayerInteractAlternate(ServerPlayer _player)
        {
            if (ItemIndex < 0) return;
            if (!CanCat(ItemIndex)) return;

            float progress = Cut(ItemIndex);   // 先取进度再广播（原来完成那刀广播 0）

            Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, progress));
            Console.WriteLine($"[GameServer] CuttingCounter {CounterId} 切菜进度: {progress}");

            if (IsCutEnd)
            {
                Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, ItemIndex, -1, -1));
                Room.BroadcastAll(NetworkMessage.Create_CounterProgressSync(CounterId, 1.0f));
            }
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