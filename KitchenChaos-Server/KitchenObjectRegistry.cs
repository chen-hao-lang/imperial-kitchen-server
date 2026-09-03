using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KitchenChaos_Server
{
    public static class KitchenObjectRegistry
    {
        private static readonly string[] ItemNames = new string[] {
            "Fish",
            "Fish_Slice",
            "FishSoup",
            "Garlic",
            "Garlic_Slice",
            "Ginger",
            "Ginger_Slice",
            "Onion",
            "Onion_Slice",
            "XiaoLongBao_Steamed",
            "XiaoLongBao_UnStream",
            "Plate",
            "Basin"
        };
        private static Dictionary<int,string> KitchenObjects = new Dictionary<int,string>();

        public static int Count => ItemNames.Length;
        private static bool isRegister = false;

        public static string GetName(int _index)
        {
            if (_index < 0 || _index > Count)
            {
                return "UnKnown";
            }
            return ItemNames[_index];
        }

        public static bool IsValid(int _index)
        {
            return _index >= 0 && _index < Count;
        }

        public static void RegisterKitchenObject(List<(int,string)> _kitchenObjects)
        {
            if (isRegister) return;

            isRegister = true;

            foreach (var kt in _kitchenObjects)
            {
                if (!KitchenObjects.ContainsKey(kt.Item1))
                {
                    KitchenObjects.Add(kt.Item1,kt.Item2);
                }
            }
        }
    }
}