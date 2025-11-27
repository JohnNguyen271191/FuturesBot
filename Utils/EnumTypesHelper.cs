using System;
namespace FuturesBot.Utils
{
    public static class EnumTypesHelper
    {
        public enum SignalType
        {
            None,
            Long,
            Short,
            Close,
            Info,
            CloseShort,
            CloseLong
        }
    }
}
