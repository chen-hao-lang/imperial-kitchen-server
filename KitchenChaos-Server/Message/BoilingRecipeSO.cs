using System.Collections.Generic;

namespace KitchenChaos_Server.Message
{
    public struct BoilingRecipeSO
    {
        public List<int> inputs;
        public int output;
        public float maxBoilingTimer;

        public BoilingRecipeSO(List<int> _inputs,int _outout,float _maxBoilingTimer)
        {
            inputs = _inputs;
            output = _outout;
            maxBoilingTimer = _maxBoilingTimer;
        }
    }
}
