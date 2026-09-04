using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    public class DeliveryCounter : BaseCounter
    {
        public DeliveryCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        public override void OnPlayerInteract(ServerPlayer _player)
        {
            if (Room.TryGetPlateByOwner(_player.PlayerId, out var plate))
            {
                Room.HandleDeliveryRequest(_player, plate.PlateId);
            }
        }
    }
}
