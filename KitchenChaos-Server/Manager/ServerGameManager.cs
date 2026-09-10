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
        WaitingForFirstDelivery = 1,// 等待第一次交付
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

        /// <summary>线程安全的游戏状态：游戏循环写，消息线程读（判大厅/可开局等）。</summary>
        private volatile int gameStata = (int)EGameState.WaitingForPlayers;
        public EGameState GameStata
        {
            get => (EGameState)gameStata;
            private set => gameStata = (int)value;
        }

        public float RemainingTime { get; private set; }

        /// <summary>
        /// 是否正在运行游戏循环（幂等保护，避免多次启动）。
        /// </summary>
        private bool isRunning;

        private const float CD_DURATION = 3f;
        private const float GAME_DURATION = 30f;

        /// <summary>台子/订单推进频率：约 20 次/秒（0.05s）</summary>
        private const float TICK_INTERVAL = 0.05f;

        private readonly object readyLock = new object();

        private CancellationTokenSource cts;
        private CancellationToken Ct => cts.Token;

        private bool hasStarted;

        /// <summary>
        /// 当前状态持续的时间
        /// </summary>
        private float stateTimer;

        /// <summary>
        /// 上次广播的倒计时秒数
        /// </summary>
        private int lastBroadcastCd = -1;

        /// <summary>
        /// 上次广播的剩余秒数
        /// </summary>
        private int lastBroadcastTime = -1;

        public ServerGameManager(ServerRoom _room)
        {
            room = _room;
        }

        /// <summary>
        /// 启动游戏主循环（幂等，只启动一次）。
        /// </summary>
        public void StartGame()
        {
            lock (readyLock)
            {
                if (hasStarted) return;
                hasStarted = true;
                cts = new CancellationTokenSource();    
            }
            _ = RunGameLoopAsync();
        }

        public void Clear()
        {
            lock (readyLock)
            {
                cts?.Cancel();
                hasStarted = false;
                isRunning = false;
            }
        }

        public void SetGameState(EGameState state)
        {
            GameStata = state;
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

                while (!Ct.IsCancellationRequested)
                {
                    room.CounterManager.UpdateAll(TICK_INTERVAL);
                    room.RoomDeliveryManager.Update(TICK_INTERVAL);

                    UpdateGameStat(TICK_INTERVAL);

                    await Task.Delay((int)(TICK_INTERVAL * 1000), Ct);
                }
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerGameManager] 游戏循环出现了问题：{ex.Message}");
                return;
            }
        }

        private void UpdateGameStat(float _delay)
        {
            switch (GameStata)
            {
                case EGameState.WaitingForPlayers:
                    break; // 等外部触发（见第三节）

                case EGameState.CDToStart:
                    stateTimer += _delay;
                    int cd = (int)Math.Ceiling(CD_DURATION - stateTimer);
                    if (cd > 0 && cd != lastBroadcastCd)
                    {
                        lastBroadcastCd = cd;
                        room.BroadcastAll(NetworkMessage.Create_GameStartCD(cd));
                    }
                    if (stateTimer >= CD_DURATION)
                    {
                        stateTimer = 0f;
                        lastBroadcastTime = -1;
                        SetGameState(EGameState.GamePlaying);
                        Console.WriteLine("[ServerGameManager] 游戏开始");
                    }
                    break;

                case EGameState.GamePlaying:
                    stateTimer += _delay;
                    RemainingTime = Math.Max(0f, GAME_DURATION - stateTimer);
                    int rem = (int)Math.Ceiling(RemainingTime);
                    if (rem != lastBroadcastTime)
                    {
                        lastBroadcastTime = rem;
                        room.BroadcastAll(NetworkMessage.Create_TimerSync(RemainingTime));
                    }
                    if (stateTimer >= GAME_DURATION)
                    {
                        SetGameState(EGameState.GameOver);
                    }
                    break;

                case EGameState.GameOver:
                    //cts.Cancel();// 停止当前的线程
                    SetGameState(EGameState.WaitingForPlayers);
                    room.BroadcastAll(NetworkMessage.Crteate_GameOver(room.RoomDeliveryManager.SuccessfulRecipesAmount));
                    Console.WriteLine("[ServerGameManager] 游戏结束");
                    break; // 终态
            }
        }
    }
}