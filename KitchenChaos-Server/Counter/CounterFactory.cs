using KitchenChaos_Server.Room;
using System;

namespace KitchenChaos_Server.Counter
{
    public static class CounterFactory
    {
        public static BaseCounter Create(ServerRoom _room, int _counterId, CounterType _counterType)
        {
            switch (_counterType)
            {
                case CounterType.ClearCounter:
                    return new ClearCounter(_room, _counterId, _counterType);
                case CounterType.ContainerCounter:
                    return new ContainerCounter(_room, _counterId, _counterType);
                case CounterType.CuttingCounter:
                    return new CuttingCounter(_room, _counterId, _counterType);
                case CounterType.BoilingCounter:
                    return new BoilingCounter(_room, _counterId, _counterType);
                case CounterType.PlateCounter:
                    return new PlateCounter(_room, _counterId, _counterType);
                case CounterType.TrashCounter:
                    return new TrashCounter(_room, _counterId, _counterType);
                case CounterType.DeliveryCounter:
                    return new DeliveryCounter(_room, _counterId, _counterType);
                case CounterType.StreamingCounter:
                    return new StreamingCounter(_room, _counterId, _counterType);
                default:
                    throw new ArgumentOutOfRangeException(nameof(_counterType), _counterType, "未知操作台类型");
            }
        }
    }
}
