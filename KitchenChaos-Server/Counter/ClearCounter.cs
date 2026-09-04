using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    public class ClearCounter : BaseCounter
    {
        public ClearCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        public override void OnPlayerInteract(ServerPlayer _player)
        {
            CounterInteractionHelper.PlaceOrTakeItem(this, _player);
        }
    }
}