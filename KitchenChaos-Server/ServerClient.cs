using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using KitchenChaos_Server.Message;
using KitchenChaos_Server.Room;

namespace KitchenChaos_Server
{
    /// <summary>
    /// 代表一个已连接的客户端
    /// 负责从该客户端的网络流中读取消息，并向它发送消息
    /// </summary>
    public class ServerClient
    {
        public ServerPlayer Player { get; set; }

        private readonly TcpClient tcpClient;
        private readonly GameServer server;
        private readonly NetworkStream stream;

        public bool IsConnected { get; private set; }

        /// <summary>
        /// 接受缓冲区，存还没处理完的字节
        /// </summary>
        private byte[] receiveBuffer = new byte[4096];
        private int bufferedBytes = 0;// 缓冲区中当前有效的字节数

        public ServerRoom CurrentRoom { get; set; }

        public DateTime LastReceiveTimeUtc { get; private set; } = DateTime.UtcNow;

        /// <summary>
        /// 该客户端是否为调试客户端（编辑器 / 开发版，由心跳标志位标记）。
        /// 调试客户端在断点暂停期间会停止发送心跳，因此使用更宽松的保活超时。
        /// </summary>
        public bool IsDebugClient { get; private set; }

        private readonly object sendLock = new object();

        public ServerClient(int _playerId, TcpClient _client, GameServer _server)
        {
            Player = new ServerPlayer(_playerId,this);

            tcpClient = _client;
            server = _server;
            stream = tcpClient.GetStream();
            IsConnected = true;
        }

        /// <summary>
        /// 开始异步接受消息
        /// </summary>
        public void BeginReceive()
        {
            _ = ReceiveLoopAsync();
        }

        public void MarkReceive()
        {
            LastReceiveTimeUtc = DateTime.UtcNow;
        }   

        /// <summary>
        /// 应用心跳消息体中的标志位（bit0 = 调试客户端）。
        /// 与客户端 NetworkMessage.HeartbeatFlag_DebugClient 保持一致。
        /// </summary>
        /// <param name="_flags">心跳消息体第 1 个字节</param>
        /// <returns>本次调用是否使该客户端首次被标记为调试客户端</returns>
        public bool MarkHeartbeatFlags(byte _flags)
        {
            if ((_flags & 0x01) == 0 || IsDebugClient) return false;

            IsDebugClient = true;
            return true;
        }

        /// <summary>
        /// 接收循环：持续从网络流中读取数据，解析成完整消息后交给 GameServer 处理
        /// TCP 粘包/拆包的处理逻辑：
        /// 1. 先把收到的字节追加到缓冲区
        /// 2. 检查缓冲区是否至少有 5 个字节（一个完整的消息头）
        /// 3. 读取消息体长度，检查缓冲区是否够一条完整消息
        /// 4. 够的话就取出完整消息，处理它，然后继续检查剩下的字节
        /// </summary>
        private async Task ReceiveLoopAsync()
        {
            byte[] tempBuffer = new byte[4096];

            while (IsConnected)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(tempBuffer, 0, tempBuffer.Length);

                    if (bytesRead == 0)
                    {
                        Console.WriteLine($"[ServerClient] 玩家 {Player.PlayerId} 连接已关闭");
                        break;
                    }

                    // 把新收到的数据加入到缓冲区
                    EnsureBufferCapacity(bufferedBytes + bytesRead);
                    Array.Copy(tempBuffer, 0, receiveBuffer, bufferedBytes, bytesRead);
                    bufferedBytes += bytesRead;

                    // 尝试从缓冲区中解析出完整的消息
                    while (bufferedBytes >= NetworkMessage.HEADER_SIZE)
                    {
                        // 读取消息头
                        var (bodyLength, type) = NetworkMessage.ReadHeader(receiveBuffer, 0);

                        int totalLength = NetworkMessage.HEADER_SIZE + bodyLength;

                        if (bufferedBytes < totalLength)
                        {
                            break;
                        }

                        // 提取消息体
                        byte[] body = new byte[bodyLength];
                        Array.Copy(receiveBuffer, NetworkMessage.HEADER_SIZE, body, 0, bodyLength);

                        MarkReceive();

                        // 交给GameServer处理
                        server.OnMessageReceived(this, type, body);

                        // 把已经处理的数据从缓冲区移除（将剩余数据前移）
                        int remaing = bufferedBytes - totalLength;
                        if (remaing > 0)
                        {
                            Array.Copy(receiveBuffer, totalLength, receiveBuffer, 0, remaing);
                        }
                        bufferedBytes = remaing;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (System.IO.IOException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ServerClient] 玩家 {Player.PlayerId} 接受出错：{ex.Message}");
                    break;
                }
            }

            IsConnected = false;
            server.OnClientDisconnected(this);
        }

        /// <summary>
        /// 给这个客户端发送一条消息
        /// </summary>
        /// <param name="data"></param>
        public void Send(byte[] data)
        {
            if (!IsConnected) return;

            try
            {
                lock (sendLock)
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ServerClient] 玩家 {Player.PlayerId} 发送失败：{ex.Message}");
                Disconnect();
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;

            try
            {
                stream?.Close();
                tcpClient.Close();
            }
            catch
            {
                //忽略
            }
        }

        /// <summary>
        /// 确保接收缓冲区足够大。
        /// </summary>
        /// <param name="_requiredSize"></param>
        private void EnsureBufferCapacity(int _requiredSize)
        {
            if (receiveBuffer.Length < _requiredSize)
            {
                int newSize = receiveBuffer.Length * 2;
                while (newSize < _requiredSize)
                {
                    newSize *= 2;
                }

                byte[] newBuffer = new byte[newSize];
                Array.Copy(receiveBuffer,newBuffer,bufferedBytes);
                receiveBuffer = newBuffer;
            }
        }
    }
}