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

        /// <summary>关卡索引下限（从 1 开始，0 保留为无效值）</summary>
        public const int MIN_LEVEL_INDEX = 1;

        /// <summary>关卡索引上限（与客户端 LevelSceneCatalog 的登记表保持一致）</summary>
        public const int MAX_LEVEL_INDEX = 2;

        /// <summary>
        /// 把客户端上报的关卡索引收敛到合法范围：非法值一律回落到关卡 1，
        /// 避免客户端传入越界索引导致全房加载不存在的场景。
        /// </summary>
        public static int ClampLevelIndex(int _levelIndex)
        {
            return _levelIndex >= MIN_LEVEL_INDEX && _levelIndex <= MAX_LEVEL_INDEX
                ? _levelIndex
                : MIN_LEVEL_INDEX;
        }

        /// <summary>房主离开后，选最小 PlayerId 作为新房主（全客户端结果一致、可复算）</summary>
        public static int PickNewOwnerPlayerId(IEnumerable<int> _remainingPlayerIds)
        {
            if (_remainingPlayerIds == null || !_remainingPlayerIds.Any())
                throw new ArgumentNullException(nameof(_remainingPlayerIds));
            return _remainingPlayerIds.Min();
        }
    }
}
