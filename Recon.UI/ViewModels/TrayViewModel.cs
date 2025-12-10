using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dapper;
using Microsoft.Data.SqlClient;
using ILogger = Serilog.ILogger;
using Microsoft.Extensions.Logging;
using Recon.Core.Interfaces;
using Recon.Core.Options;
using Recon.Core.Services;


namespace Recon.UI.ViewModels;

public partial class TrayViewModel : ObservableObject
{
    private readonly IIntegrationService _integrationService;
    private readonly IFtpService _ftpService;
    private readonly IMailService _mailService;
    private readonly IOneDriveService _oneDriveService;
    private readonly IDatabaseService _dbService;

    private bool _dbIsFull = false;
    private bool _isFastBuild = false;
    
    [ObservableProperty]
    private string _version;
    
    [ObservableProperty]
    private string _progress;
    
    [ObservableProperty]
    private bool _isFtpActive;
    
    [ObservableProperty]
    private bool _isIntegrationActive;
    
    [ObservableProperty]
    private bool _isMailActive;
    
    [ObservableProperty]
    private bool _isOneDriveActive;

    public TrayViewModel(IFtpService ftpService, IMailService mailService, IOneDriveService oneDriveService, IIntegrationService integrationService, IDatabaseService dbService)
    {
        _ftpService = ftpService;
        _mailService = mailService;
        _oneDriveService = oneDriveService;
        _integrationService = integrationService;
        _dbService = dbService;

        IsFtpActive = false;
        IsIntegrationActive = false;
        IsMailActive = false;
        IsOneDriveActive = false;
        Version = "v. 1.0.18 від 09.12.2025";
        Progress = _integrationService.GetIntegrationPercentage();
        LoadModuleStates();

        RunModules();
    }

    private void RunModules()
    {
        if (IsFtpActive)
        {
            _ftpService.StartFTP();
        }

        if (IsIntegrationActive)
        {
            // run integration
        }

        if (IsMailActive)
        {
            // run mail
        }

        if (IsOneDriveActive)
        {
            // run oneDrive
        }
        
        _oneDriveService.StartCleanupScheduler();
    }

    [RelayCommand]
    private void ChangeFtpStatus()
    {
        if (IsFtpActive)
        {
            SetFtpState(false);
            _ftpService.StopFTP();
        }
        else
        {
            SetFtpState(true);
            _ftpService.StartFTP();
        }
    }
    
    [RelayCommand]
    private void ChangeOneDriveStatus()
    {
        if (IsOneDriveActive)
        {
            SetOneDriveState(false);
            // _ftpService.Stop();
        }
        else
        {
            SetOneDriveState(true);
            //_ftpService.StartFtpService();
        }
    }
    
    [RelayCommand]
    private void ChangeIntegrationStatus()
    {
        if (IsIntegrationActive)
        {
            SetIntegrationState(false);
            // _ftpService.Stop();
        }
        else
        {
            SetIntegrationState(true);
            //_ftpService.StartFtpService();
        }
    }
    
    [RelayCommand]
    private void ChangeMailStatus()
    {
        if (IsMailActive)
        {
            SetMailState(false);
            // _ftpService.Stop();
        }
        else
        {
            SetMailState(true);
            //_ftpService.StartFtpService();
        }
    }
    
    [RelayCommand]
    private void NotifyAll()
    {
        
    }
    
    [RelayCommand]
    private void ExitApp()
    {
        Application.Current.Shutdown();
    }
    
    // methods
    private void SetFtpState(bool state)
    {
        IsFtpActive = state; 
    }

    private void SetIntegrationState(bool state)
    {
        IsIntegrationActive = state; 
    }

    private void SetMailState(bool state)
    {
        IsMailActive = state; 
    }

    private void SetOneDriveState(bool state)
    {
        IsOneDriveActive = state; 
    }

    private void LoadModuleStates()
    {
        var config = _dbService.GetModuleConfig();
        
        IsFtpActive = config.IsFtpActive;
        IsIntegrationActive = config.IsIntegrationActive;
        IsMailActive = config.IsMailActive;
        IsOneDriveActive = config.IsOneDriveActive;
    }

    private void SaveCurrentState()
    {
        var config = new ModuleConfig
        {
            IsFtpActive = IsFtpActive,
            IsIntegrationActive = IsIntegrationActive,
            IsMailActive = IsMailActive,
            IsOneDriveActive = IsOneDriveActive,
        };

        _dbService.SaveModuleConfig(config);
    }
    
    partial void OnIsFtpActiveChanged(bool value) => SaveCurrentState();
    
    partial void OnIsIntegrationActiveChanged(bool value) => SaveCurrentState();
    
    partial void OnIsMailActiveChanged(bool value) => SaveCurrentState();
    
    partial void OnIsOneDriveActiveChanged(bool value) =>  SaveCurrentState();
    
}