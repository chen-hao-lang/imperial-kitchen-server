using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    internal static class CounterInteractionHelper
    {
        public static void PlaceOrTakeItem(BaseCounter _counter, ServerPlayer _player)
        {
            ServerRoom room = _counter.GetRoom();

            // 检查操作台上有没有盘子
            bool counterHasPlate = room.TryGetPlateOnCounter(_counter.CounterId, out ServerPlate plateOnCounter);

            // ── 台子上面什么都没有 ──
            if (_counter.ItemIndex == -1 && !counterHasPlate)
            {
                if (_player.HeldItemIndex == -1) return;   // 玩家手里也没东西

                if (room.TryGetPlateByOwner(_player.PlayerId, out var heldPlate))   // 玩家手里是盘子
                {
                    // 把玩家手里的盘子放到操作台上
                    heldPlate.OwnerType = 1;
                    heldPlate.OwnerId = _counter.CounterId;
                    _counter.ItemIndex = heldPlate.platerKitchenobjectSOId;
                    room.BroadcastAll(NetworkMessage.Create_PlateOwnerSync(heldPlate));
                }
                else   // 玩家手里是配料
                {
                    _counter.ItemIndex = _player.HeldItemIndex;
                }

                _player.HeldItemIndex = -1;
                room.BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, _counter.ItemIndex, _player.PlayerId, _player.HeldItemIndex));
                return;
            }

            // ── 台子上是盘子 ──
            if (counterHasPlate)
            {
                if (_player.HeldItemIndex == -1)   // 玩家空手 → 把台面盘子给玩家
                {
                    plateOnCounter.OwnerType = 0;
                    plateOnCounter.OwnerId = _player.PlayerId;
                    _player.HeldItemIndex = plateOnCounter.platerKitchenobjectSOId;
                    _counter.ItemIndex = -1;

                    room.BroadcastAll(NetworkMessage.Create_PlateOwnerSync(plateOnCounter));
                    room.BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, _counter.ItemIndex, _player.PlayerId, _player.HeldItemIndex));
                }
                else if (!room.TryGetPlateByOwner(_player.PlayerId, out _))   // 玩家持配料 → 加到台面盘子里
                {
                    int ing = _player.HeldItemIndex;
                    if (plateOnCounter.TryAddIngredient(ing))
                    {
                        room.BroadcastAll(NetworkMessage.Create_PlateAddIngredient(plateOnCounter.PlateId, ing));
                        _player.HeldItemIndex = -1;
                        room.BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, -1));
                    }
                }
                return;
            }

            // ── 台子上是普通物 ──
            if (_player.HeldItemIndex == -1)   // 玩家空手 → 拿起
            {
                _player.HeldItemIndex = _counter.ItemIndex;
                _counter.ItemIndex = -1;
                room.BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, -1, _player.PlayerId, _player.HeldItemIndex));
            }
            else if (room.TryGetPlateByOwner(_player.PlayerId, out var heldPlate2))   // 玩家持盘 → 把台面物装进盘
            {
                if (heldPlate2.TryAddIngredient(_counter.ItemIndex))
                {
                    int ing = _counter.ItemIndex;
                    _counter.ItemIndex = -1;
                    room.BroadcastAll(NetworkMessage.Create_PlateAddIngredient(heldPlate2.PlateId, ing));
                    room.BroadcastAll(NetworkMessage.Create_InteractionSync(_counter.CounterId, _counter.ItemIndex, _player.PlayerId, -1));
                }
            }
        }
    }
}
