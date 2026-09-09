using System.Collections.Generic;
using System.Security.Policy;

namespace KitchenChaos_Server
{
    public class ServerPlayer
    {
        public int PlayerId { get; }
        public int SeatId { get; set; }
        public string Name { get; }
        public float LastPosX { get; set; }
        public float LastPosY { get; set; }
        public float LastPosZ { get; set; }
        public float LastRotY { get; set; }

        /// <summary>玩家的手持物品索引</summary>
        public int HeldItemIndex { get; set; } = -1;

        public bool IsReady { get; set; }
        public ServerClient client { get; private set; }

        public ServerPlayer(int _playerId, ServerClient _client)
        {
            PlayerId = _playerId;
            client = _client;
        }

        // 断线重连（D7）已暂时禁用
        ///// <summary>重连认领时把玩家对象重新绑定到新连接（D7）</summary>
        //public void AttachClient(ServerClient _client) => client = _client;
    }
}