using Recon.Core.Interfaces;

namespace Recon.Core.Services;

public class IntegrationService : IIntegrationService
{
    private int _percentage = 0;
    
    public string GetIntegrationPercentage()
    {
        return $"Прогрес інтеграції: {_percentage}%"; 
    }
}