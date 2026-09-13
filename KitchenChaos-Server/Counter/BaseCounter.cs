using KitchenChaos_Server.Room;
using System.IO;

namespace KitchenChaos_Server.Counter
{
    public abstract class BaseCounter
    {
        protected readonly ServerRoom Room;
        public int CounterId { get; protected set; }
        public CounterType Type { get; protected set; }

        /// <summary>台子上的物品索引，-1=空</summary>
        public int ItemIndex { get; set; } = -1;

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

        /// <summary>
        /// 写入当前操作台的快照
        /// </summary>
        /// <param name="_writer"></param>
        public virtual void WriteSnapshot(BinaryWriter _writer)
        {
            _writer.Write(CounterId);
            _writer.Write((byte)Type);
            _writer.Write(ItemIndex);
            _writer.Write((byte)0);   // state
            _writer.Write(0f);        // progress
            _writer.Write(0);         // ingredientCount
        }
    }
}