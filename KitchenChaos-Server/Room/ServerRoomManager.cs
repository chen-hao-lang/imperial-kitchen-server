using System.Collections.Generic;

namespace KitchenChaos_Server.Room
{
    public class ServerRoomManager
    {
        public Dictionary<int, ServerRoom> rooms;
        public Dictionary<int, int> playerRoomMap;

        public void CreateRoom(string _roomName,int _maxPlayers)
        {
            
        }

        public void JoinRoom(int _roomId, ServerClient _client)
        {
            
        }

        public void LeaveRoom(int _playerId)
        {
            
        }

        public void GetRoomByPlayerId(int _playerId)
        {
            
        }

        public void GetRoom(int _roomId)
        {
            
        }

        public void RemoveRoom(int _roomId)
        {
            
        }

        private void GenerateRoomId()
        {
            
        }
    }
}
