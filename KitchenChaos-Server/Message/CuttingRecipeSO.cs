namespace KitchenChaos_Server.Message
{
    public struct CuttingRecipeSO
    {
        public int input;
        public int output;
        public int cuttingPrograssMax;

        public CuttingRecipeSO(int _input,int _output,int _needCutNum)
        {
            input = _input;
            output = _output;
            cuttingPrograssMax = _needCutNum;
        }
    }
}