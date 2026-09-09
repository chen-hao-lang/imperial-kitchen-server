using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    public class TrashCounter : BaseCounter
    {
        public TrashCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        public override void OnPlayerInteract(ServerPlayer _player)
        {
            if (_player.HeldItemIndex == -1) return;

            // 手持盘子不能丢（只有交付台能销毁盘子）
            if (Room.ServerPlateManager.TryGetPlateByOwner(_player.PlayerId, out _)) return;

            _player.HeldItemIndex = -1;
            Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId, -1, _player.PlayerId, -1));
        }
    }
}
