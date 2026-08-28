using System;
using System.Collections.Generic;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server
{

    public class ServerDeliveryManager
    {
        private ServerRoom room;
        private readonly object stateLock = new object();

        /// <summary>当前等待交付的配方索引列表（服务器权威）</summary>
        private readonly List<int> waitingRecipeIndexes = new List<int>();

        /// <summary>累计成功交付数量（分数）</summary>
        private int successfulRecipesAmount;

        private float spawnRecipeTimer;
        private const float SPAWN_RECIPE_INTERVAL = 4f;
        private const int MAX_WAITING_RECIPES = 4;

        private bool isFirstDelivered = false;

        public ServerDeliveryManager(ServerRoom _room)
        {
            room = _room;
        }

        /// <summary>当前等待中的配方索引（线程安全快照，用于 FullSync）</summary>
        public int[] GetWaitingRecipeIndexes()
        {
            lock (stateLock)
            {
                return waitingRecipeIndexes.ToArray();
            }
        }

        /// <summary>累计成功交付数量（线程安全）</summary>
        public int SuccessfulRecipesAmount
        {
            get
            {
                lock (stateLock)
                {
                    return successfulRecipesAmount;
                }
            }
        }

        /// <summary>
        /// 新一局开始时调用：清空订单与分数，并立即生成第一单。
        /// </summary>
        public void StartGame()
        {
            lock (stateLock)
            {
                waitingRecipeIndexes.Clear();
                successfulRecipesAmount = 0;
                spawnRecipeTimer = 0f;
            }

            //TrySpawnRecipe();
            Console.WriteLine("[ServerDeliveryManager] 新一局开始，订单与分数已重置");
        }

        /// <summary>
        /// 每帧更新（仅游戏进行中被 GameServer 调用）：按间隔生成新订单。
        /// </summary>
        /// <param name="_deltaTime">帧间隔（秒）</param>
        public void Update(float _deltaTime)
        {
            lock (stateLock)
            {
                spawnRecipeTimer -= _deltaTime;
                if (spawnRecipeTimer > 0f) return;
                spawnRecipeTimer = SPAWN_RECIPE_INTERVAL;
            }

            TrySpawnRecipe();
        }

        /// <summary>
        /// 尝试生成一个新订单并广播（超过最大等待数则跳过；配方表未注册时跳过）。
        /// </summary>
        private void TrySpawnRecipe()
        {
            lock (stateLock)
            {
                if (waitingRecipeIndexes.Count >= MAX_WAITING_RECIPES) return;
            }

            // 客户端尚未上报配方表 → 无法生成订单
            if (!RecipeRegistry.IsRegistered) return;

            int recipeIndex = RecipeRegistry.RandomRecipeIndex();
            if (recipeIndex < 0) return;

            lock (stateLock)
            {
                waitingRecipeIndexes.Add(recipeIndex);
            }

            room.BroadcastAll(NetworkMessage.Create_RecipeSpawned(recipeIndex));
            Console.WriteLine($"[ServerDeliveryManager] 新订单: 配方{recipeIndex}({RecipeRegistry.GetName(recipeIndex)}), 当前等待={waitingRecipeIndexes.Count}/{MAX_WAITING_RECIPES}");
        }

        /// <summary>
        /// 处理客户端交付请求（服务器权威校验）。
        /// 校验通过 → 移除订单、加分、广播 RecipeCompleted + ScoreUpdate；
        /// 校验失败 → 广播 RecipeFailed。
        /// 无论成功失败，交付的盘子都会被销毁（DeliveryCounter 会消费盘子）。
        /// </summary>
        /// <param name="_plateId">客户端提交的盘子 ID</param>
        public void HandleDeliveryRequest(int _plateId)
        {
            // 从服务器权威的盘子登记表中取配料数据，不信任客户端上报的配料
            if (!room.TryGetPlateIngredients(_plateId, out List<int> plateIngredients))
            {
                Console.WriteLine($"[ServerDeliveryManager] 交付失败: 服务器中不存在盘子 {_plateId}");
                room.BroadcastAll(NetworkMessage.Create_RecipeFailed());
                return;
            }

            Console.WriteLine($"[ServerDeliveryManager] 收到交付请求: 盘子{_plateId} 配料=[{string.Join(",", plateIngredients)}]");

            bool matched = false;
            int completedRecipeIndex = -1;
            int newScore = 0;
            int remainingCount = 0;

            // 锁内只做状态变更；广播放到锁外执行，
            // 保证「先 room 锁、后 stateLock」的全局锁顺序，避免与 ServerRoom 互相等待而死锁。
            lock (stateLock)
            {
                for (int i = 0; i < waitingRecipeIndexes.Count; i++)
                {
                    int recipeIndex = waitingRecipeIndexes[i];

                    if (RecipeRegistry.Matches(recipeIndex, plateIngredients))
                    {
                        if (!isFirstDelivered)// 第一次提交，让游戏开始计时
                        {
                            isFirstDelivered = true;

                            room.RoomGameManager.StartGame();
                        }

                        waitingRecipeIndexes.RemoveAt(i);
                        successfulRecipesAmount++;
                        matched = true;
                        completedRecipeIndex = recipeIndex;
                        newScore = successfulRecipesAmount;
                        remainingCount = waitingRecipeIndexes.Count;
                        break;
                    }
                }
            }

            if (completedRecipeIndex >= 0)
            {
                // 广播订单完成 + 分数
                room.BroadcastAll(NetworkMessage.Create_RecipeCompleted(completedRecipeIndex));
                room.BroadcastAll(NetworkMessage.Create_ScoreUpdate(newScore));

                Console.WriteLine($"[ServerDeliveryManager] 交付成功! 配方{completedRecipeIndex}({RecipeRegistry.GetName(completedRecipeIndex)}), 累计={newScore}, 剩余订单={remainingCount}");
            }

            // DeliveryCounter 一定会消费盘子：无论成功失败都销毁
            room.DestroyPlate(_plateId);

            if (!matched)
            {
                room.BroadcastAll(NetworkMessage.Create_RecipeFailed());
                Console.WriteLine($"[ServerDeliveryManager] 交付失败: 盘子{_plateId} 无匹配订单");
            }
        }
    }
}
