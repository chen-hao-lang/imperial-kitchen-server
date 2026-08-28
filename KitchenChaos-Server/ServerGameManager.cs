using System;
using System.Threading;
using System.Threading.Tasks;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server
{
    public enum EGameState
    {
        WaitingForPlayers = 0,
        WaitingForDelivery = 1,
        CDToStart = 2,
        GamePlaying = 3,
        GameOver = 4
    }

    /// <summary>
    /// 服务器端游戏流程管理器。
    /// 管理状态机、计时器，所有客户端跟随服务器的节奏。
    /// </summary>
    public class ServerGameManager
    {
        private ServerRoom room;
        public EGameState GameStata { get; private set; } = EGameState.WaitingForPlayers;
        public float RemainingTime { get; private set; }

        private bool isRunning;

        private const float CD_DURATION = 3f;
        private const float GAME_DURATION = 30f;

        private readonly object readyLock = new object();

        private CancellationToken ct;

        public ServerGameManager(ServerRoom _room, CancellationToken _ct)
        {
            room = _room;
            ct = _ct;
        }

        /// <summary>
        /// 开始游戏计时
        /// </summary>
        public void StartGame()
        {
            // 当前第一次提交订单时，开始计时
            lock (readyLock)
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
            try
            {
                if (isRunning) return;
                isRunning = true;

                GameStata = EGameState.CDToStart;
                //room.BroadcastAll(NetworkMessage.Create_GameStateChange((byte)EGameState.CDToStart));
                Console.WriteLine("[ServerGameManager] 倒计时开始");

                for (int i = (int)CD_DURATION; i > 0 && !ct.IsCancellationRequested; i--)
                {
                    room.BroadcastAll(NetworkMessage.Create_GameStartCD(i));
                    Console.WriteLine($"[ServerGameManager] 开局倒计时: {i}");
                    await Task.Delay(1000, ct);
                }

                GameStata = EGameState.GamePlaying;
                // 新一局：重置订单与分数，并立即生成第一单
                //room.DeliveryManager.StartNewGame();
                //room.BroadcastAll(NetworkMessage.Create_GameStateChange((byte)EGameState.GamePlaying));
                Console.WriteLine("[ServerGameManager] 游戏开始");

                RemainingTime = GAME_DURATION;
                while (RemainingTime > 0 && !ct.IsCancellationRequested)
                {
                    room.BroadcastAll(NetworkMessage.Create_TimerSync(RemainingTime));
                    await Task.Delay(1000, ct);
                    RemainingTime -= 1f;
                }

                GameStata = EGameState.GameOver;
                room.BroadcastAll(NetworkMessage.Crteate_GameOver());
                //room.BroadcastAll(NetworkMessage.Create_GameStateChange((byte)EGameState.GameOver));
                Console.WriteLine("[ServerGameManager] 游戏结束");
            }
            catch (TaskCanceledException ex)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerGameManager] 游戏循环出现了问题：{ex.Message}");
                return;
            }
        }
    }
}