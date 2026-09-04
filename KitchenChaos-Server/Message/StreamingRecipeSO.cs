namespace KitchenChaos_Server.Message
{
    public class StreamingRecipeSO
    {
        public int input;
        public int output;
        public float streamingMaxTimer;

        public StreamingRecipeSO(int _input, int _output, float _needCutNum)
        {
            input = _input;
            output = _output;
            streamingMaxTimer = _needCutNum;
        }
    }
}
