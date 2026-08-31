namespace KitchenChaos_Server.Counter
{
    public class BaseCounter
    {
        public int CounterId { get; protected set; }
        public CounterType Type { get; set; }
        /// <summary>台子上的物品索引，-1=空</summary>
        public int ItemIndex { get; set; } = -1;

        public int ContainerSOId { get; set; }

        public BaseCounter(int _counterId,CounterType _type)
        {
            CounterId = _counterId;
            Type = _type;
        }
    }
}