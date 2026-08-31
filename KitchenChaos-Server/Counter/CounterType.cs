namespace KitchenChaos_Server.Counter
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
}
