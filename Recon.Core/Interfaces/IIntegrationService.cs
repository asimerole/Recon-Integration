namespace Recon.Core.Interfaces;

public interface IIntegrationService
{
    void StartIntegration(IProgress<int> progress = null);
    
    void StopIntegration();
    
    void SetFastBuild(bool isFastBuild);
}