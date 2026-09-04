using KitchenChaos_Server.Room;

namespace KitchenChaos_Server.Counter
{
    public abstract class BaseCounter
    {
        protected readonly ServerRoom Room;
        public int CounterId { get; protected set; }
        public CounterType Type { get; protected set; }

        /// <summary>台子上的物品索引，-1=空</summary>
        public int ItemIndex { get; set; } = -1;

        public int ContainerSOId { get; set; }

        public BaseCounter(ServerRoom _room,int _counterId,CounterType _type)
        {
            Room = _room;   
            CounterId = _counterId;
            Type = _type;
        }

        public ServerRoom GetRoom()
        {
            return Room;
        }

        public virtual void Update(float _deltaTime) { }

        public virtual void OnPlayerInteract(ServerPlayer _player) { }

        public virtual void OnPlayerInteractAlternate(ServerPlayer _player) { }
    }
}