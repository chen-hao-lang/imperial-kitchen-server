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
        public ServerClient client { get; }

        public ServerPlayer(int _playerId, ServerClient _client)
        {
            PlayerId = _playerId;
            client = _client;
        }
    }
}