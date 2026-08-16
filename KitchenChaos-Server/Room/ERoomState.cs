namespace KitchenChaos_Server.Room
{
    public enum ERoomState : byte
    {
        WaitingForPlayers = 0,
        GameInPrograss = 1,
    }

    public enum ERoomJoinFailReason : byte
    {
        RoomNotFound = 0,// 房间不存在
        RoomFull = 1,// 房间已满
        GameAlreadyStarted = 2,// 游戏已经开始
        PlayerAlreadyInRoom = 3 // 玩家已在房间中
    }
}