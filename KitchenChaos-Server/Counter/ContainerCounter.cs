using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    public class ContainerCounter : BaseCounter
    {
        public int ContainerSOId { get; private set; } = -1;
        public ContainerCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        public void SetSpawnItem(int _soIndex) => ContainerSOId = _soIndex;

        public override void OnPlayerInteract(ServerPlayer _player)
        {
            if (_player.HeldItemIndex != -1) return;

            if (ContainerSOId < 0)
                return;

            _player.HeldItemIndex = ContainerSOId;

            // 广播交互结果
            Room.BroadcastAll(NetworkMessage.Create_InteractionSync(CounterId,ItemIndex,_player.PlayerId,_player.HeldItemIndex));
        }
    }
}
