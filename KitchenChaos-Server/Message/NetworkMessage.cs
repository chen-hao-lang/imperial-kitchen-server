using KitchenChaos_Server.Room;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace KitchenChaos_Server.Message
{
    public static class NetworkMessage
    {
        public const int HEADER_SIZE = 5;

        #region 辅助函数
        /// <summary>
        /// 从缓冲区读取消息头
        /// </summary>
        /// <param name="_buffer"></param>
        /// <param name="_offset"></param>
        /// <returns>
        /// （消息体长度，消息类型）
        /// </returns>
        public static (int bodyLength, MessageType type) ReadHeader(byte[] _buffer, int _offset)
        {
            int bodyLength = BitConverter.ToInt32(_buffer, _offset);
            MessageType type = (MessageType)_buffer[_offset + 4];
            return (bodyLength, type);
        }

        private static void WriteString(BinaryWriter _writer, string _value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_value ?? "");
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }

        private static string ReadString(BinaryReader _reader)
        {
            int length = _reader.ReadInt32();
            byte[] bytes = _reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Pack(MessageType _type, byte[] _body)
        {
            byte[] result = new byte[HEADER_SIZE + _body.Length];
            BitConverter.GetBytes(_body.Length).CopyTo(result, 0);
            result[4] = (byte)_type;
            _body.CopyTo(result, HEADER_SIZE);
            return result;
        }
        #endregion

        #region 服务端->客户端
        /// <summary>
        /// 向客户端发送连接成功的消息
        /// </summary>
        /// <param name="_playerId">由服务器分配的id</param>
        /// <param name="_connectedPlayers">当前已经连接的客户端的玩家id</param>
        /// <returns></returns>
        public static byte[] Create_Connect(int _playerId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_playerId);
            return Pack(MessageType.Connect, ms.ToArray());
        }

        public static byte[] Create_PlayerLeft(int _playerId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_playerId);
            return Pack(MessageType.PlayerLeft, ms.ToArray());
        }

        public static byte[] Create_PlayerPosition(int _playerId, float _x, float _y, float _z, float _rotY)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_playerId);
            writer.Write(_x);
            writer.Write(_y);
            writer.Write(_z);
            writer.Write(_rotY);
            return Pack(MessageType.PlayerPosition, ms.ToArray());
        }

        /// <summary>
        /// 玩家准备就绪（服务端→客户端广播）。
        /// </summary>
        public static byte[] Create_PlayerReady(int _playerId, bool _isReady)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_playerId);
            writer.Write(_isReady ? 1 : 0);
            return Pack(MessageType.PlayerReady, ms.ToArray());
        }

        /// <summary>
        /// 创建玩家手上物品的消息体。
        /// </summary>
        /// <param name="_playerId"></param>
        /// <param name="_kitchenObjectSOIndex"></param>
        /// <returns></returns>
        /// <summary>
        /// 交互同步：合并了原来的 Create_PlayerHeldItem + Create_CounterStateUpdate。
        /// 一次交互就是一次原子性的物品转移，操作台和玩家状态同时变化。
        /// </summary>
        public static byte[] Create_InteractionSync(int _counterId, int _counterItem, int _playerId, int _playerItem)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_counterId);
            writer.Write(_counterItem);
            writer.Write(_playerId);
            writer.Write(_playerItem);
            return Pack(MessageType.InteractionSync, ms.ToArray());
        }

        public static byte[] Crteate_GameOver()
        {
            var ms = new MemoryStream();
            return Pack(MessageType.GameOver, ms.ToArray());
        }

        public static byte[] Create_TimerSync(float _remaingTime)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_remaingTime);
            return Pack(MessageType.GameingTimerSync, ms.ToArray());
        }

        /// <summary>
        /// 新订单出现（服务端→客户端广播）。
        /// 携带配方在 RecipeListSO 中的索引，客户端用本地配方表解析。
        /// </summary>
        public static byte[] Create_RecipeSpawned(int _recipeIndex)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_recipeIndex);
            return Pack(MessageType.RecipeSpawned, ms.ToArray());
        }

        public static byte[] Create_RecipeCompleted(int _recipeIndex)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_recipeIndex);
            return Pack(MessageType.RecipeCompleted, ms.ToArray());
        }

        /// <summary>
        /// 交付失败。
        /// </summary>
        public static byte[] Create_RecipeFailed()
        {
            return Pack(MessageType.RecipeFailed, Array.Empty<byte>());
        }

        /// <summary>
        /// 分数更新。
        /// </summary>
        public static byte[] Create_ScoreUpdate(int _successfulRecipes)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_successfulRecipes);
            return Pack(MessageType.ScoreUpdate, ms.ToArray());
        }

        /// <summary>
        /// 开局倒计时（秒）。
        /// </summary>
        public static byte[] Create_GameStartCD(float _countdown)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_countdown);
            return Pack(MessageType.GameStartCD, ms.ToArray());
        }

        /// <summary>
        /// 全量同步：新玩家加入时，发送所有玩家位置、台子状态、所有盘子和等待中的订单。
        /// 格式：playerCount(int) + [playerId(int) + nameLen(int) + name(bytes) + x/y/z/rotY(float*4)] * N
        ///      + counterCount(int) + [counterId(int) + itemIndex(int)] * N
        ///      + plateCount(int) + [plateId(int) + ownerType(byte) + ownerId(int) + soIndex(int) + ingredientCount(int) + [ingredientIndex(int)]*N] * M
        ///      + recipeCount(int) + [recipeIndex(int)] * K
        /// </summary>
        public static byte[] Create_FullSync(
            (int id, string name, float x, float y, float z, float rotY)[] players,
            (int counterId, int itemIndex)[] counters,
            (int plateId, byte ownerType, int ownerId, int soIndex, List<int> ingredients)[] plates = null,
            int[] recipeIndexes = null)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);

            // 玩家信息
            writer.Write(players.Length);
            foreach (var p in players)
            {
                writer.Write(p.id);
                WriteString(writer, p.name);
                writer.Write(p.x);
                writer.Write(p.y);
                writer.Write(p.z);
                writer.Write(p.rotY);
            }

            // 台子信息
            writer.Write(counters.Length);
            foreach (var c in counters)
            {
                writer.Write(c.counterId);
                writer.Write(c.itemIndex);
            }

            // 盘子信息
            if (plates == null)
            {
                plates = new (int, byte, int, int, List<int>)[0];
            }
            writer.Write(plates.Length);
            foreach (var p in plates)
            {
                writer.Write(p.plateId);
                writer.Write(p.ownerType);
                writer.Write(p.ownerId);
                writer.Write(p.soIndex);
                writer.Write(p.ingredients.Count);
                foreach (var ing in p.ingredients)
                {
                    writer.Write(ing);
                }
            }

            // 等待中的订单（服务器权威：中途加入的玩家需要恢复订单状态）
            if (recipeIndexes == null)
            {
                recipeIndexes = new int[0];
            }
            writer.Write(recipeIndexes.Length);
            foreach (var recipeIndex in recipeIndexes)
            {
                writer.Write(recipeIndex);
            }

            return Pack(MessageType.FullSync, ms.ToArray());
        }

        /// <summary>
        /// 操作台进度同步（切菜/煎/煮的进度条）。
        /// </summary>
        public static byte[] Create_CounterProgressSync(int _counterId, float _progressNormalized)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_counterId);
            writer.Write(_progressNormalized);
            return Pack(MessageType.CounterProgressSync, ms.ToArray());
        }

        /// <summary>
        /// 操作台状态变更
        /// </summary>
        /// <param name="_counterId"></param>
        /// <param name="_state"></param>
        /// <returns></returns>
        public static byte[] Create_CounterStateSync(int _counterId, byte _state)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_counterId);
            writer.Write(_state);
            return Pack(MessageType.CounterStateSync, ms.ToArray());
        }
        
        /// <summary>
        /// 操作台添加配料
        /// </summary>
        /// <param name="_counterId"></param>
        /// <param name="_ingredients"></param>
        /// <returns></returns>
        public static byte[] Create_CounterAddIngredient(int _counterId, int _ingredients)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_counterId);
            writer.Write(_ingredients);
            return Pack(MessageType.CounterAddIngredient,ms.ToArray());
        }

        #region 盘子相关
        /// <summary>
        /// 通知客户端盘子的拥有者发生改变
        /// </summary>
        public static byte[] Create_PlateSpawned(int _counterId,int _plateId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_counterId);
            writer.Write(_plateId);
            return Pack(MessageType.PlateSpawned, ms.ToArray());
        }

        /// <summary>
        /// 盘子持有者变更广播。
        /// </summary>

        public static byte[] Create_PlateOwnerSync(ServerPlate _plate)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_plate.PlateId);
            writer.Write(_plate.OwnerType);
            writer.Write(_plate.OwnerId);
            writer.Write(_plate.CurrentIngredients.Count);
            foreach (var ing in _plate.CurrentIngredients)
            {
                writer.Write(ing);
            }
            return Pack(MessageType.PlateOwnerSync, ms.ToArray());
        }

        public static byte[] Create_PlateAddIngredient(int _plateId, int _ingredientIndex)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_plateId);
            writer.Write(_ingredientIndex);
            return Pack(MessageType.PlateAddIngredient, ms.ToArray());
        }

        public static byte[] Create_PlateRemoveIngredient(int _plateId, int _ingredientIndex)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_plateId);
            writer.Write(_ingredientIndex);
            return Pack(MessageType.PlateRemoveIngredient, ms.ToArray());
        }

        public static byte[] Create_PlateDestroy(int _plateId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_plateId);
            return Pack(MessageType.PlateDestroy, ms.ToArray());
        }


        public static byte[] Create_RemotePlate(int _counterId, int _plateId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_counterId);
            writer.Write(_plateId);
            return Pack(MessageType.RemotePlate, ms.ToArray());
        }
        #endregion

        /// <summary>
        /// 聊天消息广播（服务端 → 客户端）。
        /// 格式：playerId(int) + messageLen(int) + message(bytes)
        /// </summary>
        public static byte[] Create_SendMessage(int _playerId, string _message)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_playerId);
            WriteString(writer, _message);
            return Pack(MessageType.SendMessage, ms.ToArray());
        }
        #endregion

        #region 服务端<-客户端
        /// <summary>
        /// 客户端连接消息：解析出玩家名字。
        /// </summary>
        public static string Parse_Connect(byte[] body)
        {
            var ms = new MemoryStream(body);
            var reader = new BinaryReader(ms);
            return ReadString(reader);
        }

        public static (int,bool) Parse_PlayerReady(byte[] _body)
        {
            var ms = new MemoryStream(_body);    
            var reader = new BinaryReader(ms);
            int playerId = reader.ReadInt32();
            bool isReady = reader.ReadInt32() ==1 ? true : false;
            return (playerId,isReady);
        }

        /// <summary>
        /// 玩家移动消息：解析出位置和朝向。
        /// </summary>
        public static (float x, float y, float z, float rotY) Parse_PlayerMove(byte[] body)
        {
            var ms = new MemoryStream(body);
            var reader = new BinaryReader(ms);
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float rotY = reader.ReadSingle();
            return (x, y, z, rotY);
        }

        /// <summary>
        /// 玩家交互消息：解析出交互的台子ID。
        /// </summary>
        public static (int playerId, int counterId) Parse_PlayerInteract(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int playerId = reader.ReadInt32();
            int counterId = reader.ReadInt32();
            return (playerId, counterId);
        }

        /// <summary>
        /// 玩家副交互消息：解析出交互的台子ID。
        /// </summary>
        public static int Parse_InteractAlternate(byte[] body)
        {
            var ms = new MemoryStream(body);
            var reader = new BinaryReader(ms);
            return reader.ReadInt32();
        }

        /// <summary>
        /// 解析客户端上报的操作台类型映射。
        /// 格式：counterCount(int) + [counterId(int) + counterType(byte)] * N
        /// </summary>
        public static (int counterId, byte counterType)[] Parse_CounterRegister(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int count = reader.ReadInt32();
            var result = new (int counterId, byte counterType)[count];
            for (int i = 0; i < count; i++)
            {
                int id = reader.ReadInt32();
                byte type = reader.ReadByte();
                result[i] = (id, type);
            }
            return result;
        }

        /// <summary>
        /// 解析容器固定物品注册：counterId(int) + soIndex(int)
        /// </summary>
        public static (int,int) Parse_RegisterContainerItem(byte[] _body)
        {
            var ms = new MemoryStream(_body);
            var reader = new BinaryReader(ms);
            int counterId = reader.ReadInt32();
            int soIndex = reader.ReadInt32();
            return (counterId, soIndex);
        }

        public static (int,List<BoilingRecipeSO>) Parse_RegisterBoilingRecipes(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int counterId = reader.ReadInt32();
            int count = reader.ReadInt32();
            List<BoilingRecipeSO> recipes = new List<BoilingRecipeSO>();

            for (int i = 0; i < count; i++)
            {
                // 读取inputs
                int inputCount = reader.ReadInt32();
                List<int> inputs = new List<int>();
                for (int j = 0; j < inputCount; j++)
                {
                    inputs.Add(reader.ReadInt32());
                }

                BoilingRecipeSO so = new BoilingRecipeSO(
                    inputs,
                    reader.ReadInt32(),// 读取output
                    reader.ReadSingle()// 读取煮沸时长
                    );

                recipes.Add(so);
            }

            return (counterId,recipes);
        }

        public static (int,List<CuttingRecipeSO>) Parse_RegisterCuttingRecipes(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int counterId = reader.ReadInt32(); 
            int count = reader.ReadInt32();
            List<CuttingRecipeSO> recipes = new List<CuttingRecipeSO>();

            for (int i = 0; i < count; i++)
            {
                CuttingRecipeSO so = new CuttingRecipeSO(
                    reader.ReadInt32(),// 读取inputs
                    reader.ReadInt32(),// 读取outputs
                    reader.ReadInt32()// 需要需要砍几刀
                    );

                recipes.Add(so);
            }

            return (counterId,recipes);
        }

        public static (int, int) Parse_RegisterPlateSpawnId(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int counterId = reader.ReadInt32();
            int plateId = reader.ReadInt32();
            return (counterId,plateId);
        }

        /// <summary>
        /// 解析客户端上报的配方表。
        /// 格式：recipeCount(int) + [ingredientCount(int) + [ingredientIndex(int)]*N] * recipeCount
        /// 返回：每个配方 = 配料索引列表（KitchenObjectRegistry 索引空间，顺序与客户端 RecipeListSO 一致）。
        /// </summary>
        public static List<int[]> Parse_RegisterRecipes(byte[] _body)
        {
            var ms = new MemoryStream(_body);
            var reader = new BinaryReader(ms);

            int recipeCount = reader.ReadInt32();
            var recipes = new List<int[]>(recipeCount);
            for (int i = 0; i < recipeCount; i++)
            {
                int ingredientCount = reader.ReadInt32();
                var ingredients = new int[ingredientCount];
                for (int j = 0; j < ingredientCount; j++)
                {
                    ingredients[j] = reader.ReadInt32();
                }
                recipes.Add(ingredients);
            }
            return recipes;
        }

        public static (int,int) Parse_RegisterPlateSO(byte[] _body)
        {
            var ms = new MemoryStream(_body);
            var reader = new BinaryReader(ms);
            int plateId = reader.ReadInt32();
            int SOId = reader.ReadInt32();
            return (plateId, SOId);
        }

        public static (int counterId, int plateId) Parse_RemotePlate(byte[] body)
        {
            var ms = new MemoryStream(body);
            var reader = new BinaryReader(ms);
            int counterId = reader.ReadInt32();
            int plateId = reader.ReadInt32();
            return (counterId, plateId);
        }

        /// <summary>
        /// 解析客户端上报的聊天消息。
        /// 格式：playerId(int) + messageLen(int) + message(bytes)
        /// </summary>
        public static (int playerId, string message) Parse_SendMessage(byte[] _body)
        {
            var ms = new MemoryStream(_body);
            var reader = new BinaryReader(ms);
            int playerId = reader.ReadInt32();
            string message = ReadString(reader);
            return (playerId, message);
        }
        #endregion

        #region Room
        public static byte[] Create_RoomCreated(int _roomId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_roomId);
            return Pack(MessageType.RoomCreated,ms.ToArray());
        }

        public static byte[] Create_RoomJoined(int _roomId, int _seadId,int _playerId,string _playerName)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);  

            writer.Write(_roomId);
            writer.Write(_seadId);
            writer.Write(_playerId);
            WriteString(writer,_playerName);

            return Pack(MessageType.RoomJoined,ms.ToArray());
        }

        public static byte[] Create_JoinedFailed(ERoomJoinFailReason _reason)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write((byte)_reason);
            return Pack(MessageType.RoomJoinFailed, ms.ToArray());
        }

        public static byte[] Create_RoomPlayerListSync(List<ServerPlayer> _list)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);

            int count = _list.Count;
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                writer.Write(_list[i].SeatId);
                writer.Write(_list[i].PlayerId);
                WriteString(writer, _list[i].Name);
                writer.Write(_list[i].IsReady ? 1:0);
            }

            return Pack(MessageType.RoomPlayerListSync, ms.ToArray());
        }

        public static byte[] Create_RoomDestroyed(int _roomId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_roomId);
            return Pack(MessageType.RoomDestroyed, ms.ToArray());
        }

        /// <summary>
        /// 返回当前的服务器拥有的房间列表
        /// </summary>
        /// <param name="_roomList"></param>
        /// <returns></returns>
        public static byte[] Create_RequestRoomList(List<(int,string,int,int)> _roomList)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_roomList.Count);
            foreach (var room in _roomList)
            {
                writer.Write(room.Item1);
                WriteString(writer,room.Item2);
                writer.Write(room.Item3);
                writer.Write(room.Item4);
            }
            return Pack(MessageType.RequestRoomList,ms.ToArray());
        }

        public static byte[] Create_StartGame(int _roomId)
        {
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(_roomId);
            return Pack(MessageType.StartGame, ms.ToArray());
        }

        #region 解析
        public static (int maxPlayers,string roomName) Parse_CreateRoom(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int maxPlayers = reader.ReadInt32();
            string name = ReadString(reader);
            return (maxPlayers,name);
        }

        public static int Parse_JoinRoom(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            return reader.ReadInt32();
        }

        public static int Parse_DestroyRoom(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int roomId = reader.ReadInt32();
            return roomId;
        }

        public static int Parse_StartGame(byte[] _data)
        {
            var ms = new MemoryStream(_data);
            var reader = new BinaryReader(ms);
            int roomId = reader.ReadInt32();
            return roomId;
        }
        #endregion
        #endregion
    }
}