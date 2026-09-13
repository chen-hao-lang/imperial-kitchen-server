using System;
using System.Collections.Generic;

namespace KitchenChaos_Server.Room
{
    public class ReconnectManager
    {
        private readonly double graceSeconds;
        private readonly Dictionary<int, ReconnectSeat> seats = new Dictionary<int, ReconnectSeat>();
        public int Count => seats.Count;

        public ReconnectManager(double _graceSeconds = 30)
        {
            graceSeconds = _graceSeconds;
        }

        /// <summary>
        /// 保留断开连接的玩家的席位
        /// </summary>
        /// <param name="_player">断线玩家</param>
        public void HoldSeat(ServerPlayer _player)
        {
            if (_player == null) return;

            seats.Remove(_player.PlayerId);

            // 添加玩家短线时的信息
            seats[_player.PlayerId] = new ReconnectSeat
            {
                Player = _player,
                ExpireUtc = DateTime.UtcNow.AddSeconds(graceSeconds)
            };
        }

        /// <summary>
        /// 认领席位
        /// </summary>
        /// <param name="_oldPlayerId"></param>
        /// <param name="_player"></param>
        /// <returns></returns>
        public bool TryTakeSeat(int _oldPlayerId, out ServerPlayer _player)
        {
            _player = null;
            if (!seats.TryGetValue(_oldPlayerId, out var seat)) return false;

            if (DateTime.UtcNow > seat.ExpireUtc)
            {
                seats.Remove(_oldPlayerId);
                return false;
            }

            seats.Remove(_oldPlayerId);
            _player = seat.Player;
            return true;
        }

        /// <summary>
        /// 释放指定玩家的席位
        /// </summary>
        /// <param name="_playerId">指定的玩家</param>
        public void ReleaseSeat(int _playerId) => seats.Remove(_playerId);

        /// <summary>
        /// 清理过期席位
        /// </summary>
        public void Prune()
        {
            if (seats.Count == 0) return;

            List<int> expired = null;
            foreach (var kv in seats)
            {
                if (DateTime.UtcNow > kv.Value.ExpireUtc)
                {
                    (expired ??= new List<int>()).Add(kv.Key);
                }
            }
            if (expired == null) return;

            foreach (int id in expired) seats.Remove(id);
        }

        public void Clear() => seats.Clear();

        private sealed class ReconnectSeat
        {
            public ServerPlayer Player;
            public DateTime ExpireUtc;
        }
    }
}
