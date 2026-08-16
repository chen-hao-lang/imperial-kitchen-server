using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KitchenChaos_Server.Message;

namespace KitchenChaos_Server
{
    public enum EGameState
    {
        WaitingForPlayers = 0,
        CDToStart = 1,
        GamePlaying = 2,
        GameOver = 3
    }

    /// <summary>
    /// 服务器端游戏流程管理器。
    /// 管理状态机、计时器，所有客户端跟随服务器的节奏。
    /// </summary>
    public class ServerGameManager
    {
        public EGameState State { get; private set; } = EGameState.WaitingForPlayers;
        public float RemainingTime { get; private set; }

        private readonly GameServer server;
        private readonly HashSet<int> readyPlayers = new HashSet<int>();
        private int totalPlayers;
        private bool isRunning;

        private const float CD_DURATION = 3f;
        private const float GAME_DURATION = 30f;

        public ServerGameManager(GameServer _server)
        {
            server = _server;
        }

        /// <summary>
        /// 有玩家连接时调用。更新总人数并开始检测
        /// </summary>
        /// <param name="_playerId">玩家Id</param>
        public void OnPlayerConnected(int _playerId)
        {
            totalPlayers++;
            Console.WriteLine($"[ServerGameManager] 等待就绪: {readyPlayers.Count}/{totalPlayers}");
        }

        /// <summary>
        /// 有玩家离开时调用
        /// </summary>
        /// <param name="_playerId">玩家id</param>
        public void OnPlayerLefted(int _playerId)
        {
            readyPlayers.Remove(_playerId);
            totalPlayers--;
        }

        /// <summary>
        /// 收到玩家就绪的消息
        /// </summary>
        /// <param name="_playerId"></param>
        public void OnPlayerReady(int _playerId)
        {
            if (State != EGameState.WaitingForPlayers) return;

            readyPlayers.Add(_playerId);
            Console.WriteLine($"[ServerGameManager] 玩家{_playerId} 就绪 ({readyPlayers.Count}/{totalPlayers})");

            // 所有人准备就绪
            if (readyPlayers.Count >= totalPlayers && totalPlayers >= 1)
            {
                _ = RunGameLoopAsync();
            }
        }

        /// <summary>
        /// 游戏主循环；倒计时—游戏中-结束
        /// </summary>
        /// <returns></returns>
        private async Task RunGameLoopAsync()
        {
            if (isRunning) return;
            isRunning = true;

            State = EGameState.CDToStart;
            server.BroadcastAll(NetworkMessage.Create_GameStateChange((byte)EGameState.CDToStart));
            Console.WriteLine("[ServerGameManager] 倒计时开始");

            for (int i = (int)CD_DURATION; i > 0; i--)
            {
                server.BroadcastAll(NetworkMessage.Create_GameStartCD(i));
                Console.WriteLine($"[ServerGameManager] 开局倒计时: {i}");
                await Task.Delay(1000);
            }

            State = EGameState.GamePlaying;
            // 新一局：重置订单与分数，并立即生成第一单
            server.StartNewGame();
            server.BroadcastAll(NetworkMessage.Create_GameStateChange((byte)EGameState.GamePlaying));
            Console.WriteLine("[ServerGameManager] 游戏开始");

            RemainingTime = GAME_DURATION;
            while (RemainingTime > 0)
            {
                server.BroadcastAll(NetworkMessage.Create_TimerSync(RemainingTime));
                await Task.Delay(1000);
                RemainingTime -= 1f;
            }

            State = EGameState.GameOver;
            server.BroadcastAll(NetworkMessage.Create_GameStateChange((byte)EGameState.GameOver));
            Console.WriteLine("[ServerGameManager] 游戏结束");
        }
    }
}