namespace Recon.Core.Interfaces;

public interface IIntegrationService
{
    void StartIntegration(IProgress<int> progress = null);
    
    Task StopIntegration();
    
    void SetFastBuild(bool isFastBuild);
}