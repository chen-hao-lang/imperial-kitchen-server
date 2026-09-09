using KitchenChaos_Server.Manager;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    public class DeliveryCounter : BaseCounter
    {
        public DeliveryCounter(ServerRoom _room, int _counterId, CounterType _type) : base(_room, _counterId, _type) { }

        public override void OnPlayerInteract(ServerPlayer _player)
        {
            if (Room.ServerPlateManager.TryGetPlateByOwner(_player.PlayerId, out var plate))
            {
                HandleDeliveryRequest(_player, plate.PlateId);
            }
        }

        /// <summary>
        /// 处理客户端提交的交付请求：转交给服务器权威的订单管理器校验并广播结果。
        /// </summary>
        public void HandleDeliveryRequest(ServerPlayer _player, int _plateId)
        {
            Room.RoomDeliveryManager?.HandleDeliveryRequest(_plateId);   // 校验 + 计分 + DestroyPlate + 广播

            // 交付后玩家手里为空（盘子已被销毁，若有普通手持物也一并清掉）
            if (_player.HeldItemIndex != -1 || Room.ServerPlateManager.TryGetPlateByOwner(_player.PlayerId, out _))
            {
                _player.HeldItemIndex = -1;
                Room.BroadcastAll(NetworkMessage.Create_InteractionSync(-1, -1, _player.PlayerId, -1));
            }
        }
    }
}