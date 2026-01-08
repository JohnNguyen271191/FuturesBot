using FuturesBot.Models;
using static FuturesBot.Utils.EnumTypesHelper;

namespace FuturesBot.IServices
{
    public interface IModeProfileProvider
    {
        ModeProfile Get(TradeMode mode);
    }
}
