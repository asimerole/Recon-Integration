using Recon.Core.Enums;

namespace Recon.Core.Interfaces;

public interface IStatisticsService
{
    void RegisterAction(ServiceType type, int count = 1);
    
    (int Last2Hours, int Today) GetStats(ServiceType type);
    
    void SetDailyCountFromDb(ServiceType type, int dbCount);
}