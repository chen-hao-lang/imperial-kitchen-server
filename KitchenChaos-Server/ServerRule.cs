using System;
using System.Collections.Generic;
using System.Linq;

namespace KitchenChaos_Server
{
    /// <summary>
    /// 房间级纯规则（不依赖网络/锁），集中可自检的判定。
    /// </summary>
    public static class ServerRule
    {
        /// <summary>开局最少人数</summary>
        public const int MIN_PLAYERS_TO_START = 2;

        /// <summary>房主离开后，选最小 PlayerId 作为新房主（全客户端结果一致、可复算）</summary>
        public static int PickNewOwnerPlayerId(IEnumerable<int> _remainingPlayerIds)
        {
            if (_remainingPlayerIds == null || !_remainingPlayerIds.Any())
                throw new ArgumentNullException(nameof(_remainingPlayerIds));
            return _remainingPlayerIds.Min();
        }
    }
}
