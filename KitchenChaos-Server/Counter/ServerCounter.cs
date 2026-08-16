using KitchenChaos_Server.Counter;
using System;
using System.Collections.Generic;

namespace KitchenChaos_Server
{
    /// <summary>
    /// 操作台类型枚举（与客户端 CounterType 保持一一对应）
    /// </summary>
    public enum CounterType : byte
    {
        ClearCounter = 1,
        ContainerCounter = 2,
        CuttingCounter = 3,
        BoilingCounter = 4,
        PlateCounter = 5,
        TrashCounter = 6,
        DeliveryCounter = 7,
        StreamingCounter = 8,
    }

    /// <summary>
    /// 服务端维护的单个台子状态。
    /// 根据 CounterType 不同，使用不同的字段。
    /// </summary>
    public class ServerCounter : BaseCounter
    {
        

        /// <summary>通用进度 0f~1f（切菜/煎/煮等进度类操作台使用）</summary>
        public float Progress { get; set; }


        // ===== CuttingCounter 专用 =====
        /// <summary>是否正在被切</summary>
        public bool IsCutting { get; set; }
        /// <summary>当前切了几刀</summary>
        public int CutCount { get; set; }
        /// <summary>总共需要切几刀才能完成</summary>
        public const int CUTS_NEEDED = 5;

        // ===== BoilingCounter 专用 =====
        /// <summary>是否正在煮沸</summary>
        public bool IsBoiling { get; set; }
        /// <summary>煮沸计时器（秒）</summary>
        public float BoilingTimer { get; set; }
        /// <summary>该配方需要的煮沸总时长（秒），由客户端上报</summary>
        public float MaxBoilingTimer { get; set; }

        /// <summary>
        /// 煮沸进度归一化值
        /// </summary>
        public float BoilingProgressNormalized =>
            MaxBoilingTimer > 0f ? Math.Min(BoilingTimer / MaxBoilingTimer, 1f) : 0f;

        public ServerCounter(int _counterId, CounterType _type = CounterType.ClearCounter) : base(_counterId, _type)
        { 

        }
        //{
        //    CounterId = _counterId;
        //    Type = _type;
        //}

        /// <summary>
        /// 切菜进度归一化值
        /// </summary>
        public float CuttingProgressNormalized =>
            CUTS_NEEDED > 0 ? (float)CutCount / CUTS_NEEDED : 0f;
    }
}